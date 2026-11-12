namespace Monica.JobScheduler.Models.Execution;

/// <summary>
/// Selects the ordering used for execution history queries.
/// </summary>
public enum JobExecutionSortField
{
    /// <summary>
    /// Sort by enqueue time.
    /// </summary>
    CreatedAtUtc,

    /// <summary>
    /// Sort by availability time.
    /// </summary>
    AvailableAtUtc,

    /// <summary>
    /// Sort by the most recent attempt start time.
    /// </summary>
    StartedAtUtc,

    /// <summary>
    /// Sort by terminal completion time.
    /// </summary>
    CompletedAtUtc
}

/// <summary>
/// Defines a bounded execution history query.
/// </summary>
public sealed record JobExecutionQuery
{
    /// <summary>
    /// Gets the required scheduler scope.
    /// </summary>
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets an optional exact owner filter.
    /// </summary>
    public string? OwnerKey { get; init; }

    /// <summary>
    /// Gets an optional exact logical job key.
    /// </summary>
    public string? JobKey { get; init; }

    /// <summary>
    /// Gets optional execution states to include.
    /// </summary>
    public IReadOnlyCollection<JobExecutionState>? States { get; init; }

    /// <summary>
    /// Gets an optional case-insensitive search across job and execution identifiers.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Gets an optional inclusive lower enqueue-time boundary.
    /// </summary>
    public DateTimeOffset? CreatedAfterUtc { get; init; }

    /// <summary>
    /// Gets an optional inclusive upper enqueue-time boundary.
    /// </summary>
    public DateTimeOffset? CreatedBeforeUtc { get; init; }

    /// <summary>Gets an optional inclusive terminal-completion lower bound. Executions without a completion are excluded.</summary>
    public DateTimeOffset? CompletedAfterUtc { get; init; }

    /// <summary>Gets an optional inclusive terminal-completion upper bound. Combines with other filters before counting and paging.</summary>
    public DateTimeOffset? CompletedBeforeUtc { get; init; }

    /// <summary>
    /// Gets the selected sort field.
    /// </summary>
    public JobExecutionSortField SortField { get; init; } = JobExecutionSortField.CreatedAtUtc;

    /// <summary>
    /// Gets whether results are returned in descending order.
    /// </summary>
    public bool SortDescending { get; init; } = true;

    /// <summary>
    /// Gets the one-based page number.
    /// </summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>
    /// Gets the number of rows returned per page.
    /// </summary>
    public int PageSize { get; init; } = 20;

    internal void Validate()
    {
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        if (OwnerKey is not null)
        {
            JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        }

        if (JobKey is not null)
        {
            JobSchedulerIdentity.ValidateJobKey(JobKey, nameof(JobKey));
        }

        if (!Enum.IsDefined(SortField))
        {
            throw new ArgumentOutOfRangeException(nameof(SortField), SortField, "Sort field is not supported.");
        }

        if (PageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PageNumber), PageNumber, "Page number must be greater than zero.");
        }

        if (PageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize), PageSize, "Page size must be greater than zero.");
        }
    }
}

/// <summary>
/// Defines history retention for one logical job.
/// </summary>
public sealed record JobHistoryRetentionPolicy
{
    /// <summary>
    /// Gets the maximum number of newest terminal executions retained. Non-positive values disable the count limit.
    /// </summary>
    public int MaxRecords { get; init; }

    /// <summary>
    /// Gets the maximum age in days. <see langword="null"/> disables the age limit.
    /// </summary>
    public int? MaxDays { get; init; }
}
