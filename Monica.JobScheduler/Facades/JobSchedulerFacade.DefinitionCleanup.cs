using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Microsoft.Extensions.Logging;

namespace Monica.JobScheduler.Facades;

public sealed partial class JobSchedulerFacade
{
    /// <summary>Queries obsolete definitions and stale owner observations in this host's configured shared scope.</summary>
    /// <remarks>The caller owns authorization. Manual-mode hosts cannot maintain the shared store.</remarks>
    public Task<Result<JobDefinitionCleanupSnapshot>> QueryDefinitionCleanupAsync(JobDefinitionCleanupQuery query,
        CancellationToken cancellationToken = default) => ExecuteAsync(() =>
        {
            EnsureDefinitionMaintenanceEnabled();
            return store.QueryDefinitionCleanupAsync(_schedulerScopeKey, query,
                options.Value.SnapshotSyncInterval * OWNER_ONLINE_THRESHOLD_SNAPSHOT_MULTIPLIER, cancellationToken);
        }, "assess definition cleanup", cancellationToken);

    /// <summary>Freezes the exact removal selection and explicit queue-cancellation choice for review.</summary>
    public Task<Result<JobDefinitionCleanupPlan>> PreviewDefinitionCleanupAsync(IReadOnlyList<JobId> ids,
        bool cancelQueued, CancellationToken cancellationToken = default)
    {
        var frozen = ids.ToArray();
        return ExecuteAsync(() =>
        {
            EnsureDefinitionMaintenanceEnabled();
            return store.PreviewDefinitionCleanupAsync(_schedulerScopeKey, frozen, cancelQueued, cancellationToken);
        }, "preview definition cleanup", cancellationToken);
    }

    /// <summary>Revalidates and atomically applies a reviewed removal request, retaining execution evidence.</summary>
    /// <remarks>Authorize this destructive operation separately from viewing. Changed and blocked outcomes perform no removal.</remarks>
    public Task<Result<JobDefinitionMaintenanceResult>> RemoveDefinitionsAsync(JobDefinitionCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var frozen = request with { Targets = request.Targets.ToArray() };
        return ExecuteAsync(async () =>
        {
            EnsureDefinitionMaintenanceEnabled();
            var result = await store.RemoveDefinitionsAsync(_schedulerScopeKey, frozen, cancellationToken);
            logger.LogInformation("Definition cleanup in scope {SchedulerScope}: {Status}, {Definitions} definitions, {Cancelled} queued cancellations, {Retained} retained executions.",
                _schedulerScopeKey, result.Status, result.Definitions, result.CancelledCount, result.RetainedExecutionCount);
            return result;
        }, "remove obsolete definitions", cancellationToken);
    }

    /// <summary>Reviews publication and execution prerequisites before deliberately retiring one exact owner.</summary>
    public Task<Result<JobOwnerRetirementPreview>> PreviewOwnerRetirementAsync(string ownerKey,
        CancellationToken cancellationToken = default) => ExecuteAsync(() =>
        {
            EnsureDefinitionMaintenanceEnabled();
            return store.PreviewOwnerRetirementAsync(_schedulerScopeKey, ownerKey, cancellationToken);
        }, "preview owner retirement", cancellationToken);

    /// <summary>After confirmed publisher shutdown, permanently fences an exact owner and marks its definitions absent.</summary>
    /// <remarks>The caller owns authorization and shutdown confirmation. Queued work and history remain; no automatic reactivation exists.</remarks>
    public Task<Result<JobDefinitionMaintenanceResult>> RetireOwnerAsync(string ownerKey, string expectedStamp,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
        {
            EnsureDefinitionMaintenanceEnabled();
            var result = await store.RetireOwnerAsync(_schedulerScopeKey, ownerKey, expectedStamp, cancellationToken);
            logger.LogInformation("Owner retirement in scope {SchedulerScope} for {OwnerKey}: {Status}, {Definitions} definitions.",
                _schedulerScopeKey, ownerKey, result.Status, result.Definitions.Count);
            return result;
        }, "retire a scheduler owner", cancellationToken);

    private void EnsureDefinitionMaintenanceEnabled()
    {
        if (!options.Value.AutonomousWork)
            throw new InvalidOperationException("Shared definition maintenance is unavailable in manual mode.");
    }
}
