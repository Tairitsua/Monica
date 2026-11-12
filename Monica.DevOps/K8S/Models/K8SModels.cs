using System.Text;
using Monica.DevOps.K8S.Exceptions;

namespace Monica.DevOps.K8S.Models;

public enum K8SProviderKind
{
    SshRemoteKubectl
}

public enum K8SResourceType
{
    Service,
    Deployment,
    StatefulSet,
    DaemonSet
}

/// <summary>
/// Describes a supported workload replica scaling operation.
/// </summary>
public enum K8SScaleOperation
{
    /// <summary>
    /// Records the current desired replica count and scales the workload to zero replicas.
    /// </summary>
    ScaleDown,

    /// <summary>
    /// Restores the recorded desired replica count, or one replica when no valid record exists.
    /// </summary>
    ScaleUp
}

public static class K8SResourceTypeExtensions
{
    public static bool TryParse(string? value, out K8SResourceType resourceType)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "service":
            case "services":
                resourceType = K8SResourceType.Service;
                return true;
            case "deployment":
            case "deployments":
                resourceType = K8SResourceType.Deployment;
                return true;
            case "statefulset":
            case "statefulsets":
                resourceType = K8SResourceType.StatefulSet;
                return true;
            case "daemonset":
            case "daemonsets":
                resourceType = K8SResourceType.DaemonSet;
                return true;
            default:
                return Enum.TryParse(value, ignoreCase: true, out resourceType);
        }
    }

    public static string ToDisplayName(this K8SResourceType resourceType)
    {
        return resourceType switch
        {
            K8SResourceType.Service => "Service",
            K8SResourceType.Deployment => "Deployment",
            K8SResourceType.StatefulSet => "StatefulSet",
            K8SResourceType.DaemonSet => "DaemonSet",
            _ => resourceType.ToString()
        };
    }

    public static string ToPluralDisplayName(this K8SResourceType resourceType)
    {
        return resourceType switch
        {
            K8SResourceType.Service => "Services",
            K8SResourceType.Deployment => "Deployments",
            K8SResourceType.StatefulSet => "StatefulSets",
            K8SResourceType.DaemonSet => "DaemonSets",
            _ => resourceType.ToString()
        };
    }

    public static string ToKubectlResource(this K8SResourceType resourceType)
    {
        return resourceType switch
        {
            K8SResourceType.Service => "service",
            K8SResourceType.Deployment => "deployment",
            K8SResourceType.StatefulSet => "statefulset",
            K8SResourceType.DaemonSet => "daemonset",
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null)
        };
    }
}

public class K8SRuntimeConfig
{
    public K8SProviderKind Provider { get; set; } = K8SProviderKind.SshRemoteKubectl;

    public List<string> MasterNodes { get; set; } = [];

    public List<string> WorkerNodes { get; set; } = [];

    public string ExecutionNode { get; set; } = string.Empty;

    public string UserName { get; set; } = "root";

    public string Password { get; set; } = string.Empty;

    public List<string> NamespaceScope { get; set; } = [];

    public List<string> SensitiveOperationNameRestrictions { get; set; } = [];

    public K8SRuntimeConfig Clone()
    {
        return new K8SRuntimeConfig
        {
            Provider = Provider,
            MasterNodes = [.. MasterNodes],
            WorkerNodes = [.. WorkerNodes],
            ExecutionNode = ExecutionNode,
            UserName = UserName,
            Password = Password,
            NamespaceScope = [.. NamespaceScope],
            SensitiveOperationNameRestrictions = [.. SensitiveOperationNameRestrictions]
        };
    }

    public K8SRuntimeConfig Normalize()
    {
        MasterNodes = NormalizeList(MasterNodes);
        WorkerNodes = NormalizeList(WorkerNodes);
        NamespaceScope = NormalizeList(NamespaceScope);
        SensitiveOperationNameRestrictions = NormalizeList(SensitiveOperationNameRestrictions);
        ExecutionNode = ExecutionNode.Trim();
        UserName = UserName.Trim();

        if (string.IsNullOrWhiteSpace(ExecutionNode))
        {
            ExecutionNode = MasterNodes.FirstOrDefault() ?? WorkerNodes.FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            UserName = "root";
        }

        return this;
    }

