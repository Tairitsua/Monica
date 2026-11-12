using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.InProcessCollector.Models;

namespace Test.Monica.OpenTelemetry.UIOpenTelemetry.Support;

/// <summary>Holds or fails genuine host facade reads without replacing the collector or inventing evidence.</summary>
internal sealed class OpenTelemetryReadControl(OpenTelemetryFacade facade)
{
    private readonly object _sync = new();
    private OpenTelemetryReadGate? _next;
    private Result<OpenTelemetrySnapshot>? _failure;
    public int Reads { get; private set; }
    public OpenTelemetrySnapshot? LastGenuineSnapshot { get; private set; }

    public OpenTelemetryReadGate HoldNext()
    {
        lock (_sync)
        {
            if (_next is not null) throw new InvalidOperationException("A read is already held.");
            return _next = new();
        }
    }

    public void Fail(string? message) { lock (_sync) _failure = message is null ? null : Result.Failure<OpenTelemetrySnapshot>(message, ResultStatus.ServiceUnavailable); }

    public async Task<Result<OpenTelemetrySnapshot>> ReadAsync()
    {
        OpenTelemetryReadGate? gate;
        Result<OpenTelemetrySnapshot>? failure;
        lock (_sync) { Reads++; gate = _next; _next = null; failure = _failure; }
        var genuine = await facade.GetSnapshotAsync();
        LastGenuineSnapshot = genuine.Data;
        if (gate is not null) await gate.WaitAsync();
        return failure ?? genuine;
    }
}

internal sealed class OpenTelemetryReadGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void Release() => _release.TrySetResult();
    public Task WaitAsync() { _entered.TrySetResult(); return _release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
}
