using Monica.Core.Results;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;

namespace Test.Monica.Profiling.UIRuntimeMetrics.Support;

/// <summary>
/// Decorates genuine scoped-facade reads at the internal page boundary. It can hold a result or simulate
/// a failed envelope, but never fabricates samples, replaces the facade, or changes the real collector.
/// </summary>
internal sealed class RuntimeMetricsReadControl(RuntimeMetricsFacade facade)
{
    private readonly object _sync = new();
    private RuntimeMetricsReadGate? _nextGate;
    private Result<RuntimeMetricsTrend>? _failure;
    private Result<RuntimeMetricsTrend>? _pinnedResult;
    private bool _pinResults;
    private int _active;

    public int Reads { get; private set; }
    public int MaximumConcurrentReads { get; private set; }
    public RuntimeMetricsTrend? LastGenuineTrend { get; private set; }

    public RuntimeMetricsReadGate HoldNextRead()
    {
        lock (_sync)
        {
            if (_nextGate is not null)
            {
                throw new InvalidOperationException("A runtime read gate is already armed.");
            }

            return _nextGate = new RuntimeMetricsReadGate();
        }
    }

    public Result<RuntimeMetricsTrend> FailReads(string message, ResultStatus status = ResultStatus.ServiceUnavailable)
    {
        lock (_sync)
        {
            return _failure = Result.Failure<RuntimeMetricsTrend>(message, status);
        }
    }

    public void ClearFailure()
    {
        lock (_sync)
        {
            _failure = null;
        }
    }

    /// <summary>
    /// Reuses one genuine successful snapshot to represent a boundary returning no newer sample.
    /// Every invocation still calls the real facade; this never inserts or alters collector points.
    /// </summary>
    public void PinNextGenuineResult()
    {
        lock (_sync)
        {
            _pinResults = true;
        }
    }

    public Result<RuntimeMetricsTrend> Read()
    {
        RuntimeMetricsReadGate? gate;
        Result<RuntimeMetricsTrend>? failure;
        lock (_sync)
        {
            Reads++;
            _active++;
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, _active);
            gate = _nextGate;
            _nextGate = null;
            failure = _failure;
        }

        try
        {
            var genuine = facade.GetTrend();
            lock (_sync)
            {
                LastGenuineTrend = genuine.Data;
                if (_pinResults && genuine.IsSuccess())
                {
                    _pinnedResult ??= genuine;
                }
            }

            gate?.WaitForRelease();
            lock (_sync)
            {
                return failure ?? _pinnedResult ?? genuine;
            }
        }
        finally
        {
            lock (_sync)
            {
                _active--;
            }
        }
    }
}

internal sealed class RuntimeMetricsReadGate : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();

    public Task Entered => _entered.Task;
    public void Release() => _release.Set();

    public void WaitForRelease()
    {
        _entered.TrySetResult();
        if (!_release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The test did not release its runtime metrics read boundary.");
        }
    }

    public void Dispose() => _release.Dispose();
}