    public void ValidateForConnection()
    {
        if (string.IsNullOrWhiteSpace(ExecutionNode))
        {
            throw K8SOperationException.ExecutionNodeRequired();
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            throw K8SOperationException.SshUserNameRequired();
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            throw K8SOperationException.SshPasswordRequired();
        }
    }

    public void EnsureNamespaceAllowed(string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            throw K8SOperationException.NamespaceRequired();
        }

        if (NamespaceScope.Count == 0)
        {
            return;
        }

        if (NamespaceScope.Contains(namespaceName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw K8SOperationException.NamespaceOutsideScope(namespaceName);
    }

    public bool IsSensitiveOperationAllowed(string candidateName, out List<string> matchedKeywords)
    {
        matchedKeywords = [];

        if (SensitiveOperationNameRestrictions.Count == 0)
        {
            return true;
        }

        matchedKeywords = SensitiveOperationNameRestrictions
            .Where(keyword => candidateName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matchedKeywords.Count > 0;
    }

    public static List<string> ParseMultiValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        return rawValue
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToMultilineText(IEnumerable<string> values)
    {
        return string.Join(Environment.NewLine, NormalizeList(values));
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        var combined = values == null
            ? string.Empty
            : string.Join(Environment.NewLine, values);

        return ParseMultiValue(combined);
    }
}

public class K8SNamespaceInfo
{
    public string Name { get; init; } = string.Empty;

    public bool Exists { get; init; } = true;

    public bool IsInScope { get; init; }

    public string DisplayName => Exists ? Name : $"{Name} (missing)";
}

public class K8SServicePort
{
    public string Name { get; init; } = string.Empty;

    public string Protocol { get; init; } = "TCP";

    public int Port { get; init; }

    public string TargetPort { get; init; } = "-";

    public int? NodePort { get; init; }

    public string DisplayText
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Name) ? Protocol : $"{Name}/{Protocol}";
            var builder = new StringBuilder($"{label}: {Port}->{TargetPort}");

            if (NodePort is { } nodePort)
            {
                builder.Append($" (node {nodePort})");
            }

            return builder.ToString();
        }
    }
}

public class K8SServiceSummary
{
    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string ClusterIp { get; init; } = string.Empty;

    public Dictionary<string, string> Selector { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<K8SServicePort> Ports { get; init; } = [];

    public string SelectorDisplay => Selector.Count == 0
        ? "-"
        : string.Join(", ", Selector.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));

    public string PortsDisplay => Ports.Count == 0
        ? "-"
        : string.Join(", ", Ports.Select(port => port.DisplayText));
}

public class K8SResourceSummary
{
    public K8SResourceType ResourceType { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string ClusterIp { get; init; } = string.Empty;

    public Dictionary<string, string> Selector { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<K8SServicePort> Ports { get; init; } = [];

    public List<string> Images { get; init; } = [];

    public int DesiredReplicas { get; init; }

    public int ReadyReplicas { get; init; }

    public DateTimeOffset? LastRestartedAtUtc { get; init; }

    public bool RestartSupported { get; init; }

    /// <summary>
    /// Gets the resolved backing workloads for a Service, or the workload itself for a workload row.
    /// </summary>
    public List<K8SWorkloadReference> RelatedWorkloads { get; init; } = [];

    /// <summary>
    /// Gets the Services resolved to this workload in the same namespace capture.
    /// </summary>
    public List<K8SServiceSummary> RelatedServices { get; init; } = [];

    /// <summary>
    /// Gets the captured Pods bound to the related workloads. An empty collection does not imply readiness.
    /// </summary>
    public List<K8SPodSummary> RelatedPods { get; init; } = [];

    /// <summary>Gets whether the capture contains any Pods bound to this resource's workloads.</summary>
    public bool HasPodEvidence => RelatedPods.Count > 0;

    /// <summary>Gets the total observed container restarts across the related Pods.</summary>
    public int RestartCount => RelatedPods.Sum(pod => pod.RestartCount);

    public string SelectorDisplay => Selector.Count == 0
        ? "-"
        : string.Join(", ", Selector.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));

    public string PortsDisplay => Ports.Count == 0
        ? "-"
        : string.Join(", ", Ports.Select(port => port.DisplayText));

    public string ImagesDisplay => Images.Count == 0
        ? "-"
        : string.Join(", ", Images);

    public string ReplicaStatus => DesiredReplicas > 0 || ReadyReplicas > 0 ? $"{ReadyReplicas}/{DesiredReplicas}" : "-";

    public string SearchText
    {
        get
        {
            var tokens =
                new List<string> { Name, Namespace, Kind, Type, ClusterIp, SelectorDisplay, PortsDisplay, ImagesDisplay, ReplicaStatus, LastRestartedAtUtc?.ToString("O") ?? string.Empty };

            tokens.AddRange(Selector.Select(pair => $"{pair.Key} {pair.Value}"));
            tokens.AddRange(Ports.Select(port => port.DisplayText));
            tokens.AddRange(Images);
            tokens.AddRange(RelatedWorkloads.Select(workload => workload.DisplayName));
            tokens.AddRange(RelatedServices.Select(service => service.Name));

            return string.Join(" ", tokens.Where(token => !string.IsNullOrWhiteSpace(token)));
        }
    }
}

public class K8SWorkloadReference
{
    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public int DesiredReplicas { get; init; }

