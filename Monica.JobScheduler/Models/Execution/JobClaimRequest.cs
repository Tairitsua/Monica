namespace Monica.JobScheduler.Models.Execution;

/// <summary>
/// Describes one bounded atomic claim operation for a logical owner's queued work.
/// A worker serving several owners issues separate claims within its shared process budget.
/// </summary>
public sealed record JobClaimRequest
{
    /// <summary>
    /// Defines the hard upper bound for one atomic store claim.
    /// </summary>
    public const int MAX_COUNT = 256;
    private const int MIN_CANDIDATE_BUDGET = 64;
    private const int CANDIDATES_PER_REQUESTED_LEASE = 4;
    private const int MAX_CANDIDATE_BUDGET = MAX_COUNT * CANDIDATES_PER_REQUESTED_LEASE;

    /// <summary>
    /// Gets the scheduler scope from which work may be claimed.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the stable owner whose queued work this host executes.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets the concrete worker process identifier recorded on claimed leases.
    /// </summary>
    public required string WorkerInstanceId { get; init; }

    /// <summary>
    /// Gets the job keys executable by this process. Work owned by other owners, or work whose job key is absent
    /// from this collection, is never claimed.
    /// </summary>
    public required IReadOnlyCollection<string> JobKeys { get; init; }

    /// <summary>
    /// Gets the maximum number of executions to claim in this operation. A single claim is limited to 256 leases so
    /// every store implementation can enforce a finite candidate and query budget.
    /// </summary>
    public int MaxCount { get; init; } = 1;

    /// <summary>
    /// Gets the initial duration of each claimed execution lease.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; }

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        JobSchedulerIdentity.ValidateStandard(WorkerInstanceId, nameof(WorkerInstanceId));
        ArgumentNullException.ThrowIfNull(JobKeys);
        foreach (var jobKey in JobKeys)
        {
            JobSchedulerIdentity.ValidateJobKey(jobKey, nameof(JobKeys));
        }

        if (JobKeys.Count != JobKeys.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("The claim request contains duplicate job keys.", nameof(JobKeys));
        }

        if (MaxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCount), MaxCount, "Claim count must be greater than zero.");
        }

        if (MaxCount > MAX_COUNT)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCount),
                MaxCount,
                $"Claim count cannot exceed {MAX_COUNT}.");
        }

        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                LeaseDuration,
                "Execution lease duration must be greater than zero.");
        }
    }

    internal int GetCandidateBudget()
    {
        return Math.Clamp(
            MaxCount * CANDIDATES_PER_REQUESTED_LEASE,
            MIN_CANDIDATE_BUDGET,
            MAX_CANDIDATE_BUDGET);
    }
}
