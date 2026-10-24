using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Shared;

/// <summary>
/// Centralizes small, theme-aware scheduler presentation mappings shared by UI components.
/// </summary>
internal static class JobSchedulerUiPresentation
{
    internal static Color GetStateColor(JobExecutionState state) => state switch
    {
        JobExecutionState.Queued => Color.Info,
        JobExecutionState.Running => Color.Primary,
        JobExecutionState.Succeeded => Color.Success,
        JobExecutionState.Failed => Color.Error,
        JobExecutionState.Cancelled => Color.Secondary,
        JobExecutionState.Skipped => Color.Warning,
        _ => Color.Default
    };

    internal static Color GetRecurringScheduleStatusColor(JobRecurringScheduleStatus status) => status switch
    {
        JobRecurringScheduleStatus.Scheduled => Color.Success,
        JobRecurringScheduleStatus.AwaitingSynchronization => Color.Info,
        JobRecurringScheduleStatus.Suspended => Color.Warning,
        JobRecurringScheduleStatus.Exhausted => Color.Secondary,
        _ => Color.Default
    };

    internal static bool HasSuspensionReason(
        JobRecurringScheduleSuspensionReason reasons,
        JobRecurringScheduleSuspensionReason reason) => (reasons & reason) != 0;

    internal static bool IsOperatorPaused(JobOperationalSummary summary) =>
        HasSuspensionReason(summary.SuspensionReasons, JobRecurringScheduleSuspensionReason.OperatorPolicy);

    internal static bool IsManualSuppressed(JobOperationalSummary summary) =>
        HasSuspensionReason(summary.SuspensionReasons, JobRecurringScheduleSuspensionReason.Manual);

    internal static bool IsManualOnlySuppressed(JobOperationalSummary summary) =>
        IsManualSuppressed(summary) && !IsOperatorPaused(summary);

    internal static JobRecurringScheduleSuspensionReason SetOperatorPolicySuspension(
        JobRecurringScheduleSuspensionReason reasons,
        bool isSuspended) => isSuspended
        ? reasons | JobRecurringScheduleSuspensionReason.OperatorPolicy
        : reasons & ~JobRecurringScheduleSuspensionReason.OperatorPolicy;

    internal static string ShortIdentity(string? value, int length = 12)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
        {
            return value ?? "—";
        }

        return value[..length];
    }

    internal static string GetJobKeyLabel(string jobKey) => jobKey.Split('.').LastOrDefault() ?? jobKey;

    internal static string GetJobDisplayName(JobExecutionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return string.IsNullOrWhiteSpace(template.JobName)
            ? GetJobKeyLabel(template.JobKey)
            : template.JobName;
    }
}
