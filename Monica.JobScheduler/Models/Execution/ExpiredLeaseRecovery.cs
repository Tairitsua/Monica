using Microsoft.Extensions.Logging;

namespace Monica.JobScheduler.Models.Execution;

/// <summary>
/// Defines one bounded expired-lease recovery pass and owns the policy that resolves each recovered
/// execution's durable outcome.
/// </summary>
public sealed record ExpiredLeaseRecoveryRequest
{
    /// <summary>Gets the automatic scope maintenance grant, when invoked by a hosted coordinator.</summary>
    public JobCoordinatorLeaseKey? CoordinatorLease { get; init; }

    /// <summary>
    /// Gets the scheduler scope to repair.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the maximum number of expired leases repaired in one operation.
    /// </summary>
    public int MaxCount { get; init; } = 100;

    /// <summary>
    /// Gets the delay applied before a recovery-caused failed attempt becomes retryable.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the maximum number of expired-lease recoveries one execution may accumulate before the recovery
    /// records a failed attempt instead of requeueing the execution again.
    /// </summary>
    public int MaxLeaseLossesBeforeFailure { get; init; } = 5;

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        if (MaxCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCount),
                MaxCount,
                "Recovery count must be greater than zero.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), RetryDelay, "Retry delay cannot be negative.");
        }

        if (MaxLeaseLossesBeforeFailure < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxLeaseLossesBeforeFailure),
                MaxLeaseLossesBeforeFailure,
                "The lease-loss budget must be greater than zero.");
        }
    }

    /// <summary>
    /// Resolves the durable outcome for one recovered expired lease whose execution carries no persisted
    /// cancellation request. The recovery records a failed attempt (consuming retry policy, mirroring a
    /// worker-reported failure) when the expired attempt already exceeded its execution timeout or when the
    /// execution exhausted its lease-loss budget; otherwise the execution is requeued for another claim.
    /// </summary>
    /// <param name="now">The recovery pass time, on the same clock that judged the lease expired.</param>
    /// <param name="attemptStartedAtUtc">
    /// When the expired attempt was claimed, or <see langword="null"/> when no attempt start is recorded.
    /// </param>
    /// <param name="maxExecutionTimeout">The execution timeout captured in the attempt's template snapshot.</param>
    /// <param name="leaseLossCountAfterRecovery">
    /// The execution's lease-loss count including the recovery being resolved.
    /// </param>
    /// <param name="retryAttempt">Failed attempts the execution has already consumed.</param>
    /// <param name="retryCount">The retry budget captured in the attempt's template snapshot.</param>
    internal ExpiredLeaseRecoveryOutcome ResolveOutcome(
        DateTimeOffset now,
        DateTimeOffset? attemptStartedAtUtc,
        TimeSpan maxExecutionTimeout,
        int leaseLossCountAfterRecovery,
        int retryAttempt,
        int retryCount)
    {
        var exceededTimeout = attemptStartedAtUtc is { } startedAt && now - startedAt > maxExecutionTimeout;
        if (!exceededTimeout && leaseLossCountAfterRecovery < MaxLeaseLossesBeforeFailure)
        {
            return new ExpiredLeaseRecoveryOutcome
            {
                NewState = JobExecutionState.Queued,
                HistoryMessage = "Expired execution lease recovered",
                AvailableAtUtc = now
            };
        }

        var reason = exceededTimeout
            ? "Expired lease recovered after the attempt exceeded MaxExecutionTimeout"
            : $"Expired lease recovered after {leaseLossCountAfterRecovery} lost leases";
        if (retryAttempt + 1 <= retryCount)
        {
            return new ExpiredLeaseRecoveryOutcome
            {
                NewState = JobExecutionState.Queued,
                HistoryMessage = $"{reason}; retry {retryAttempt + 1} queued",
                HistoryLogLevel = LogLevel.Warning,
                ConsumesRetryAttempt = true,
                AvailableAtUtc = now + RetryDelay
            };
        }

        return new ExpiredLeaseRecoveryOutcome
        {
            NewState = JobExecutionState.Failed,
            HistoryMessage = reason,
            HistoryLogLevel = LogLevel.Error,
            ConsumesRetryAttempt = true
        };
    }
}

/// <summary>
/// The durable outcome one expired-lease recovery assigns to an execution.
/// </summary>
internal sealed record ExpiredLeaseRecoveryOutcome
{
    /// <summary>
    /// Gets the state the recovered execution transitions to.
    /// </summary>
    public required JobExecutionState NewState { get; init; }

    /// <summary>
    /// Gets the retained history message describing the recovery decision.
    /// </summary>
    public required string HistoryMessage { get; init; }

    /// <summary>
    /// Gets the diagnostic severity recorded with the history entry.
    /// </summary>
    public LogLevel HistoryLogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Gets whether the recovery consumes one unit of the execution's retry policy, mirroring a
    /// worker-reported failed attempt.
    /// </summary>
    public bool ConsumesRetryAttempt { get; init; }

    /// <summary>
    /// Gets the availability time assigned when the outcome requeues the execution, or <see langword="null"/>
    /// for terminal outcomes.
    /// </summary>
    public DateTimeOffset? AvailableAtUtc { get; init; }
}
