using Monica.Core.Results;
using Monica.Profiling.TypeAllocation.Facades;
using Monica.Profiling.TypeAllocation.Models;

namespace Test.Monica.Profiling.UIMemoryAnalysis.Support;

/// <summary>
/// Preserves real idle facade reads/subscriptions while supplying structural result fixtures at the
/// page boundary. Heap results are simulated only here; no native heap or ETW action is invoked.
/// Captured callbacks can model an invocation already admitted before unsubscription.
/// </summary>
internal sealed class TypeAllocationBoundaryControl(TypeAllocationFacade facade)
{
    private readonly object _sync = new();
    private MemoryBoundaryGate? _heapGate;
    private MemoryBoundaryGate? _readGate;

    public Result<TypeAllocationSnapshot>? LiveResult { get; set; }
    public Result<TypeAllocationSnapshot> HeapResult { get; set; } = new()
    {
        Status = ResultStatus.ServiceUnavailable,
        Message = "No structural heap result was supplied."
    };
    public TypeAllocationSnapshot? LastGenuineLiveSnapshot { get; private set; }
    public Action<TypeAllocationSnapshot>? CapturedCallback { get; private set; }
    public int ReadCount { get; private set; }
    public int HeapCaptureCount { get; private set; }
    public int SubscriptionCount { get; private set; }
    public int UnsubscriptionCount { get; private set; }

    public Result<TypeAllocationSnapshot> ReadLive()
    {
        MemoryBoundaryGate? gate;
        lock (_sync)
        {
            ReadCount++;
            gate = _readGate;
            _readGate = null;
        }
        var genuine = facade.GetCurrentSnapshot();
        LastGenuineLiveSnapshot = genuine.Data;
        gate?.WaitForRelease();
        return LiveResult ?? genuine;
    }

    public async Task<Result<TypeAllocationSnapshot>> CaptureHeapAsync()
    {
        MemoryBoundaryGate? gate;
        Result<TypeAllocationSnapshot> result;
        lock (_sync)
        {
            HeapCaptureCount++;
            gate = _heapGate;
            _heapGate = null;
            result = HeapResult;
        }
        if (gate != null) await gate.WaitForReleaseAsync();
        return result;
    }

    public Action Subscribe(Action<TypeAllocationSnapshot> callback)
    {
        var unsubscribe = facade.SubscribeToUpdates(callback);
        lock (_sync)
        {
            SubscriptionCount++;
            CapturedCallback = callback;
        }
        return () =>
        {
            lock (_sync) UnsubscriptionCount++;
            unsubscribe();
        };
    }

    public MemoryBoundaryGate HoldNextHeap()
    {
        lock (_sync)
        {
            if (_heapGate != null) throw new InvalidOperationException("A heap boundary is already held.");
            return _heapGate = new MemoryBoundaryGate();
        }
    }

    public MemoryBoundaryGate HoldNextRead()
    {
        lock (_sync)
        {
            if (_readGate != null) throw new InvalidOperationException("A read boundary is already held.");
            return _readGate = new MemoryBoundaryGate();
        }
    }

    public void Publish(TypeAllocationSnapshot snapshot) => CapturedCallback?.Invoke(snapshot);
}
