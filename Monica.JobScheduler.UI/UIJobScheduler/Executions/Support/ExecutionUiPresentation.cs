using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Executions.Support;

/// <summary>
/// Formats execution evidence consistently across the ledger and detail dialog.
/// </summary>
internal static class ExecutionUiPresentation
{
    private static readonly JsonSerializerOptions INDENTED_JSON_OPTIONS = new()
    {
        WriteIndented = true
    };

    internal static string ExportDiagnostics(JobExecutionInstance execution, DateTimeOffset observedAtUtc)
    {
        return JsonSerializer.Serialize(
            new { ObservedAtUtc = observedAtUtc, Execution = execution },
            INDENTED_JSON_OPTIONS);
    }

    internal static (string Message, Severity Severity) GetCancellationFeedback(
        ExecutionCancellationUiResult result,
        IStringLocalizer<JobSchedulerResource> localizer)
    {
        if (result.Error is not null)
        {
            return (result.Error, Severity.Error);
        }

        if (!result.IsAuthorized)
        {
            return (localizer["Access:DeniedDescription"].Value, Severity.Error);
        }

        return result.Status switch
        {
            JobCancellationStatus.Cancelled => (localizer["Executions:Cancel:Cancelled"].Value, Severity.Success),
            JobCancellationStatus.CancellationRequested => (localizer["Executions:Cancel:Requested"].Value, Severity.Success),
            JobCancellationStatus.AlreadyTerminal => (localizer["Executions:Cancel:AlreadyTerminal"].Value, Severity.Info),
            JobCancellationStatus.NotFound => (localizer["Executions:Cancel:NotFound"].Value, Severity.Warning),
            _ => (localizer["Executions:Cancel:Failed"].Value, Severity.Error)
        };
    }

    internal static Color GetLogLevelColor(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace or LogLevel.Debug => Color.Default,
        LogLevel.Information => Color.Info,
        LogLevel.Warning => Color.Warning,
        LogLevel.Error or LogLevel.Critical => Color.Error,
        _ => Color.Default
    };

    internal static string GetStateIcon(JobExecutionState state) => state switch
    {
        JobExecutionState.Queued => MoIcons.Clock,
        JobExecutionState.Running => MoIcons.PlayCircle,
        JobExecutionState.Succeeded => MoIcons.Success,
        JobExecutionState.Failed => MoIcons.Error,
        JobExecutionState.Cancelled => MoIcons.Error,
        JobExecutionState.Skipped => MoIcons.SkipNext,
        _ => MoIcons.Circle
    };
}
