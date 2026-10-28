using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Models.Execution;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Abstractions;

/// <summary>
/// Provides the single durable correctness boundary for definition and execution state.
/// </summary>
public interface IJobSchedulerStore : IJobDefinitionStore, IJobExecutionStore, IJobSchedulerAnalyticsStore
{
    /// <summary>Gets role leases bound to this store's correctness authority, distinct from job execution claims.</summary>
    IExclusiveLeaseStore CoordinatorLeases { get; }

    /// <summary>Observes retained coordinator holders and expiries in one exact scope without exposing tokens.</summary>
    /// <remarks>This read does not acquire or renew any lease. Expired records remain visible for diagnosis.</remarks>
    Task<JobCoordinatorSnapshot> GetCoordinatorSnapshotAsync(string schedulerScopeKey,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one bounded history pass, returning a keyset continuation instead of rescanning the scope.</summary>
    Task<JobHistoryCleanupResult> CleanupHistoryAsync(JobHistoryCleanupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the operational projection for one exact job definition.
    /// </summary>
    /// <returns>
    /// The bounded operational projection, or <see langword="null"/> when the identity is unknown.
    /// </returns>
    Task<JobOperationalSummary?> GetOperationalSummaryAsync(
        string schedulerScopeKey,
        JobId jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a page of definitions enriched with their recurring cursor, newest execution, and active queue counts.
    /// </summary>
    /// <remarks>
    /// Definition filters and ordering are applied before runtime enrichment, keeping execution and cursor reads
    /// bounded to the selected definition page. Returned execution snapshots omit retained audit history.
    /// </remarks>
    Task<QueryResult<JobOperationalSummary>> QueryOperationalSummariesAsync(
        string schedulerScopeKey,
        JobDefinitionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates every owner's definition counts by presence, active queue depth, and snapshot freshness in one
    /// scope. The result is ordered by owner key.
    /// </summary>
    Task<IReadOnlyList<JobOwnerSummary>> QueryOwnerSummariesAsync(
        string schedulerScopeKey,
        CancellationToken cancellationToken = default);
}
