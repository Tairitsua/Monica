using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Models;
using Monica.OpenTelemetry.InProcessCollector.Services;
using Monica.OpenTelemetry.Localization;

namespace Monica.OpenTelemetry.InProcessCollector.Facades;

/// <summary>
/// Host-facing entry point for OpenTelemetry in-process metric snapshots.
/// </summary>
public sealed class OpenTelemetryFacade(
    InProcessMetricsCollector collector,
    ILogger<OpenTelemetryFacade> logger,
    IStringLocalizer<OpenTelemetryResource> localizer)
{
    /// <summary>
    /// Gets the latest bounded in-process metric snapshot for the current application instance.
    /// </summary>
    public Task<Result<OpenTelemetrySnapshot>> GetSnapshotAsync()
    {
        try
        {
            return Task.FromResult(Result.Success(collector.GetSnapshot()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read OpenTelemetry in-process metrics snapshot.");
            return Task.FromResult<Result<OpenTelemetrySnapshot>>(Result.Failure<OpenTelemetrySnapshot>(
                localizer["Snapshot:ReadFailed", ex.GetMessageRecursively()],
                ResultStatus.InternalError));
        }
    }
}
