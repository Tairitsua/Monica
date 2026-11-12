using Monica.Core.Results;
using Monica.Profiling.MemoryDiagnostics.Facades;
using Monica.Profiling.MemoryDiagnostics.Models;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;

namespace Test.Monica.Profiling.UIMemoryAnalysis.Support;

internal enum MemoryReadStage { Snapshot, GcDetails, Trend }

/// <summary>
/// Reads every observation through real scoped facades, then optionally holds/decorates the result at
/// the internal page boundary. Action outcomes are simulated only there: no GC or dump provider is invoked.
/// </summary>
internal sealed class MemoryAnalysisBoundaryControl(MemoryDiagnosticsFacade memoryFacade, RuntimeMetricsFacade runtimeFacade)
{
    private readonly object _sync = new();
    private readonly Dictionary<MemoryReadStage, Result> _failures = [];
    private readonly Dictionary<MemoryReadStage, MemoryBoundaryGate> _gates = [];
    private MemoryBoundaryGate? _actionGate;
    private int _activeReads;

    public List<MemoryReadStage> ReadOrder { get; } = [];
    public int MaximumConcurrentReads { get; private set; }
    public MemorySnapshot? LastGenuineSnapshot { get; private set; }
    public GcDetails? LastGenuineGcDetails { get; private set; }
    public RuntimeMetricsTrend? LastGenuineTrend { get; private set; }
    public int GcActions { get; private set; }
    public int DumpActions { get; private set; }
    public int? RequestedGeneration { get; private set; }
    public Result GcResult { get; set; } = Result.Success();
    public Result<string> DumpResult { get; set; } = Result.Success<string>("server/provider-result.gcdump");

    public Result Fail(MemoryReadStage stage, string? message, ResultStatus status = ResultStatus.ServiceUnavailable)
    {
        lock (_sync)
        {
            return _failures[stage] = new Result { Message = message, Status = status };
        }
    }

    public void ClearFailures()
    {
        lock (_sync) _failures.Clear();
    }

    public MemoryBoundaryGate HoldNextRead(MemoryReadStage stage)
    {
        lock (_sync)
        {
            var gate = new MemoryBoundaryGate();
            _gates.Add(stage, gate);
            return gate;
        }
    }

    public MemoryBoundaryGate HoldNextAction()
    {
        lock (_sync)
        {
            if (_actionGate is not null) throw new InvalidOperationException("An action gate is already armed.");
            return _actionGate = new MemoryBoundaryGate();
        }
    }

    public Result<MemorySnapshot> ReadSnapshot() => Read(MemoryReadStage.Snapshot, () =>
    {
        var genuine = memoryFacade.GetCurrentSnapshot();
        LastGenuineSnapshot = genuine.Data;
        return genuine;
    });

    public Result<GcDetails> ReadGcDetails() => Read(MemoryReadStage.GcDetails, () =>
    {
        var genuine = memoryFacade.GetGcDetails();
        LastGenuineGcDetails = genuine.Data;
        return genuine;
    });

    public Result<RuntimeMetricsTrend> ReadTrend() => Read(MemoryReadStage.Trend, () =>
    {
        var genuine = runtimeFacade.GetTrend();
        LastGenuineTrend = genuine.Data;
        return genuine;
    });

    public Result ForceGarbageCollection(int generation)
    {
        MemoryBoundaryGate? gate;
        lock (_sync)
        {
            GcActions++;
            RequestedGeneration = generation;
            gate = TakeActionGate();
        }
        gate?.WaitForRelease();
        return GcResult;
    }

    public async Task<Result<string>> CreateGcDumpAsync()
    {
        MemoryBoundaryGate? gate;
        lock (_sync)
        {
            DumpActions++;
            gate = TakeActionGate();
        }
        if (gate is not null) await gate.WaitForReleaseAsync();
        return DumpResult;
    }

    private MemoryBoundaryGate? TakeActionGate()
    {
        var gate = _actionGate;
        _actionGate = null;
        return gate;
    }

    private Result<T> Read<T>(MemoryReadStage stage, Func<Result<T>> query)
    {
        MemoryBoundaryGate? gate;
        Result? failure;
        lock (_sync)
        {
            ReadOrder.Add(stage);
            _activeReads++;
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, _activeReads);
            _gates.Remove(stage, out gate);
            _failures.TryGetValue(stage, out failure);
        }
        try
        {
            var genuine = query();
            gate?.WaitForRelease();
            return failure is null ? genuine : new Result<T> { Status = failure.Status, Message = failure.Message, Metadata = failure.Metadata };
        }
        finally
        {
            lock (_sync) _activeReads--;
        }
    }
}

internal sealed class MemoryBoundaryGate : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task;
    public void Release() => _release.TrySetResult();

    public void WaitForRelease()
    {
        _entered.TrySetResult();
        if (!_release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("A held memory read/action was not released.");
    }

    public async Task WaitForReleaseAsync()
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose() => Release();
}
