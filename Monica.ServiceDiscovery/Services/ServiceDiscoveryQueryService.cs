using Monica.Core.Domains.Abstractions;
using Monica.ApplicationModel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Services;

public sealed class ServiceDiscoveryQueryService(IServiceProvider serviceProvider)
{
    public async Task<List<RegisteredServiceStatus>> GetServicesStatusAsync()
    {
        return ConvertToRegisteredServiceStatus(await GetRegisteredInstancesAsync());
    }

    public async Task<List<RegisteredServiceStatus>> GetMergedServicesStatusAsync(CancellationToken cancellationToken = default)
    {
        var registeredServices = ConvertToRegisteredServiceStatus(await GetRegisteredInstancesAsync(cancellationToken));
        // The catalog contract has no cancellation parameter. Drain its read, then reject retired evidence.
        var predefinedServices = await GetCatalogProvider().GetPreloadedServicesAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var mergedServices = new List<RegisteredServiceStatus>(registeredServices);

        foreach (var predefinedService in predefinedServices)
        {
            var exists = registeredServices.Any(service =>
                string.Equals(service.AppId, predefinedService.AppId, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                continue;
            }

            mergedServices.Add(new RegisteredServiceStatus
            {
                AppId = predefinedService.AppId,
                AppName = predefinedService.AppName ?? predefinedService.AppId
            });
        }

        return mergedServices;
    }

    public Task<List<DomainInfo>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(serviceProvider.GetRequiredService<IDomainCatalog>().Domains.Values.OrderBy(domain => domain.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>Reads static API usage evidence without requiring online instances or inspecting current traffic.</summary>
    public ApplicationTopologySnapshot GetApplicationTopology()
    {
        var model = serviceProvider.GetService<IApplicationModel>();
        if (model is null) return new(false, []);
        var dependencies = model.Dependencies.Select(dependency =>
        {
            var role = model.Roles.Values.SingleOrDefault(role => role.AssemblyName == dependency.SourceAssemblyName);
            var domain = role?.DomainId ?? model.Domains.Values.Single(domain => domain.AssemblyName == dependency.SourceAssemblyName).Id;
            var target = model.GetOperation(dependency.RequestType);
            return new DomainDependencyInfo(domain, role?.Id, target.DomainId, target.RoleId,
                dependency.RequestType.FullName ?? dependency.RequestType.Name, dependency.SourceTypeName);
        }).ToArray();
        return new(true, Array.AsReadOnly(dependencies));
    }

    public async Task<DomainDetailInfo> GetDomainDetailAsync(string domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
        {
            throw new ArgumentException("Domain name is required.", nameof(domainName));
        }

        var domains = await GetDomainsAsync();
        var domain = domains.FirstOrDefault(item =>
            string.Equals(item.Id, domainName, StringComparison.Ordinal));

        if (domain is null)
        {
            throw new KeyNotFoundException($"Domain not found: {domainName}");
        }

        var topology = GetApplicationTopology();
        var dependencies = topology.Dependencies.Where(dependency => dependency.TargetDomainId == domain.Id).ToArray();
        return new DomainDetailInfo
        {
            Domain = domain,
            HasDependencyEvidence = topology.IsAvailable,
            Dependencies = dependencies,
            RelatedServices = await GetDomainRelatedServicesAsync(domain.Id, dependencies)
        };
    }

    public async Task<LeaderState?> GetLeaderStateAsync()
    {
        return await GetStateManager().GetLeaderStateAsync();
    }

    public async Task<CurrentInstanceSnapshot> GetCurrentInstanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var clientInfo = GetClientInfo();
        var leaderService = GetLeaderService();

        var lease = await GetStateManager().GetLeaderStateAsync(cancellationToken);
        return new CurrentInstanceSnapshot
        {
            CurrentInstance = ServiceDiscoverySnapshot.Copy(clientInfo.GetServiceStatus(isHeartbeatInfo: false)),
            IsLeaderElectionEnabled = serviceProvider.GetRequiredService<IOptions<ModuleServiceDiscoveryOption>>().Value.Role
                is ServiceDiscoveryRole.Registry or ServiceDiscoveryRole.Standalone,
            CurrentLeaderStatus = leaderService.CurrentStatus,
            IsLeader = leaderService.IsLeader,
            LeaderBecomeTime = leaderService.LeaderBecomeTime,
            CurrentETag = leaderService.CurrentETag,
            ClusterLeaderState = lease is null ? null : new LeaderState
            {
                AppId = lease.AppId, InstanceId = lease.InstanceId, BecomeLeaderTime = lease.BecomeLeaderTime
            }
        };
    }

    public async Task<LeaderStatusResponse> GetRegistryLeaderStatusAsync()
    {
        var leaderService = GetLeaderService();
        var currentInstance = GetClientInfo().GetServiceStatus();

        return new LeaderStatusResponse
        {
            Status = leaderService.CurrentStatus,
            LeaderInstanceId = leaderService.IsLeader ? currentInstance.InstanceId : null,
            LeaderRegistrationTime = leaderService.LeaderBecomeTime,
            RunningInstanceCount = 1,
            Message = leaderService.IsLeader ? "当前实例是 Leader" : "当前实例不是 Leader"
        };
    }

    public async Task<RegistryServiceStatusResponse> GetRegistryServiceStatusAsync()
    {
        var leaderService = GetLeaderService();
        var currentInstance = GetClientInfo().GetServiceStatus();
        var registeredInstances = await GetRegisteredInstancesAsync();
        var leaderState = await GetStateManager().GetLeaderStateAsync();

        return new RegistryServiceStatusResponse
        {
            CurrentInstance = new RegistryCurrentInstanceStatus
            {
                InstanceInfo = currentInstance,
                IsLeader = leaderService.IsLeader,
                LeaderStatus = leaderService.CurrentStatus.ToString(),
                LeaderBecomeTime = leaderService.LeaderBecomeTime
            },
            RegisteredInstances = registeredInstances,
            LeaderInfo = leaderState is null
                ? null
                : new RegistryLeaderInfo
                {
                    InstanceId = leaderState.InstanceId,
                    BecomeLeaderTime = leaderState.BecomeLeaderTime,
                    AppId = leaderState.AppId
                }
        };
    }

    public async Task<bool> ReleaseLeaderAsync()
    {
        var leaderService = GetLeaderService();
        if (!leaderService.IsLeader)
        {
            return false;
        }

        leaderService.TriggerLeaderLost(LeaderLostReason.GracefulShutdown);
        await GetStateManager().DeleteLeaderKeyAsync();
        return true;
    }

    public async Task ForceDeleteLeaderAsync(string appId)
    {
        await GetStateManager().ForceDeleteLeaderKeyAsync(appId);
    }

    public Task<List<InstanceState>> GetRegisteredInstancesAsync(CancellationToken cancellationToken = default)
    {
        return GetStateManager().GetAllInstancesAsync(cancellationToken);
    }

    private async Task<List<RegisteredServiceStatus>> GetDomainRelatedServicesAsync(string domainName, IReadOnlyList<DomainDependencyInfo> dependencies)
    {
        var services = await GetMergedServicesStatusAsync();

        return services
            .Where(service =>
                service.BelongsToDomain(domainName) ||
                dependencies.Any(dependency => dependency.IsOwnedBy(service)))
            .ToList();
    }

    private static List<RegisteredServiceStatus> ConvertToRegisteredServiceStatus(List<InstanceState> instances)
    {
        return instances
            .GroupBy(instance => instance.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var firstInstance = group.First();
                return new RegisteredServiceStatus
                {
                    AppId = firstInstance.AppId,
                    AppName = firstInstance.AppName,
                    DomainName = firstInstance.DomainName,
                    DomainNames = group.SelectMany(instance => instance.GetDomainNames()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    RoleIds = group.SelectMany(instance => instance.RoleIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    ProjectName = firstInstance.ProjectName,
                    Instances = group.ToDictionary(
                        instance => instance.InstanceId,
                        instance =>
                        {
                            // An unexpired registration represents membership, not a health probe.
                            var captured = ServiceDiscoverySnapshot.Copy(instance);
                            captured.Status = ServiceStatus.Running;
                            return captured;
                        })
                };
            })
            .ToList();
    }

    private IRegistrationStateManager GetStateManager()
        => serviceProvider.GetService<IRegistrationStateManager>()
           ?? throw new InvalidOperationException("Service discovery state manager is not configured.");

    private IServiceDiscoveryCatalogProvider GetCatalogProvider()
        => serviceProvider.GetService<IServiceDiscoveryCatalogProvider>()
           ?? throw new InvalidOperationException("Service discovery catalog provider is not configured.");

    private IServiceDiscoveryClientInfo GetClientInfo()
        => serviceProvider.GetService<IServiceDiscoveryClientInfo>()
           ?? throw new InvalidOperationException("Service discovery client info is not configured.");

    private ILeaderElectionService GetLeaderService()
        => serviceProvider.GetService<ILeaderElectionService>()
           ?? throw new InvalidOperationException("Service discovery leader election service is not configured.");
}
