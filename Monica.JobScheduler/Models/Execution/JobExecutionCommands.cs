using Microsoft.Extensions.Logging;

namespace Monica.JobScheduler.Models.Execution;

/// <summary>
/// Describes a durable enqueue operation.
/// </summary>
public sealed record JobEnqueueRequest
{
    /// <summary>
    /// Gets the idempotency and execution identifier supplied by the caller.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Gets the scheduler scope whose definitions resolve the job.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the owner of the job definition that resolves the job.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the owner-scoped logical job key resolved atomically from the persisted definition.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// Gets the serialized triggered-job arguments.
    /// </summary>
    public string? JobArgs { get; init; }

    /// <summary>
    /// Gets the earliest UTC time at which the execution may be claimed. When omitted, the store captures its
    /// authoritative current time once while creating the execution. An explicitly supplied value is part of the
    /// idempotency assertion for later retries.
    /// </summary>
    public DateTimeOffset? AvailableAtUtc { get; init; }

    /// <summary>
    /// Gets a concise audit message explaining how the execution entered the queue.
    /// </summary>
    public string EnqueueReason { get; init; } = "Job execution enqueued";

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(InstanceId, nameof(InstanceId));
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        JobSchedulerIdentity.ValidateJobKey(JobKey, nameof(JobKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(EnqueueReason);

        if (AvailableAtUtc is { } availableAtUtc && availableAtUtc == default)
        {
            throw new ArgumentException(
                "An explicitly supplied availability time cannot be the default value.",
                nameof(AvailableAtUtc));
        }
    }
}

/// <summary>
/// Describes an operator request to run one recurring job immediately without moving its schedule cursor.
/// </summary>
public sealed record JobRecurringRunNowCommand
{
    /// <summary>
    /// Gets the idempotency and execution identifier supplied by the caller.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Gets the scheduler scope whose definitions resolve the recurring job.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the owner of the recurring job definition.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the owner-scoped logical recurring job key.
    /// </summary>
    public required string JobKey { get; init; }

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(InstanceId, nameof(InstanceId));
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        JobSchedulerIdentity.ValidateJobKey(JobKey, nameof(JobKey));
    }
}

/// <summary>
/// Contains the opaque fencing values required to mutate a running execution.
/// </summary>
public sealed record JobLeaseKey
{
    /// <summary>
    /// Gets the scheduler scope containing the execution.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the execution identifier.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Gets the worker instance that owns the lease.
    /// </summary>
    public required string WorkerInstanceId { get; init; }

    /// <summary>
    /// Gets the opaque execution lease token. A new token fences every previous owner.
    /// </summary>
    public required string LeaseToken { get; init; }

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(InstanceId, nameof(InstanceId));
        JobSchedulerIdentity.ValidateStandard(WorkerInstanceId, nameof(WorkerInstanceId));
        JobSchedulerIdentity.ValidateStandard(LeaseToken, nameof(LeaseToken));
    }
}

/// <summary>
/// Couples a claimed execution snapshot with the opaque key used for later lease mutations.
/// </summary>
public sealed record JobExecutionLease
{
    /// <summary>
    /// Gets the authoritative store time when this lease was granted. Workers use the duration between this
    /// value and the expiration with their local monotonic request-start timestamp, never a cross-host clock comparison.
    /// </summary>
    public required DateTimeOffset GrantedAtUtc { get; init; }

    /// <summary>
    /// Gets the claimed execution snapshot.
    /// </summary>
    public required JobExecutionInstance Execution { get; init; }

    /// <summary>
    /// Gets the fenced lease key.
    /// </summary>
    public required JobLeaseKey LeaseKey { get; init; }
}

/// <summary>
/// Describes the result of renewing an execution lease.
/// </summary>
public enum JobLeaseRenewalStatus
{
    /// <summary>
    /// The lease remains active and job execution may continue.
    /// </summary>
    Active,

    /// <summary>
    /// The lease remains active, but the worker must request cooperative cancellation from job code.
    /// </summary>
    CancellationRequested,

    /// <summary>
    /// The supplied fencing token no longer owns the execution.
    /// </summary>
    Lost
}

