using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monica.DevOps.K8S.Abstractions;
using Monica.DevOps.K8S.Exceptions;
using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.Services.Support;

namespace Monica.DevOps.K8S.Services;

/// <summary>
/// Discovers cluster resources and details using one detached runtime configuration snapshot per operation.
/// </summary>
public class K8SClusterService(
    IK8SRuntimeConfigStore runtimeConfigStore,
    K8SResourceDiscoveryService discoveryService,
    IK8SProvider provider,
    ILogger<K8SClusterService> logger)
{
    public Task<K8SRuntimeConfig> GetRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtimeConfigStore.GetCurrent());
    }

    public Task UpdateRuntimeConfigAsync(K8SRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(runtimeConfig.ExecutionNode))
        {
            throw K8SOperationException.ExecutionNodeRequired();
        }

        if (string.IsNullOrWhiteSpace(runtimeConfig.UserName))
        {
            throw K8SOperationException.SshUserNameRequired();
        }

        runtimeConfigStore.Update(runtimeConfig);
        logger.LogInformation(
            "Updated K8S runtime configuration. Execution node: {ExecutionNode}. Namespace scope count: {NamespaceCount}. Restriction count: {RestrictionCount}",
            runtimeConfig.ExecutionNode,
            runtimeConfig.NamespaceScope.Count,
            runtimeConfig.SensitiveOperationNameRestrictions.Count);

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<K8SNamespaceInfo>> ListNamespacesAsync(CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();

        using var document = await ExecuteJsonAsync(runtimeConfig, "get namespaces -o json", cancellationToken);
        var actualNamespaces = document.RootElement.TryGetProperty("items", out var items)
            ? items.EnumerateArray()
                .Select(item => item.GetProperty("metadata").GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (runtimeConfig.NamespaceScope.Count == 0)
        {
            return actualNamespaces
                .Select(namespaceName => new K8SNamespaceInfo
                {
                    Name = namespaceName,
                    Exists = true,
                    IsInScope = true
                })
                .ToList();
        }

        var actualSet = actualNamespaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return runtimeConfig.NamespaceScope
            .Select(namespaceName => new K8SNamespaceInfo
            {
                Name = namespaceName,
                Exists = actualSet.Contains(namespaceName),
                IsInScope = true
            })
            .ToList();
    }

    public async Task<IReadOnlyList<K8SServiceSummary>> ListServicesAsync(string namespaceName, CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);
        return await discoveryService.ListServiceSummariesAsync(runtimeConfig, namespaceName, cancellationToken);
    }

    public async Task<K8SResourceListResult> ListResourcesAsync(string namespaceName, K8SResourceType resourceType, CancellationToken cancellationToken = default)
    {
        return await ListResourcesAsync(runtimeConfigStore.GetCurrent(), namespaceName, resourceType, cancellationToken);
    }

    /// <summary>
    /// Reads every supported resource kind and its Pod and Service relationships in a namespace.
    /// One detached configuration is reused for the entire read; failed or canceled reads return no partial capture.
    /// </summary>
    public async Task<K8SNamespaceInventoryResult> ListNamespaceInventoryAsync(
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);
        var services = await discoveryService.ListServiceSummariesAsync(runtimeConfig, namespaceName, cancellationToken);
        var workloads = await discoveryService.ListWorkloadsAsync(runtimeConfig, namespaceName,
            K8SResourceDiscoveryService.RestartableWorkloadTypes, cancellationToken);
        var pods = await discoveryService.ListPodsForWorkloadsAsync(runtimeConfig, namespaceName, workloads, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        workloads = K8SPodWorkloadBinder.ApplyPodStartFallback(workloads, pods);

        var items = new List<K8SResourceSummary>();
        var workloadServices = new Dictionary<(string Kind, string Name, string Namespace), List<K8SServiceSummary>>();
        foreach (var service in services)
        {
            var backingWorkloads = K8SPodWorkloadBinder.ResolveServiceWorkloads(service, workloads, failOnMissing: false);
            items.Add(ToResourceSummary(service, backingWorkloads, pods));
            foreach (var workload in backingWorkloads)
            {
                var key = (workload.Kind, workload.Name, workload.Namespace);
                if (!workloadServices.TryGetValue(key, out var relatedServices))
                    workloadServices[key] = relatedServices = [];
                relatedServices.Add(service);
            }
        }
        items.AddRange(workloads.Select(workload => ToResourceSummary(workload, pods,
            workloadServices.GetValueOrDefault((workload.Kind, workload.Name, workload.Namespace)))));

        return new K8SNamespaceInventoryResult
        {
            Namespace = namespaceName,
            Items = items,
            FetchedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task<K8SResourceListResult> ListResourcesAsync(
        K8SRuntimeConfig runtimeConfig,
        string namespaceName,
        K8SResourceType resourceType,
        CancellationToken cancellationToken)
    {
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);

        var items = resourceType == K8SResourceType.Service
            ? await BuildServiceResourceSummariesAsync(runtimeConfig, namespaceName, cancellationToken)
            : (await discoveryService.ListWorkloadsAsync(runtimeConfig, namespaceName, [resourceType], cancellationToken, includePodStartFallback: true))
            .Select(ToResourceSummary)
            .ToList();

        return new K8SResourceListResult
        {
            Namespace = namespaceName,
            ResourceType = resourceType,
            Items = items,
            FetchedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<K8SServiceDetails> DescribeServiceAsync(string namespaceName, string serviceName, CancellationToken cancellationToken = default)
    {
        return await DescribeServiceAsync(runtimeConfigStore.GetCurrent(), namespaceName, serviceName, cancellationToken);
    }

    private async Task<K8SServiceDetails> DescribeServiceAsync(
        K8SRuntimeConfig runtimeConfig,
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken)
    {
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);

        var services = await discoveryService.ListServiceSummariesAsync(runtimeConfig, namespaceName, cancellationToken);
        var service = services.FirstOrDefault(item => string.Equals(item.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw K8SOperationException.ServiceNotFound(serviceName, namespaceName);

        var workloads = await discoveryService.ListWorkloadsAsync(
            runtimeConfig,
            namespaceName,
            K8SResourceDiscoveryService.RestartableWorkloadTypes,
            cancellationToken,
            includePodStartFallback: true);
        var description = await provider.ExecuteKubectlAsync(
            runtimeConfig,
            $"describe service {KubectlCommandBuilder.ShellQuote(serviceName)} -n {KubectlCommandBuilder.ShellQuote(namespaceName)}",
            cancellationToken);

        var backingWorkloads = K8SPodWorkloadBinder.ResolveServiceWorkloads(service, workloads, failOnMissing: false);
        var relatedPods = await discoveryService.ListPodsForWorkloadsAsync(runtimeConfig, namespaceName, backingWorkloads, cancellationToken);

        return new K8SServiceDetails
        {
            Summary = service,
            DescriptionText = description.Trim(),
            BackingWorkloads = backingWorkloads,
            RelatedPods = relatedPods
        };
    }

    public async Task<K8SResourceDetails> DescribeResourceAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);

        var listResult = await ListResourcesAsync(runtimeConfig, namespaceName, resourceType, cancellationToken);
        var summary = listResult.Items.FirstOrDefault(item => string.Equals(item.Name, resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw K8SOperationException.ResourceNotFound(resourceType, resourceName, namespaceName);

        if (resourceType == K8SResourceType.Service)
        {
            var serviceDetails = await DescribeServiceAsync(runtimeConfig, namespaceName, resourceName, cancellationToken);
            return new K8SResourceDetails
            {
                Summary = summary,
                DescriptionText = serviceDetails.DescriptionText,
                RelatedWorkloads = serviceDetails.BackingWorkloads,
                RelatedPods = serviceDetails.RelatedPods
            };
        }

        var description = await provider.ExecuteKubectlAsync(
            runtimeConfig,
            $"describe {resourceType.ToKubectlResource()} {KubectlCommandBuilder.ShellQuote(resourceName)} -n {KubectlCommandBuilder.ShellQuote(namespaceName)}",
            cancellationToken);

        var relatedWorkloads = new List<K8SWorkloadReference> { ToWorkloadReference(summary) };
        var relatedPods = await discoveryService.ListPodsForWorkloadsAsync(runtimeConfig, namespaceName, relatedWorkloads, cancellationToken);

        return new K8SResourceDetails
        {
            Summary = summary,
            DescriptionText = description.Trim(),
            RelatedWorkloads = relatedWorkloads,
            RelatedPods = relatedPods
        };
    }

    public async Task<K8SPodDetails> DescribePodAsync(string namespaceName, string podName, CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        runtimeConfig.EnsureNamespaceAllowed(namespaceName);

        using var document = await ExecuteJsonAsync(
            runtimeConfig,
            $"get pod {KubectlCommandBuilder.ShellQuote(podName)} -n {KubectlCommandBuilder.ShellQuote(namespaceName)} -o json",
            cancellationToken);

        var summary = KubectlJsonReader.ReadPodSummary(document.RootElement);
        var description = await provider.ExecuteKubectlAsync(
            runtimeConfig,
            $"describe pod {KubectlCommandBuilder.ShellQuote(podName)} -n {KubectlCommandBuilder.ShellQuote(namespaceName)}",
            cancellationToken);

        return new K8SPodDetails
        {
            Summary = summary,
            DescriptionText = description.Trim()
        };
    }

    private async Task<List<K8SResourceSummary>> BuildServiceResourceSummariesAsync(
        K8SRuntimeConfig runtimeConfig,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        var services = await discoveryService.ListServiceSummariesAsync(runtimeConfig, namespaceName, cancellationToken);
        var workloads = await discoveryService.ListWorkloadsAsync(
            runtimeConfig,
            namespaceName,
            K8SResourceDiscoveryService.RestartableWorkloadTypes,
            cancellationToken,
            includePodStartFallback: true);

        return services
            .Select(service => ToResourceSummary(service,
                K8SPodWorkloadBinder.ResolveServiceWorkloads(service, workloads, failOnMissing: false)))
            .ToList();
    }

    private async Task<JsonDocument> ExecuteJsonAsync(
        K8SRuntimeConfig runtimeConfig,
        string kubectlArguments,
        CancellationToken cancellationToken)
    {
        var json = await provider.ExecuteKubectlAsync(runtimeConfig, kubectlArguments, cancellationToken);
        return KubectlJsonReader.ParseDocument(json);
    }

    private static K8SResourceSummary ToResourceSummary(
        K8SServiceSummary service,
        IReadOnlyCollection<K8SWorkloadReference> backingWorkloads,
        IReadOnlyCollection<K8SPodSummary>? pods = null)
    {
        var lastRestartedAtUtc = backingWorkloads
            .Select(workload => workload.LastRestartedAtUtc)
            .Where(restartedAt => restartedAt.HasValue)
            .OrderByDescending(restartedAt => restartedAt!.Value)
            .FirstOrDefault();

        return new K8SResourceSummary
        {
            ResourceType = K8SResourceType.Service,
            Kind = K8SResourceType.Service.ToDisplayName(),
            Name = service.Name,
            Namespace = service.Namespace,
            Type = service.Type,
            ClusterIp = service.ClusterIp,
            Selector = service.Selector,
            Ports = service.Ports,
            Images = backingWorkloads.SelectMany(workload => workload.Images).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DesiredReplicas = backingWorkloads.Sum(workload => workload.DesiredReplicas),
            ReadyReplicas = backingWorkloads.Sum(workload => workload.ReadyReplicas),
            LastRestartedAtUtc = lastRestartedAtUtc,
            RestartSupported = true,
            RelatedWorkloads = backingWorkloads.ToList(),
            RelatedPods = GetRelatedPods(backingWorkloads, pods)
        };
    }

    private static K8SResourceSummary ToResourceSummary(K8SWorkloadReference workload) =>
        ToResourceSummary(workload, null, null);

    private static K8SResourceSummary ToResourceSummary(K8SWorkloadReference workload,
        IReadOnlyCollection<K8SPodSummary>? pods, List<K8SServiceSummary>? services)
    {
        var resourceType = workload.Kind switch
        {
            "Deployment" => K8SResourceType.Deployment,
            "StatefulSet" => K8SResourceType.StatefulSet,
            "DaemonSet" => K8SResourceType.DaemonSet,
            _ => throw K8SOperationException.UnsupportedWorkloadKind(workload.Kind)
        };

        return new K8SResourceSummary
        {
            ResourceType = resourceType,
            Kind = workload.Kind,
            Name = workload.Name,
            Namespace = workload.Namespace,
            Type = workload.Kind,
            Selector = workload.Labels,
            Images = workload.Images,
            DesiredReplicas = workload.DesiredReplicas,
            ReadyReplicas = workload.ReadyReplicas,
            LastRestartedAtUtc = workload.LastRestartedAtUtc,
            RestartSupported = true,
            RelatedWorkloads = [workload],
            RelatedServices = services ?? [],
            RelatedPods = GetRelatedPods([workload], pods)
        };
    }

    private static List<K8SPodSummary> GetRelatedPods(IReadOnlyCollection<K8SWorkloadReference> workloads,
        IReadOnlyCollection<K8SPodSummary>? pods) => pods?.Where(pod => workloads.Any(workload =>
            string.Equals(pod.Namespace, workload.Namespace, StringComparison.Ordinal) &&
            string.Equals(pod.WorkloadKind, workload.Kind, StringComparison.Ordinal) &&
            string.Equals(pod.WorkloadName, workload.Name, StringComparison.Ordinal))).ToList() ?? [];

    private static K8SWorkloadReference ToWorkloadReference(K8SResourceSummary summary)
    {
        return new K8SWorkloadReference
        {
            Kind = summary.Kind,
            Name = summary.Name,
            Namespace = summary.Namespace,
            DesiredReplicas = summary.DesiredReplicas,
            ReadyReplicas = summary.ReadyReplicas,
            LastRestartedAtUtc = summary.LastRestartedAtUtc,
            Labels = summary.Selector,
            Images = summary.Images
        };
    }
}
