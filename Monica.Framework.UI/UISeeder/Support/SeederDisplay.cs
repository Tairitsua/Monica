using Monica.Framework.Seeder.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UISeeder.Support;

/// <summary>
/// Maps Seeder diagnostics states onto MudBlazor's shared semantic presentation roles.
/// </summary>
public static class SeederDisplay
{
    /// <summary>Gets the stable DOM identifier for a Seeder's detail trigger.</summary>
    public static string DetailsTriggerId(string seederTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seederTypeName);
        return $"seeder-details-trigger-{Uri.EscapeDataString(seederTypeName)}";
    }

    /// <summary>Gets whether a run state requires one-second polling.</summary>
    public static bool IsActive(SeederRunStatus status) => status is
        SeederRunStatus.Waiting or SeederRunStatus.Running or SeederRunStatus.Aborting;

    /// <summary>Gets the alert severity for an overall run status.</summary>
    public static Severity Severity(SeederRunStatus status) => status switch
    {
        SeederRunStatus.Succeeded => MudBlazor.Severity.Success,
        SeederRunStatus.CompletedWithFailures => MudBlazor.Severity.Warning,
        SeederRunStatus.Aborting => MudBlazor.Severity.Warning,
        SeederRunStatus.Aborted => MudBlazor.Severity.Error,
        SeederRunStatus.Cancelled => MudBlazor.Severity.Warning,
        SeederRunStatus.Waiting => MudBlazor.Severity.Info,
        SeederRunStatus.Running => MudBlazor.Severity.Info,
        SeederRunStatus.Held => MudBlazor.Severity.Info,
        _ => MudBlazor.Severity.Normal
    };

    /// <summary>Gets the Material icon for an overall run status.</summary>
    public static string Icon(SeederRunStatus status) => status switch
    {
        SeederRunStatus.Waiting => MoIcons.Hourglass,
        SeederRunStatus.Running => MoIcons.Refresh,
        SeederRunStatus.Aborting => MoIcons.CancelSchedule,
        SeederRunStatus.Succeeded => MoIcons.Success,
        SeederRunStatus.CompletedWithFailures => MoIcons.Warning,
        SeederRunStatus.Aborted => MoIcons.Error,
        SeederRunStatus.Cancelled => MoIcons.Error,
        SeederRunStatus.Held => MoIcons.Clock,
        _ => MoIcons.Help
    };

    /// <summary>Gets the semantic color for a seeder status.</summary>
    public static Color Color(SeederStatus status) => status switch
    {
        SeederStatus.Pending => MudBlazor.Color.Default,
        SeederStatus.Running => MudBlazor.Color.Info,
        SeederStatus.Succeeded => MudBlazor.Color.Success,
        SeederStatus.Failed => MudBlazor.Color.Error,
        SeederStatus.Blocked => MudBlazor.Color.Warning,
        SeederStatus.Cancelled => MudBlazor.Color.Default,
        SeederStatus.Held => MudBlazor.Color.Info,
        SeederStatus.Disabled => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the Material icon for a seeder status.</summary>
    public static string Icon(SeederStatus status) => status switch
    {
        SeederStatus.Pending => MoIcons.Clock,
        SeederStatus.Running => MoIcons.PlayCircle,
        SeederStatus.Succeeded => MoIcons.Success,
        SeederStatus.Failed => MoIcons.Error,
        SeederStatus.Blocked => MoIcons.Ban,
        SeederStatus.Cancelled => MoIcons.Error,
        SeederStatus.Held => MoIcons.Clock,
        SeederStatus.Disabled => MoIcons.Ban,
        _ => MoIcons.Help
    };

    /// <summary>Gets the semantic color for an attempt status.</summary>
    public static Color Color(SeederAttemptStatus status) => status switch
    {
        SeederAttemptStatus.Running => MudBlazor.Color.Info,
        SeederAttemptStatus.Succeeded => MudBlazor.Color.Success,
        SeederAttemptStatus.Failed => MudBlazor.Color.Error,
        SeederAttemptStatus.Cancelled => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the semantic color for readiness criticality.</summary>
    public static Color Color(SeederCriticality criticality) => criticality switch
    {
        SeederCriticality.Required => MudBlazor.Color.Primary,
        SeederCriticality.Optional => MudBlazor.Color.Secondary,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the semantic color for failure behavior.</summary>
    public static Color Color(SeederFailureBehavior behavior) => behavior switch
    {
        SeederFailureBehavior.ContinueAndRecord => MudBlazor.Color.Info,
        SeederFailureBehavior.FailFast => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the semantic color for Seeder readiness.</summary>
    public static Color Color(SeederReadinessStatus status) => status switch
    {
        SeederReadinessStatus.Healthy => MudBlazor.Color.Success,
        SeederReadinessStatus.Degraded => MudBlazor.Color.Warning,
        SeederReadinessStatus.Unhealthy => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the Material icon for Seeder readiness.</summary>
    public static string Icon(SeederReadinessStatus status) => status switch
    {
        SeederReadinessStatus.Healthy => MoIcons.Success,
        SeederReadinessStatus.Degraded => MoIcons.Warning,
        SeederReadinessStatus.Unhealthy => MoIcons.Error,
        _ => MoIcons.Help
    };
}