    public int ReadyReplicas { get; init; }

    public DateTimeOffset? LastRestartedAtUtc { get; init; }

    /// <summary>
    /// Gets the replica count recorded by Monica before a previous scale-down operation, when present.
    /// </summary>
    public int? PreviousReplicas { get; init; }

    public Dictionary<string, string> Labels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Images { get; init; } = [];

    public string DisplayName => $"{Kind}/{Name}";

    public string ReplicaStatus => DesiredReplicas > 0 || ReadyReplicas > 0 ? $"{ReadyReplicas}/{DesiredReplicas}" : "-";

    public K8SWorkloadReference WithPodStartFallback(DateTimeOffset? podStartTime)
    {
        if (LastRestartedAtUtc.HasValue || !podStartTime.HasValue)
        {
            return this;
        }

        return new K8SWorkloadReference
        {
            Kind = Kind,
            Name = Name,
            Namespace = Namespace,
            DesiredReplicas = DesiredReplicas,
            ReadyReplicas = ReadyReplicas,
            LastRestartedAtUtc = podStartTime,
            PreviousReplicas = PreviousReplicas,
            Labels = Labels,
            Images = Images
        };
    }
}

public class K8SPodSummary
{
    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public int ReadyContainers { get; init; }

    public int TotalContainers { get; init; }

    public int RestartCount { get; init; }

    public string NodeName { get; init; } = string.Empty;

    public string PodIp { get; init; } = string.Empty;

    public string OwnerKind { get; init; } = string.Empty;

    public string OwnerName { get; init; } = string.Empty;

    public string WorkloadKind { get; init; } = string.Empty;

    public string WorkloadName { get; init; } = string.Empty;

