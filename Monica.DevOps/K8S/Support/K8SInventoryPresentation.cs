using Monica.DevOps.K8S.Models;
using Monica.UI.Shared.Components.Visual;

namespace Monica.DevOps.K8S.Support;

/// <summary>Shared presentation rules for captured Kubernetes inventory evidence.</summary>
internal static class K8SInventoryPresentation
{
    public static bool IsSidecar(K8SResourceSummary row) => row.ResourceType == K8SResourceType.Service &&
        row.Ports.Count > 0 && row.Ports.All(port => port.Name.StartsWith("dapr-", StringComparison.OrdinalIgnoreCase));

    public static string Exposure(K8SResourceSummary row) => IsSidecar(row) ? "Sidecar" :
        string.Equals(row.ClusterIp, "None", StringComparison.OrdinalIgnoreCase) ? "Headless" :
        row.Type is "NodePort" or "ClusterIP" ? row.Type : "Other";

    public static bool NeedsAttention(K8SResourceSummary row) => row.ResourceType == K8SResourceType.Service
        ? row.RelatedWorkloads.Any(x => x.DesiredReplicas > 0 && x.ReadyReplicas < x.DesiredReplicas)
        : row.DesiredReplicas > 0 && row.ReadyReplicas < row.DesiredReplicas;

    public static MoVisualRole KindRole(K8SResourceType kind) => kind switch
    {
        K8SResourceType.Service => MoVisualRole.Info,
        K8SResourceType.Deployment => MoVisualRole.Primary,
        K8SResourceType.StatefulSet => MoVisualRole.Secondary,
        _ => MoVisualRole.Neutral
    };

    public static MoVisualRole HealthRole(K8SResourceSummary row) => row.DesiredReplicas == 0 ? MoVisualRole.Neutral :
        row.ReadyReplicas == 0 ? MoVisualRole.Error : NeedsAttention(row) ? MoVisualRole.Warning : MoVisualRole.Success;

    public static MoVisualRole AttentionRole(IEnumerable<K8SResourceSummary> rows) => rows.Any(row =>
        row.ResourceType == K8SResourceType.Service
            ? row.RelatedWorkloads.Any(workload => workload.DesiredReplicas > 0 && workload.ReadyReplicas == 0)
            : row.DesiredReplicas > 0 && row.ReadyReplicas == 0) ? MoVisualRole.Error : MoVisualRole.Warning;
}
