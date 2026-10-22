using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Services;
using Monica.Core.Results;

namespace Monica.ServiceDiscovery.Facades;

public sealed class ServiceDiscoveryFacade(
    ServiceDiscoveryQueryService queryService,
    IOptions<ModuleServiceDiscoveryOption> options,
    IStringLocalizer<ServiceDiscoveryResource> localizer,
    ILogger<ServiceDiscoveryFacade> logger)
{
    private readonly ModuleServiceDiscoveryOption _option = options.Value;

    /// <summary>
    /// Captures detached current registrations and predefined services. Failed reads return an error,
    /// rather than a successful empty registry. Cancellation prevents the UI from adopting retired work.
    /// </summary>
    public Task<Res<List<RegisteredServiceStatus>>> CaptureServicesAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(async () => (await queryService.GetMergedServicesStatusAsync(cancellationToken))
            .Select(ServiceDiscoverySnapshot.Copy).ToList(), cancellationToken);

    /// <summary>Captures detached catalog entries. A successful empty catalog remains distinct from a failed read.</summary>
    public Task<Res<List<DomainInfo>>> CaptureDomainsAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(async () => (await queryService.GetDomainsAsync(cancellationToken)).Select(domain => new DomainInfo
        {
            Id = domain.Id, DisplayName = domain.DisplayName, Description = domain.Description
        }).ToList(), cancellationToken);

    /// <summary>Captures querying-host identity and local election evidence alongside the independently read application lease.</summary>
    public Task<Res<CurrentInstanceSnapshot>> CaptureCurrentInstanceAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(() => queryService.GetCurrentInstanceSnapshotAsync(cancellationToken), cancellationToken);

    /// <summary>Captures compiled API usage separately from live registration and transport evidence.</summary>
    public Task<Res<ApplicationTopologySnapshot>> CaptureTopologyAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(() => Task.FromResult(queryService.GetApplicationTopology()), cancellationToken);

    private async Task<Res<T>> CaptureAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            var captured = await read();
            cancellationToken.ThrowIfCancellationRequested();
            return Res.Ok(captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogError(exception, "Failed to capture discovery evidence.");
            return Res.Fail(localizer["Workbench:ReadFailed"].Value);
        }
    }

    public async Task<Res<List<RegisteredServiceStatus>>> GetServicesStatusAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetServicesStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get service status.");
            return Res.Fail(localizer["Service:Errors:GetServicesStatusFailed", ex.Message].Value);
        }
    }

    public async Task<Res<LeaderStatusResponse>> GetRegistryLeaderStatusAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetRegistryLeaderStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get registry leader status.");
            return Res.Fail($"获取 Leader 状态失败: {ex.Message}");
        }
    }

    public async Task<Res<RegistryServiceStatusResponse>> GetRegistryServiceStatusAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetRegistryServiceStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get registry service status.");
            return Res.Fail($"获取服务状态失败: {ex.Message}");
        }
    }

    public async Task<Res> ReleaseLeaderAsync()
    {
        try
        {
            if (!await queryService.ReleaseLeaderAsync())
            {
                return Res.Fail("当前实例不是 Leader");
            }

            return Res.Ok("已释放 Leader 状态");
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to release leader status.");
            return Res.Fail($"释放 Leader 状态失败: {ex.Message}");
        }
    }

    public Res<ElectionConfig> GetElectionConfig()
    {
        return Res.Ok(_option.Election);
    }

    public async Task<Res<List<RegisteredServiceStatus>>> GetMergedServicesStatusAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetMergedServicesStatusAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get merged service status.");
            return Res.Fail(localizer["Service:Errors:GetMergedServicesStatusFailed", ex.Message].Value);
        }
    }

    public async Task<Res<List<DomainInfo>>> GetDomainsAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetDomainsAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:InfoProviderNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get service discovery domains.");
            return Res.Fail(localizer["Service:Errors:GetDomainsFailed", ex.Message].Value);
        }
    }

    public async Task<Res<DomainDetailInfo>> GetDomainDetailAsync(string domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
        {
            return Res.Fail(localizer["Service:Errors:DomainNameRequired"].Value);
        }

        try
        {
            return Res.Ok(await queryService.GetDomainDetailAsync(domainName));
        }
        catch (KeyNotFoundException)
        {
            return Res.Fail(localizer["Service:Errors:DomainNotFound", domainName].Value);
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:InfoProviderNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get domain detail for {DomainName}.", domainName);
            return Res.Fail(localizer["Service:Errors:GetDomainDetailFailed", ex.Message].Value);
        }
    }

    public async Task<Res<LeaderState?>> GetLeaderStateAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetLeaderStateAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get leader state.");
            return Res.Fail(localizer["Service:Errors:GetLeaderStateFailed", ex.Message].Value);
        }
    }

    public async Task<Res<CurrentInstanceSnapshot>> GetCurrentInstanceSnapshotAsync()
    {
        try
        {
            return Res.Ok(await queryService.GetCurrentInstanceSnapshotAsync());
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["CurrentInstance:Errors:ClientInfoNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get current instance snapshot.");
            return Res.Fail(localizer["CurrentInstance:Errors:LoadFailed", ex.Message].Value);
        }
    }

    public async Task<Res> ForceDeleteLeaderAsync(string appId)
    {
        try
        {
            await queryService.ForceDeleteLeaderAsync(appId);
            return Res.Ok();
        }
        catch (InvalidOperationException)
        {
            return Res.Fail(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to force delete leader for application {AppId}.", appId);
            return Res.Fail(localizer["Service:Errors:ForceDeleteLeaderFailed", ex.Message].Value);
        }
    }
}
