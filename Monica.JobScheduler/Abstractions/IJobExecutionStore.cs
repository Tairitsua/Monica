using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Abstractions;

/// <summary>
/// Defines the atomic persistence boundary for durable job execution.
/// </summary>
/// <remarks>
/// Implementations own queue availability, distributed concurrency gates, execution leases, fencing, retries,
/// cancellation, recurring cursor materialization, and history. Callers must not emulate these operations with
/// read-modify-write sequences. A concurrency gate is identified by scheduler scope, owner, and job key, so it fences
/// overlapping work for one owner-scoped definition.
/// </remarks>
public interface IJobExecutionStore
{
    /// <summary>Aggregates exact retained execution evidence using the ledger's filters except state selection.</summary>
    /// <param name="query">Identity, search and inclusive time bounds. State, sort and page fields do not affect the distribution.</param>
    /// <param name="bucketCount">Number of time buckets, from 1 through 200; defaults to 48.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>Completion bounds select completion time; otherwise creation time is used. All buckets share one store snapshot.</remarks>
    Task<JobExecutionDistribution> GetExecutionDistributionAsync(JobExecutionQuery query, int bucketCount = 48,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably enqueues an execution against a present definition. Repeating an identical request is idempotent. When
    /// availability is omitted, the store captures its authoritative current time only for the initial insert and
    /// ignores availability on retries.
    /// </summary>
    /// <exception cref="JobDefinitionNotFoundException">The addressed definition is unknown or absent.</exception>
    Task<JobExecutionInstance> EnqueueAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably queues an immediate operator execution of a recurring job without advancing or otherwise changing its
    /// recurring schedule cursor. Schedule suspension does not reject this explicit operator action. Repeating an
    /// identical command is idempotent.
    /// </summary>
    /// <exception cref="JobDefinitionNotFoundException">The addressed definition is unknown or absent.</exception>
    Task<JobExecutionInstance> RunRecurringNowAsync(
        JobRecurringRunNowCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one execution by scope and identifier, including its complete retained audit history.
    /// </summary>
    Task<JobExecutionInstance?> GetExecutionAsync(
        string schedulerScopeKey,
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries executions through a bounded page. Returned execution snapshots omit audit history; callers that need
    /// history must load a selected execution with <see cref="GetExecutionAsync"/>.
    /// </summary>
    Task<QueryResult<JobExecutionInstance>> QueryExecutionsAsync(
        JobExecutionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets execution counts grouped by state within an optional enqueue-time range.
    /// </summary>
    Task<IReadOnlyDictionary<JobExecutionState, int>> GetExecutionStateStatisticsAsync(
        string schedulerScopeKey,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets at most one newest execution summary for every distinct requested job key of one owner. Durable providers
    /// keep each per-key read bounded, and returned snapshots omit audit history.
    /// </summary>
    Task<IReadOnlyDictionary<JobId, JobExecutionInstance?>> GetLatestExecutionsAsync(
        string schedulerScopeKey,
        string ownerKey,
        IEnumerable<JobId> jobIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects the globally oldest terminal executions eligible under per-job count and age retention.
    /// </summary>
    /// <remarks>
    /// The returned collection never exceeds <paramref name="maxDeletions"/>. A positive bound is required so durable
    /// providers can evaluate arbitrarily large histories without materializing the entire terminal set. Definitions
    /// without a supplied retention policy fall back to the orphan-retention bound.
    /// </remarks>
    Task<IReadOnlyList<string>> GetExecutionCleanupCandidatesAsync(
        string schedulerScopeKey,
        IReadOnlyDictionary<JobId, JobHistoryRetentionPolicy> retentionPolicies,
        int maxRetainedOrphanedExecutions,
        int maxDeletions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes terminal executions. Running or queued work is never deleted.
    /// </summary>
    Task<int> DeleteExecutionsAsync(
        string schedulerScopeKey,
        IEnumerable<string> instanceIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims due work owned by the requesting host's owner while enforcing the distributed concurrency
    /// gate for each <c>(SchedulerScopeKey, OwnerKey, JobKey)</c> definition identity.
    /// </summary>
    /// <remarks>
    /// Only queued executions whose owner matches the request and whose job key is locally executable are claimable.
    /// </remarks>
    Task<IReadOnlyList<JobExecutionLease>> ClaimAsync(
        JobClaimRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a current execution lease and reports persisted cancellation requests.
    /// </summary>
    Task<JobLeaseRenewalResult> RenewLeaseAsync(
        JobLeaseKey leaseKey,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically completes a fenced attempt, applying retry policy and releasing its distributed concurrency gate.
    /// </summary>
    Task<JobAttemptCompletionResult> CompleteAttemptAsync(
        JobAttemptCompletion completion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues a fenced execution after user code has fully exited during graceful host shutdown.
    /// </summary>
    Task<JobAttemptCompletionResult> ReleaseLeaseAsync(
        JobLeaseKey leaseKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels queued work immediately or persists a cooperative cancellation request for running work.
    /// </summary>
    Task<JobCancellationResult> RequestCancellationAsync(
        string schedulerScopeKey,
        string instanceId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a worker log entry only while the supplied fenced execution lease remains current. Implementations
    /// enforce the configured total per-execution history count and history-message length boundaries atomically.
    /// </summary>
    Task<JobLeaseMutationStatus> AppendExecutionLogAsync(
        JobExecutionLogEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repairs expired running work of every owner in the scope: work with a persisted cancellation request is
    /// cancelled, an attempt that already exceeded its execution timeout or whose execution exhausted its
    /// lease-loss budget receives a failed attempt (consuming retry policy), and the remaining work is requeued
    /// for another claim.
    /// </summary>
    Task<IReadOnlyList<JobExecutionInstance>> RecoverExpiredLeasesAsync(
        ExpiredLeaseRecoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles one recurring cursor with the persisted definition and the host-owned suspension reasons.
    /// </summary>
    /// <remarks>
    /// The store derives the effective execution template and schedule from the definition, so the host reports only
    /// its own debug-mode state. A created cursor starts from the store's authoritative current time; a schedule
    /// replacement or resume is prospective and never replays suppressed time. An absent or triggered definition
    /// removes its stale cursor.
    /// </remarks>
    Task<RecurringScheduleSynchronizationResult> SynchronizeRecurringScheduleAsync(
        RecurringScheduleSynchronization synchronization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets due recurring cursors of one owner in deterministic occurrence-time order. A caller may repeatedly query
    /// after advancing cursors to share a materialization budget fairly across the overdue set.
    /// </summary>
    Task<IReadOnlyList<RecurringScheduleCursor>> GetDueRecurringSchedulesAsync(
        string schedulerScopeKey,
        string ownerKey,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically materializes one recurring occurrence and compare-and-swap advances its cursor. The store's
    /// authoritative clock determines the first future occurrence after the materialized one, coalescing any missed
    /// backlog. Repeated attempts after a successful cursor advance return a stale-cursor result.
    /// </summary>
    Task<RecurringMaterializationResult> TryMaterializeRecurringOccurrenceAsync(
        RecurringOccurrenceMaterialization materialization,
        CancellationToken cancellationToken = default);
}
