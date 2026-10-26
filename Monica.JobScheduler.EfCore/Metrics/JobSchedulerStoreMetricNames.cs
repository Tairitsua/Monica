namespace Monica.JobScheduler.EfCore.Metrics;

/// <summary>Identifies the scheduler store instruments that hosts may subscribe to with their metrics exporter.</summary>
public static class JobSchedulerStoreMetricNames
{
    /// <summary>Gets the meter name for bounded relational scheduler operations.</summary>
    public const string MeterName = "Monica.JobScheduler.Store";
    /// <summary>Gets the operation latency histogram name; duration includes local admission and database waits.</summary>
    public const string OperationDuration = "monica.job_scheduler.store.operation.duration";
    /// <summary>Gets the completed operation counter name, tagged by operation and outcome.</summary>
    public const string Operations = "monica.job_scheduler.store.operations";
    /// <summary>Gets the transaction retry counter name.</summary>
    public const string TransactionRetries = "monica.job_scheduler.store.transaction.retries";
}
