namespace Monica.JobScheduler.Models.Execution;

/// <summary>Exact retained execution counts distributed over a bounded number of time buckets.</summary>
/// <remarks>
/// Uses the ledger's identity, search and time bounds, but deliberately excludes its state selection so state
/// facets remain comparable. Completion bounds select completion time and exclude records without a completion.
/// Paging and ordering do not affect aggregation. Counts describe retained records, not proof of history completeness.
/// </remarks>
public sealed record JobExecutionDistribution
{
    /// <summary>Gets the inclusive first bucket boundary.</summary>
    public required DateTimeOffset StartTimeUtc { get; init; }
    /// <summary>Gets the exclusive final bucket boundary.</summary>
    public required DateTimeOffset EndTimeUtc { get; init; }
    /// <summary>Gets whether bucket positions use completion time instead of creation time.</summary>
    public required bool UsesCompletionTime { get; init; }
    /// <summary>Gets equally spaced buckets, including empty intervals.</summary>
    public required IReadOnlyList<JobExecutionDistributionBucket> Buckets { get; init; }
    /// <summary>Gets whole-query counts for every defined state, independent of list pagination.</summary>
    public required IReadOnlyDictionary<JobExecutionState, long> StateTotals { get; init; }
}

/// <summary>One half-open interval of retained execution evidence.</summary>
public sealed record JobExecutionDistributionBucket
{
    /// <summary>Gets the inclusive start.</summary>
    public required DateTimeOffset StartTimeUtc { get; init; }
    /// <summary>Gets the exclusive end.</summary>
    public required DateTimeOffset EndTimeUtc { get; init; }
    /// <summary>Gets every defined state's exact count in this interval.</summary>
    public required IReadOnlyDictionary<JobExecutionState, long> Counts { get; init; }
}
