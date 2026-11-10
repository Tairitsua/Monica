using System.Diagnostics;

namespace Monica.JobScheduler.EfCore.Metrics;

/// <summary>Captures the first cancellation phase without running logging or provider code in a cancellation callback.</summary>
internal sealed class JobSchedulerStoreOperationTrace
{
    private readonly object _gate = new();
    private string _phase = "admission";
    private string? _cancelledPhase;
    private long _cancelledAt;

    public void Enter(string phase) { lock (_gate) _phase = phase; }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_cancelledPhase is not null) return;
            _cancelledPhase = _phase;
            _cancelledAt = Stopwatch.GetTimestamp();
        }
    }

    public (string Phase, TimeSpan Elapsed)? GetCancellation()
    {
        lock (_gate)
            return _cancelledPhase is null ? null : (_cancelledPhase, Stopwatch.GetElapsedTime(_cancelledAt));
    }
}
