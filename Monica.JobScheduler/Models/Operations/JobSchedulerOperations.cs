using Monica.Core.Results;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Models.Operations;

/// <summary>
/// Describes the runtime state of a job definition's recurring schedule.
/// </summary>
public enum JobRecurringScheduleStatus
{
    /// <summary>
    /// The declaration is triggered rather than recurring.
    /// </summary>
    NotRecurring,

    /// <summary>
    /// The recurring declaration is active, but its durable cursor has not been created yet.
    /// </summary>
    AwaitingSynchronization,

    /// <summary>
    /// The durable cursor has a future occurrence that can be materialized.
    /// </summary>
    Scheduled,

    /// <summary>
    /// One or more explicit suspension reasons prevent automatic materialization.
    /// </summary>
    Suspended,

    /// <summary>
    /// The durable cursor has no future occurrence under the effective schedule.
    /// </summary>
    Exhausted
}

/// <summary>
/// Projects the bounded operational state associated with one job definition.
/// </summary>
/// <remarks>
/// The definition remains the source of truth for the immutable declaration and operator policy. Runtime fields are
/// read-only projections: the latest execution, queue counts, and cursor state follow the definition identity.
/// </remarks>
public sealed record JobOperationalSummary
{
    /// <summary>
    /// Gets the current definition with its declaration and independent operator policy.
    /// </summary>
    public required JobDefinition Definition { get; init; }

    /// <summary>
    /// Gets the recurring runtime state for the definition.
    /// </summary>
    public JobRecurringScheduleStatus RecurringScheduleStatus { get; init; }

    /// <summary>
    /// Gets the independent reasons that currently prevent automatic recurring occurrence materialization.
    /// Triggered jobs always return <see cref="JobRecurringScheduleSuspensionReason.None"/>.
    /// </summary>
    public JobRecurringScheduleSuspensionReason SuspensionReasons { get; init; }

    /// <summary>
    /// Gets whether any reason currently prevents automatic recurring occurrence materialization.
    /// </summary>
    public bool IsSuspended => SuspensionReasons != JobRecurringScheduleSuspensionReason.None;

    /// <summary>
    /// Gets the next UTC occurrence for a scheduled recurring job. Suspended, exhausted, unsynchronized, and
    /// triggered jobs return <see langword="null"/>.
    /// </summary>
    public DateTimeOffset? NextOccurrenceUtc { get; init; }

    /// <summary>
    /// Gets the newest execution for the definition, or <see langword="null"/> when it has never run. The bounded
    /// snapshot omits retained audit history; use the execution detail query when history is required.
    /// </summary>
    public JobExecutionInstance? LatestExecution { get; init; }

    /// <summary>
    /// Gets the number of durable queued executions for the definition.
    /// </summary>
    public int QueuedExecutionCount { get; init; }

    /// <summary>
    /// Gets the number of durable running executions for the definition.
    /// </summary>
    public int RunningExecutionCount { get; init; }

    /// <summary>
    /// Gets the total number of non-terminal queued and running executions for the definition.
    /// </summary>
    public int ActiveExecutionCount => QueuedExecutionCount + RunningExecutionCount;
}

/// <summary>
/// Describes one policy replacement in a bounded batch operation.
/// </summary>
/// <remarks>
/// Policy fields are replacement values, matching <see cref="JobPolicyChange"/> semantics. The expected concurrency
/// stamp fences changes based on a stale operational page.
/// </remarks>
public sealed record JobPolicyBatchUpdateItem
{
    /// <summary>
    /// Gets the owner of the job definition.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the owner-scoped logical job key.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// Gets the complete replacement override set.
    /// </summary>
    public required JobPolicyOverrides Overrides { get; init; }

    /// <summary>
    /// Gets the policy concurrency stamp observed by the caller.
    /// </summary>
    public required string ExpectedConcurrencyStamp { get; init; }

    internal JobPolicyChange ToChange() => new()
    {
        Overrides = Overrides,
        ExpectedConcurrencyStamp = ExpectedConcurrencyStamp
    };
}

