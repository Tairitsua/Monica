namespace Monica.JobScheduler.Metrics;

/// <summary>Identifies worker admission and bounded store-cleanup instruments.</summary>
public static class JobExecutionWorkerMetricNames
{
    /// <summary>The host-owned execution-worker meter.</summary>
    public const string MeterName = "Monica.JobScheduler.Worker";
    /// <summary>Counts pause, recovery and termination events by bounded reason code.</summary>
    public const string RecoveryEvents = "monica.job_scheduler.worker.recovery.events";
    /// <summary>Measures cancellation-to-exit time for store calls and their callbacks, in seconds.</summary>
    public const string StoreDrainDuration = "monica.job_scheduler.worker.store_drain.duration";
}
