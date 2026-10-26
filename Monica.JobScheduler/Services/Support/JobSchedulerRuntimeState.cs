namespace Monica.JobScheduler.Services.Support;

/// <summary>
/// Holds the local host's readiness facts without duplicating durable scheduler state. Reads are safe from any
/// thread; only the scheduler's own hosted services mutate the state through its internal members.
/// </summary>
public sealed class JobSchedulerRuntimeState
{
    private int _schedulingReady;
    private int _workerReady;
    private int _inFlightExecutions;
    private string? _schedulingMessage;
    private string? _workerMessage;
    private long _lastOwnerSchedulingWorkUtcTicks;
    private long _lastLeaseRecoveryWorkUtcTicks;
    private long _lastHistoryCleanupWorkUtcTicks;

    /// <summary>Gets the last successful local owner pass, excluding standby-only cycles.</summary>
    public DateTimeOffset? LastOwnerSchedulingWorkAtUtc => ReadTimestamp(ref _lastOwnerSchedulingWorkUtcTicks);

    /// <summary>Gets the last successful local lease-recovery pass, including an empty pass.</summary>
    public DateTimeOffset? LastLeaseRecoveryWorkAtUtc => ReadTimestamp(ref _lastLeaseRecoveryWorkUtcTicks);

    /// <summary>Gets the last successful local bounded cleanup pass, including an empty pass.</summary>
    public DateTimeOffset? LastHistoryCleanupWorkAtUtc => ReadTimestamp(ref _lastHistoryCleanupWorkUtcTicks);

    /// <summary>
    /// Gets whether the local scheduling plane's last cycle succeeded. Stays <see langword="false"/> until the
    /// first completed cycle.
    /// </summary>
    public bool SchedulingReady => Volatile.Read(ref _schedulingReady) != 0;

    /// <summary>
    /// Gets whether the local execution worker's last cycle succeeded. Stays <see langword="false"/> until the
    /// first completed cycle.
    /// </summary>
    public bool WorkerReady => Volatile.Read(ref _workerReady) != 0;

    /// <summary>Gets the scheduling plane's latest readiness message, when one was recorded.</summary>
    public string? SchedulingMessage => Volatile.Read(ref _schedulingMessage);

    /// <summary>Gets the worker plane's latest readiness message, when one was recorded.</summary>
    public string? WorkerMessage => Volatile.Read(ref _workerMessage);

    /// <summary>
    /// Gets the local worker's current in-flight execution count. Zero until the worker loop has run once.
    /// </summary>
    public int InFlightExecutions => Volatile.Read(ref _inFlightExecutions);

    internal bool SetScheduling(bool ready, string message) =>
        SetState(ref _schedulingReady, ref _schedulingMessage, ready, message);

    internal bool SetWorker(bool ready, string message) =>
        SetState(ref _workerReady, ref _workerMessage, ready, message);

    internal void SetInFlightExecutions(int count) => Volatile.Write(ref _inFlightExecutions, count);

    internal void RecordOwnerSchedulingWork(DateTimeOffset completedAtUtc) =>
        Interlocked.Exchange(ref _lastOwnerSchedulingWorkUtcTicks, completedAtUtc.UtcTicks);

    internal void RecordLeaseRecoveryWork(DateTimeOffset completedAtUtc) =>
        Interlocked.Exchange(ref _lastLeaseRecoveryWorkUtcTicks, completedAtUtc.UtcTicks);

    internal void RecordHistoryCleanupWork(DateTimeOffset completedAtUtc) =>
        Interlocked.Exchange(ref _lastHistoryCleanupWorkUtcTicks, completedAtUtc.UtcTicks);

    private static DateTimeOffset? ReadTimestamp(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }

    private static bool SetState(ref int state, ref string? currentMessage, bool ready, string message)
    {
        Volatile.Write(ref currentMessage, message);
        var value = ready ? 1 : 0;
        return Interlocked.Exchange(ref state, value) != value;
    }
}