/// <summary>
/// Requests independent policy replacements for a finite set of job definitions.
/// </summary>
public sealed record JobPolicyBatchUpdateRequest
{
    /// <summary>
    /// Defines the hard upper bound for one facade request.
    /// </summary>
    public const int MAX_ITEM_COUNT = 100;

    /// <summary>
    /// Gets the policy replacements to attempt. Items are processed independently and in request order.
    /// </summary>
    public required IReadOnlyList<JobPolicyBatchUpdateItem> Items { get; init; }
}

/// <summary>
/// Reports the outcome of one item in a partial-success policy batch.
/// </summary>
public sealed record JobPolicyBatchUpdateItemResult
{
    /// <summary>
    /// Gets the definition owner supplied by the request.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the logical job key supplied by the request.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// Gets the updated policy when the item succeeded.
    /// </summary>
    public JobPolicy? Policy { get; init; }

    /// <summary>
    /// Gets the failure detail when the item failed validation, lookup, or optimistic concurrency.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the structured failure status when this item failed. Conflict identifies an optimistic-concurrency miss,
    /// not-found identifies an unknown definition, and bad-request identifies invalid overrides.
    /// </summary>
    public ResultStatus? FailureStatus { get; init; }

    /// <summary>
    /// Gets whether this item completed successfully.
    /// </summary>
    public bool IsSucceeded => Policy is not null;
}

/// <summary>
/// Contains ordered per-item outcomes for a bounded policy batch.
/// </summary>
public sealed record JobPolicyBatchUpdateResult
{
    /// <summary>
    /// Gets one outcome for every requested item, preserving request order.
    /// </summary>
    public required IReadOnlyList<JobPolicyBatchUpdateItemResult> Items { get; init; }

    /// <summary>
    /// Gets the number of successful policy replacements.
    /// </summary>
    public int SucceededCount => Items.Count(static item => item.IsSucceeded);

    /// <summary>
    /// Gets the number of failed policy replacements.
    /// </summary>
    public int FailedCount => Items.Count - SucceededCount;
}

/// <summary>
/// Provides one bounded operational snapshot of the scheduler scope: definitions, queue state, and recent activity.
/// </summary>
public sealed record JobSchedulerOverview
{
    /// <summary>
    /// Gets the definitions currently persisted in the scope, including absent-but-retained definitions.
    /// </summary>
    public required IReadOnlyList<JobDefinition> Definitions { get; init; }

    /// <summary>
    /// Gets queue counts grouped by durable execution state.
    /// </summary>
    public required IReadOnlyDictionary<JobExecutionState, int> ExecutionStateCounts { get; init; }

    /// <summary>
    /// Gets a bounded newest-first execution activity sample.
    /// </summary>
    public required IReadOnlyList<JobExecutionInstance> RecentExecutions { get; init; }

    /// <summary>
    /// Gets the average number of executions admitted per hour over the trailing 24 hours.
    /// </summary>
    public double ExecutionsPerHourLast24h { get; init; }

    /// <summary>
    /// Gets when the operational snapshot finished loading.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; }
}

/// <summary>
/// Describes one operator-initiated execution admission request.
/// </summary>
public sealed record JobTriggerRequest
{
    /// <summary>
    /// Gets the owner of the job definition.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the owner-scoped logical job key.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// Gets serialized triggered-job arguments. The payload must match the declaration's argument contract.
    /// </summary>
    public string? JobArgs { get; init; }

    /// <summary>
    /// Gets the earliest claim time. When omitted, the store atomically captures its authoritative current time while
    /// creating the execution; idempotent retries do not assert a newly computed timestamp.
    /// </summary>
    public DateTimeOffset? AvailableAtUtc { get; init; }

    /// <summary>
    /// Gets an optional idempotency identifier. A generated identifier is used when omitted.
    /// </summary>
    public string? InstanceId { get; init; }
}

/// <summary>
/// Describes an operator-initiated immediate execution of a recurring job.
/// </summary>
public sealed record JobRecurringRunNowRequest
{
    /// <summary>
    /// Gets the owner of the recurring job definition.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the owner-scoped logical recurring job key.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// Gets an optional idempotency identifier. A generated identifier is used when omitted.
    /// </summary>
    public string? InstanceId { get; init; }
}
