using Monica.JobScheduler.Models;

namespace Monica.JobScheduler.UI.UIJobScheduler.Shared;

/// <summary>
/// Builds owner-aware scheduler URLs from the same identity used by the store and detail page.
/// </summary>
internal static class JobSchedulerUiUrls
{
    private const string JOB_DEFINITION_PATH = "/job-scheduler/jobs";

    /// <summary>
    /// Builds the detail URL for one owner-scoped job definition.
    /// </summary>
    internal static string JobDefinition(JobId jobId, string? returnUrl = null) =>
        $"{JOB_DEFINITION_PATH}/{Uri.EscapeDataString(jobId.OwnerKey)}/{Uri.EscapeDataString(jobId.JobKey)}"
        + (returnUrl is null ? "" : $"?return={Uri.EscapeDataString(returnUrl)}");
}
