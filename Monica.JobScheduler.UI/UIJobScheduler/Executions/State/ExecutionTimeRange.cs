namespace Monica.JobScheduler.UI.UIJobScheduler.Executions.State;

/// <summary>
/// Defines the enqueue-time window available in the execution ledger.
/// </summary>
internal enum ExecutionTimeRange
{
    /// <summary>
    /// Includes executions from every enqueue time.
    /// </summary>
    All,

    /// <summary>
    /// Includes executions enqueued during the previous hour.
    /// </summary>
    LastHour,

    /// <summary>
    /// Includes executions enqueued during the previous 24 hours.
    /// </summary>
    Last24Hours,

    /// <summary>
    /// Includes executions enqueued during the previous seven days.
    /// </summary>
    Last7Days,

    /// <summary>
    /// Includes executions enqueued during the previous 30 days.
    /// </summary>
    Last30Days,

    /// <summary>
    /// Uses operator-selected local calendar boundaries.
    /// </summary>
    Custom,

    /// <summary>Uses an exact route-provided creation interval, with an exclusive end, from an analytics drilldown.</summary>
    Exact
}