    public DateTimeOffset? StartTimeUtc { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsReady { get; init; }

    public Dictionary<string, string> Labels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string PhaseDisplay => string.IsNullOrWhiteSpace(Phase) ? "-" : Phase;

    public string ReadyDisplay => TotalContainers > 0 ? $"{ReadyContainers}/{TotalContainers}" : "-";

    public string WorkloadDisplayName => !string.IsNullOrWhiteSpace(WorkloadKind) && !string.IsNullOrWhiteSpace(WorkloadName)
        ? $"{WorkloadKind}/{WorkloadName}"
        : !string.IsNullOrWhiteSpace(OwnerKind) && !string.IsNullOrWhiteSpace(OwnerName)
            ? $"{OwnerKind}/{OwnerName}"
            : "-";

    public bool HasIssue => !IsReady ||
                            !string.Equals(Phase, "Running", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(Reason) ||
                            !string.IsNullOrWhiteSpace(Message);

    public string IssueSummary
    {
        get
        {
            var segments = new List<string>();

            if (!string.IsNullOrWhiteSpace(Reason))
            {
                segments.Add(Reason);
            }

            if (!string.IsNullOrWhiteSpace(Message))
            {
                segments.Add(Message);
            }

            return segments.Count == 0 ? string.Empty : string.Join(" | ", segments);
        }
    }

    public K8SPodSummary WithWorkloadBinding(K8SWorkloadReference workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        return new K8SPodSummary
        {
            Name = Name,
            Namespace = Namespace,
            Phase = Phase,
            ReadyContainers = ReadyContainers,
            TotalContainers = TotalContainers,
            RestartCount = RestartCount,
            NodeName = NodeName,
            PodIp = PodIp,
            OwnerKind = OwnerKind,
            OwnerName = OwnerName,
            WorkloadKind = workload.Kind,
            WorkloadName = workload.Name,
            StartTimeUtc = StartTimeUtc,
            Reason = Reason,
            Message = Message,
            IsReady = IsReady,
            Labels = Labels
        };
    }
}

public class K8SPodDetails
{
    public K8SPodSummary Summary { get; init; } = new();

    public string DescriptionText { get; init; } = string.Empty;
}

public class K8SServiceDetails
{
    public K8SServiceSummary Summary { get; init; } = new();

    public string DescriptionText { get; init; } = string.Empty;

    public List<K8SWorkloadReference> BackingWorkloads { get; init; } = [];

    public List<K8SPodSummary> RelatedPods { get; init; } = [];
}

public class K8SResourceDetails
{
    public K8SResourceSummary Summary { get; init; } = new();

    public string DescriptionText { get; init; } = string.Empty;

    public List<K8SWorkloadReference> RelatedWorkloads { get; init; } = [];

    public List<K8SPodSummary> RelatedPods { get; init; } = [];
}

public class K8SResourceListResult
{
    public string Namespace { get; init; } = string.Empty;

    public K8SResourceType ResourceType { get; init; }

    public List<K8SResourceSummary> Items { get; init; } = [];

    public DateTimeOffset FetchedAtUtc { get; init; }
}

/// <summary>
/// Captures Services and supported workload kinds from one namespace read using one runtime configuration snapshot.
/// Kubernetes objects are read sequentially; this is not an atomic Kubernetes API snapshot.
/// </summary>
public class K8SNamespaceInventoryResult
{
    /// <summary>Gets the exact namespace captured by this read.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Gets all captured Services, Deployments, StatefulSets and DaemonSets.</summary>
    public List<K8SResourceSummary> Items { get; init; } = [];

    /// <summary>Gets when every constituent read completed successfully.</summary>
    public DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Projects one kind without performing another cluster read or changing capture time.</summary>
    public K8SResourceListResult ForResourceType(K8SResourceType resourceType) => new()
    {
        Namespace = Namespace,
        ResourceType = resourceType,
        Items = Items.Where(item => item.ResourceType == resourceType).ToList(),
        FetchedAtUtc = FetchedAtUtc
    };
}

public class K8SRestartTarget
{
    public K8SResourceType ResourceType { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string DisplayName => $"{Kind}/{Name}";
}

public class K8SRestartIgnoredTarget : K8SRestartTarget
{
    public K8SMessageCode? ReasonCode { get; init; }

    public List<string> ReasonArguments { get; init; } = [];

    public string Reason { get; internal set; } = string.Empty;

    public string DisplayText => $"{DisplayName}: {Reason}";

    public void ApplyLocalizedReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Reason = reason;
    }
}

public class K8SRestartPreview
{
    public string Namespace { get; init; } = string.Empty;

    public string ScopeName { get; init; } = string.Empty;

    public K8SResourceType? ResourceType { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public List<string> RestrictionKeywords { get; init; } = [];

    public List<K8SRestartTarget> RequestedTargets { get; init; } = [];

    public List<K8SRestartTarget> EligibleTargets { get; init; } = [];

    public List<K8SRestartIgnoredTarget> IgnoredTargets { get; init; } = [];

    public List<K8SWorkloadReference> Workloads { get; init; } = [];

    public List<string> CommandPreview { get; init; } = [];

    public bool HasRestartableTargets => Workloads.Count > 0;

    public K8SRestartPreview LocalizeIgnoredTargets(Func<K8SRestartIgnoredTarget, string> reasonFactory)
    {
        ArgumentNullException.ThrowIfNull(reasonFactory);

        foreach (var ignoredTarget in IgnoredTargets)
        {
            ignoredTarget.ApplyLocalizedReason(reasonFactory(ignoredTarget));
        }

        return this;
    }
}

public class K8SRestartResult
{
    public string Namespace { get; init; } = string.Empty;

    public string ScopeName { get; init; } = string.Empty;

    public K8SResourceType? ResourceType { get; init; }

    public List<string> AffectedTargets { get; init; } = [];

    public List<K8SWorkloadReference> Workloads { get; init; } = [];

    public List<string> ExecutedCommands { get; init; } = [];

    public List<K8SRestartIgnoredTarget> IgnoredTargets { get; init; } = [];

    public DateTimeOffset CompletedAtUtc { get; init; }

    public K8SRestartResult LocalizeIgnoredTargets(Func<K8SRestartIgnoredTarget, string> reasonFactory)
    {
        ArgumentNullException.ThrowIfNull(reasonFactory);

        foreach (var ignoredTarget in IgnoredTargets)
        {
            ignoredTarget.ApplyLocalizedReason(reasonFactory(ignoredTarget));
        }

        return this;
    }
}

/// <summary>
/// Represents a requested K8S resource target in a scale operation.
/// </summary>
public class K8SScaleTarget
{
    /// <summary>
    /// Gets the resource type that the operator requested.
    /// </summary>
    public K8SResourceType ResourceType { get; init; }

    /// <summary>
    /// Gets the Kubernetes kind displayed to operators.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested resource name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the namespace that contains the requested resource.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Gets a compact kind/name display value for dialogs and result summaries.
    /// </summary>
    public string DisplayName => $"{Kind}/{Name}";
}

/// <summary>
/// Represents a requested target that was intentionally excluded from a scale operation.
/// </summary>
public class K8SScaleIgnoredTarget : K8SScaleTarget
{
    /// <summary>
    /// Gets the stable reason code used for localization, when the reason came from a known K8S operation failure.
    /// </summary>
    public K8SMessageCode? ReasonCode { get; init; }

    /// <summary>
    /// Gets the localization arguments for <see cref="ReasonCode"/>.
    /// </summary>
    public List<string> ReasonArguments { get; init; } = [];

    /// <summary>
    /// Gets the current human-readable reason text.
    /// </summary>
    public string Reason { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets a compact target and reason display value.
    /// </summary>
    public string DisplayText => $"{DisplayName}: {Reason}";

    /// <summary>
    /// Replaces the reason with localized text for UI and API consumers.
    /// </summary>
    public void ApplyLocalizedReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Reason = reason;
    }
}

/// <summary>
/// Describes the concrete workload replica change and kubectl commands for one scalable workload.
/// </summary>
public class K8SScaleWorkloadPlan
{
    /// <summary>
    /// Gets the scale operation this workload plan will execute.
    /// </summary>
    public K8SScaleOperation Operation { get; init; }

    /// <summary>
    /// Gets the scalable workload kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the scalable workload name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the namespace that contains the workload.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Gets the desired replica count observed while building the preview.
    /// </summary>
    public int CurrentReplicas { get; init; }

    /// <summary>
    /// Gets the desired replica count that will be applied.
    /// </summary>
    public int TargetReplicas { get; init; }

    /// <summary>
    /// Gets the previously recorded replica count used by scale-up, when present.
    /// </summary>
    public int? PreviousReplicas { get; init; }

    /// <summary>
    /// Gets the exact kubectl commands that will be executed for this workload.
    /// </summary>
    public List<string> CommandPreview { get; init; } = [];

    /// <summary>
    /// Gets a compact kind/name display value.
    /// </summary>
    public string DisplayName => $"{Kind}/{Name}";

    /// <summary>
    /// Gets a compact current-to-target replica transition display value.
    /// </summary>
    public string ReplicaChangeDisplay => $"{CurrentReplicas}->{TargetReplicas}";
}

/// <summary>
/// Shows the full planned effect of a guarded K8S scale operation before execution.
/// </summary>
public class K8SScalePreview
{
    /// <summary>
    /// Gets the namespace where the operation would run.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Gets the human-readable scope name used in dialogs and result summaries.
    /// </summary>
    public string ScopeName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested resource type, when the preview targets one list type.
    /// </summary>
    public K8SResourceType? ResourceType { get; init; }

    /// <summary>
    /// Gets the planned scale operation.
    /// </summary>
    public K8SScaleOperation Operation { get; init; }

    /// <summary>
    /// Gets when the preview was generated.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>
    /// Gets active sensitive-operation keywords that constrained the preview.
    /// </summary>
    public List<string> RestrictionKeywords { get; init; } = [];

    /// <summary>
    /// Gets all requested resource targets.
    /// </summary>
    public List<K8SScaleTarget> RequestedTargets { get; init; } = [];

    /// <summary>
    /// Gets requested targets that produced at least one workload plan.
    /// </summary>
    public List<K8SScaleTarget> EligibleTargets { get; init; } = [];

    /// <summary>
    /// Gets requested targets that were excluded before execution.
    /// </summary>
    public List<K8SScaleIgnoredTarget> IgnoredTargets { get; init; } = [];

    /// <summary>
    /// Gets the concrete workload plans that would be executed.
    /// </summary>
    public List<K8SScaleWorkloadPlan> Workloads { get; init; } = [];

    /// <summary>
    /// Gets the flattened kubectl command preview for all workload plans.
    /// </summary>
    public List<string> CommandPreview { get; init; } = [];

    /// <summary>
    /// Gets whether this preview has at least one executable workload plan.
    /// </summary>
    public bool HasScalableTargets => Workloads.Count > 0;

    /// <summary>
    /// Localizes ignored target reasons in place and returns this preview.
    /// </summary>
    public K8SScalePreview LocalizeIgnoredTargets(Func<K8SScaleIgnoredTarget, string> reasonFactory)
    {
        ArgumentNullException.ThrowIfNull(reasonFactory);

        foreach (var ignoredTarget in IgnoredTargets)
        {
            ignoredTarget.ApplyLocalizedReason(reasonFactory(ignoredTarget));
        }

        return this;
    }
}

/// <summary>
/// Describes the completed effect of a guarded K8S scale operation.
/// </summary>
public class K8SScaleResult
{
    /// <summary>
    /// Gets the namespace where the operation ran.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Gets the human-readable scope name used in dialogs and result summaries.
    /// </summary>
    public string ScopeName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested resource type, when the operation targeted one list type.
    /// </summary>
    public K8SResourceType? ResourceType { get; init; }

    /// <summary>
    /// Gets the completed scale operation.
    /// </summary>
    public K8SScaleOperation Operation { get; init; }

    /// <summary>
    /// Gets the requested target names that produced executable workload plans.
    /// </summary>
    public List<string> AffectedTargets { get; init; } = [];

    /// <summary>
    /// Gets the workload plans that were executed.
    /// </summary>
    public List<K8SScaleWorkloadPlan> Workloads { get; init; } = [];

    /// <summary>
    /// Gets the kubectl commands that were executed.
    /// </summary>
    public List<string> ExecutedCommands { get; init; } = [];

    /// <summary>
    /// Gets requested targets that were excluded before execution.
    /// </summary>
    public List<K8SScaleIgnoredTarget> IgnoredTargets { get; init; } = [];

    /// <summary>
    /// Gets when the operation completed.
    /// </summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>
    /// Localizes ignored target reasons in place and returns this result.
    /// </summary>
    public K8SScaleResult LocalizeIgnoredTargets(Func<K8SScaleIgnoredTarget, string> reasonFactory)
    {
        ArgumentNullException.ThrowIfNull(reasonFactory);

        foreach (var ignoredTarget in IgnoredTargets)
        {
            ignoredTarget.ApplyLocalizedReason(reasonFactory(ignoredTarget));
        }

        return this;
    }
}
