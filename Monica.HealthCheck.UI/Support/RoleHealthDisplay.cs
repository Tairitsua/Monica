using Monica.Core.ApplicationRoles;
using Monica.UI.Shared.Components.Visual;

namespace Monica.HealthCheck.UI.Support;

internal static class RoleHealthDisplay
{
    public static MoVisualRole Color(RoleHealthStatus status) => status switch
    {
        RoleHealthStatus.Faulted => MoVisualRole.Error,
        RoleHealthStatus.Degraded => MoVisualRole.Warning,
        RoleHealthStatus.Healthy => MoVisualRole.Success,
        RoleHealthStatus.Initializing => MoVisualRole.Info,
        _ => MoVisualRole.Neutral
    };

    public static bool CandidateReady(RoleHealthEntrySnapshot role) => role.StartupCandidateObservedValid == true;

    public static int Severity(RoleHealthEntrySnapshot role) => role.Status switch
    {
        RoleHealthStatus.Faulted => 0,
        RoleHealthStatus.Degraded => 1,
        RoleHealthStatus.Initializing => 2,
        RoleHealthStatus.Healthy => 3,
        _ => 4
    };
}