/// <summary>
/// Reports the outcome of an execution lease renewal.
/// </summary>
public sealed record JobLeaseRenewalResult
{
    /// <summary>
    /// Gets the authoritative store time used to grant the renewal. Successful results must supply this value
    /// and <see cref="LeaseExpiresAtUtc"/>; lost leases supply neither.
    /// </summary>
    public DateTimeOffset? GrantedAtUtc { get; init; }

    /// <summary>
    /// Gets the renewal status.
    /// </summary>
    public JobLeaseRenewalStatus Status { get; init; }

    /// <summary>
    /// Gets the new lease expiration when renewal succeeded.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAtUtc { get; init; }
}

/// <summary>
/// Describes how one claimed execution attempt ended.
/// </summary>
public enum JobAttemptOutcome
{
    /// <summary>
    /// User code completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// User code or the execution pipeline failed.
    /// </summary>
    Failed,

    /// <summary>
    /// User code cooperatively observed cancellation.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The host stopped owning the attempt after user code fully exited; the work should be made available again.
    /// </summary>
    Abandoned
}

/// <summary>
/// Describes completion of a fenced execution attempt.
/// </summary>
public sealed record JobAttemptCompletion
{
    /// <summary>
    /// Gets the current fenced lease key.
    /// </summary>
    public required JobLeaseKey LeaseKey { get; init; }

    /// <summary>
    /// Gets how the attempt ended.
    /// </summary>
    public JobAttemptOutcome Outcome { get; init; }

    /// <summary>
    /// Gets an optional diagnostic message recorded in execution history.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets how long a failed attempt should wait before its next retry.
    /// </summary>
    public TimeSpan RetryDelay { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(LeaseKey);
        LeaseKey.Validate();

        if (!Enum.IsDefined(Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(Outcome), Outcome, "The attempt outcome is not supported.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetryDelay),
                RetryDelay,
                "Retry delay cannot be negative.");
        }
    }
}

/// <summary>
/// Reports whether a fenced completion changed the execution.
/// </summary>
public enum JobAttemptCompletionStatus
{
    /// <summary>
    /// The completion was accepted and persisted.
    /// </summary>
    Applied,

    /// <summary>
    /// The lease token was stale, expired, or otherwise no longer owned the execution.
    /// </summary>
    Lost
}

/// <summary>
/// Reports the durable result of completing or releasing an attempt.
/// </summary>
public sealed record JobAttemptCompletionResult
{
    /// <summary>
    /// Gets whether the completion was applied.
    /// </summary>
    public JobAttemptCompletionStatus Status { get; init; }

    /// <summary>
    /// Gets the resulting execution snapshot when the completion was applied.
    /// </summary>
    public JobExecutionInstance? Execution { get; init; }
}

/// <summary>
/// Describes the durable effect of a cancellation request.
/// </summary>
public enum JobCancellationStatus
{
    /// <summary>
    /// No execution with the supplied identity exists.
    /// </summary>
    NotFound,

    /// <summary>
    /// Queued work was cancelled immediately.
    /// </summary>
    Cancelled,

    /// <summary>
    /// A running worker must observe and honor the persisted request.
    /// </summary>
    CancellationRequested,

    /// <summary>
    /// The execution had already reached a terminal state.
    /// </summary>
    AlreadyTerminal
}

/// <summary>
/// Reports the durable result of requesting execution cancellation.
/// </summary>
public sealed record JobCancellationResult
{
    /// <summary>
    /// Gets the cancellation status.
    /// </summary>
    public JobCancellationStatus Status { get; init; }

    /// <summary>
    /// Gets the execution snapshot when an execution was found.
    /// </summary>
    public JobExecutionInstance? Execution { get; init; }
}

/// <summary>
/// Reports whether a mutation guarded by an execution lease was accepted.
/// </summary>
public enum JobLeaseMutationStatus
{
    /// <summary>
    /// The current lease accepted the mutation.
    /// </summary>
    Applied,

    /// <summary>
    /// The supplied lease no longer owns the execution.
    /// </summary>
    Lost
}

/// <summary>
/// Describes a worker log entry guarded by the current execution lease.
/// </summary>
public sealed record JobExecutionLogEntry
{
    /// <summary>
    /// Gets the current fenced lease key.
    /// </summary>
    public required JobLeaseKey LeaseKey { get; init; }

    /// <summary>
    /// Gets the developer-facing log message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
}
