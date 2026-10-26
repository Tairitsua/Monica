namespace Monica.JobScheduler.Models.Execution;

/// <summary>Bounds one maintenance pass by visited jobs, deleted executions, and elapsed time.</summary>
public sealed record JobHistoryCleanupRequest
{
    /// <summary>Gets the scheduler scope to maintain, including absent or removed owners.</summary>
    public required string SchedulerScopeKey { get; init; }
    /// <summary>Gets the exclusive maintenance grant, when invoked by the automatic host.</summary>
    public JobCoordinatorLeaseKey? CoordinatorLease { get; init; }
    /// <summary>Gets the last fully scanned identity; null starts a fresh keyset scan.</summary>
    public JobId? AfterJob { get; init; }
    /// <summary>Gets the maximum job identities scanned. The default is 32.</summary>
    public int MaxJobs { get; init; } = 32;
    /// <summary>Gets the maximum terminal executions deleted. The default is 5,000.</summary>
    public int MaxDeletions { get; init; } = 5000;
    /// <summary>Gets the budget between bounded job operations. The default is two seconds.</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Gets retained terminal executions for jobs without a definition; zero disables count retention.</summary>
    public int MaxRetainedOrphanedExecutions { get; init; } = 10;
}

/// <summary>Reports a bounded maintenance pass and its stable keyset continuation.</summary>
public sealed record JobHistoryCleanupResult
{
    /// <summary>Gets the deleted terminal execution count.</summary>
    public int DeletedCount { get; init; }
    /// <summary>Gets the fully processed job count.</summary>
    public int ScannedJobs { get; init; }
    /// <summary>Gets the last fully processed identity; a partial job must be revisited.</summary>
    public JobId? LastScannedJob { get; init; }
    /// <summary>Gets whether the pass reached the end of the scope's job identities.</summary>
    public bool CompletedScan { get; init; }
}
