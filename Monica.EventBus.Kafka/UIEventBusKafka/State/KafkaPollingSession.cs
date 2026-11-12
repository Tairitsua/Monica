using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.EventBus.Kafka.UIEventBusKafka.State;

internal sealed record KafkaSamplingTarget(string ClusterId, string? GroupId = null, string? TopicName = null);

/// <summary>
/// Coordinates admitted sampling work and owns each timer and target cancellation lifetime.
/// </summary>
/// <remarks>
/// External work and subscriber callbacks run outside the state lock. A callback already admitted
/// before retirement may finish during teardown; subscribers own and drain their renderer work.
/// </remarks>
internal sealed class KafkaPollingSession<TSnapshot>(
    Func<KafkaSamplingTarget, CancellationToken, Task<Result<TSnapshot>>> capture,
    Func<TSnapshot, bool> isUsable,
    Func<TSnapshot, string?> diagnostic,
    Func<TSnapshot, DateTimeOffset> capturedAt,
    bool enabled,
    TimeSpan refreshInterval) : IAsyncDisposable where TSnapshot : class
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _controls = new(1, 1);
    private readonly SemaphoreSlim _captures = new(1, 1);
    private readonly HashSet<Task> _admitted = [];
    private KafkaSamplingTarget? _target;
    private KafkaSamplingTarget? _snapshotTarget;
    private CancellationTokenSource? _targetLifetime;
    private CancellationToken _targetToken;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _loopLifetime;
    private Task? _loop;
    private Task? _retirement;
    private long _generation;
    private bool _retired;
    private bool _enabled = enabled;
    private TimeSpan _interval = NormalizeInterval(refreshInterval);
    private TSnapshot? _snapshot;
    private DateTimeOffset? _lastCapturedAt;
    private DateTimeOffset? _lastAttemptedAt;
    private string? _lastError;
    private bool _isSampling;

    public TSnapshot? Snapshot { get { lock (_sync) return _snapshot; } private set => _snapshot = value; }
    public DateTimeOffset? LastCapturedAt { get { lock (_sync) return _lastCapturedAt; } private set => _lastCapturedAt = value; }
    public DateTimeOffset? LastAttemptedAt { get { lock (_sync) return _lastAttemptedAt; } private set => _lastAttemptedAt = value; }
    public string? LastError { get { lock (_sync) return _lastError; } private set => _lastError = value; }
    public bool IsSampling { get { lock (_sync) return _isSampling; } private set => _isSampling = value; }
    public bool IsEnabled { get { lock (_sync) return _enabled; } }
    public TimeSpan RefreshInterval { get { lock (_sync) return _interval; } }
    public event Action? StateChanged;

    public Task ActivateAsync(KafkaSamplingTarget? target, CancellationToken token = default, bool captureWhenDisabled = false) =>
        Admit(async operationToken =>
        {
            await _controls.WaitAsync(operationToken);
            try
            {
                var changed = !Equals(_target, target);
                if (!changed && (!_enabled || _loop is { IsCompleted: false })) return;
                await StopLoopAsync();
                operationToken.ThrowIfCancellationRequested();
                if (changed)
                {
                    await RetireTargetAsync();
                    operationToken.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        _target = target;
                        _generation++;
                        if (!Equals(_snapshotTarget, target))
                        {
                            Snapshot = null;
                            _snapshotTarget = null;
                            LastCapturedAt = null;
                            LastAttemptedAt = null;
                            LastError = null;
                        }
                        if (target is not null)
                        {
                            _targetLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                            _targetToken = _targetLifetime.Token;
                        }
                    }
                    Notify();
                }
                if (target is not null && (_enabled || captureWhenDisabled))
                {
                    await CaptureCoreAsync(operationToken, true);
                    operationToken.ThrowIfCancellationRequested();
                    StartLoop();
                }
            }
            finally { _controls.Release(); }
        }, token);

    public Task SetEnabledAsync(bool value, CancellationToken token = default) =>
        Admit(async operationToken =>
        {
            await _controls.WaitAsync(operationToken);
            try
            {
                if (_enabled == value) return;
                await StopLoopAsync();
                operationToken.ThrowIfCancellationRequested();
                _enabled = value;
                Notify();
                if (_enabled && _target is not null)
                {
                    await CaptureCoreAsync(operationToken, true);
                    operationToken.ThrowIfCancellationRequested();
                    StartLoop();
                }
            }
            finally { _controls.Release(); }
        }, token);

    public Task SetIntervalAsync(int seconds, CancellationToken token = default) =>
        Admit(async operationToken =>
        {
            await _controls.WaitAsync(operationToken);
            try
            {
                var interval = NormalizeInterval(TimeSpan.FromSeconds(seconds));
                if (_interval == interval) return;
                await StopLoopAsync();
                operationToken.ThrowIfCancellationRequested();
                _interval = interval;
                Notify();
                StartLoop();
            }
            finally { _controls.Release(); }
        }, token);

    public Task<bool> CaptureAsync(CancellationToken token = default) =>
        Admit(operationToken => CaptureCoreAsync(operationToken, true), token);

    public Task DeactivateAsync() => Admit(async token =>
    {
        await _controls.WaitAsync(token);
        try { await StopLoopAsync(); }
        finally { _controls.Release(); }
    }, CancellationToken.None);

    public void SetSnapshot(TSnapshot? snapshot, KafkaSamplingTarget? target = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (snapshot is null)
            {
                Snapshot = null;
                _snapshotTarget = null;
                LastCapturedAt = null;
                LastAttemptedAt = null;
                LastError = null;
            }
            else
            {
                if (!Equals(_snapshotTarget, target))
                {
                    Snapshot = null;
                    LastCapturedAt = null;
                }
                _snapshotTarget = target;
                LastAttemptedAt = capturedAt(snapshot);
                LastError = diagnostic(snapshot);
                if (isUsable(snapshot))
                {
                    Snapshot = snapshot;
                    _snapshotTarget = target;
                    LastCapturedAt = LastAttemptedAt;
                }
            }
        }
        Notify();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] admitted;
        lock (_sync)
        {
            if (_retirement is not null) return new ValueTask(_retirement);
            _retired = true;
            _generation++;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            admitted = [.. _admitted];
        }
        _ = CompleteRetirementAsync(admitted, completion);
        return new ValueTask(completion.Task);
    }

    private Task Admit(Func<CancellationToken, Task> work, CancellationToken token) =>
        Admit(async operationToken => { await work(operationToken); return true; }, token);

    private Task<TResult> Admit<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken token)
    {
        TaskCompletionSource<TResult> completion;
        CancellationToken lifetimeToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _admitted.Add(completion.Task);
            lifetimeToken = _lifetime.Token;
        }
        _ = CompleteAdmittedAsync(work, token, lifetimeToken, completion);
        return completion.Task;
    }

    private async Task CompleteAdmittedAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken token,
        CancellationToken lifetimeToken,
        TaskCompletionSource<TResult> completion)
    {
        try
        {
            TResult result;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken))
            {
                linked.Token.ThrowIfCancellationRequested();
                result = await work(linked.Token);
                // Parent cancellation is visible before CancelAsync finishes linked callbacks.
                token.ThrowIfCancellationRequested();
                lifetimeToken.ThrowIfCancellationRequested();
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally { lock (_sync) _admitted.Remove(completion.Task); }
    }

    private async Task<bool> CaptureCoreAsync(CancellationToken token, bool waitForSlot)
    {
        KafkaSamplingTarget? target;
        CancellationToken targetToken;
        long generation;
        lock (_sync)
        {
            if (_retired) return false;
            target = _target;
            targetToken = _targetToken;
            generation = _generation;
        }
        if (target is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, targetToken);
        var captureToken = linked.Token;
        if (waitForSlot) await _captures.WaitAsync(captureToken);
        else if (!await _captures.WaitAsync(0, captureToken)) return false;
        try
        {
            captureToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_retired || generation != _generation) return false;
                IsSampling = true;
            }
            Notify();
            var result = await capture(target, captureToken);
            // A cancellation-ignoring capture must still retire with its caller or target.
            token.ThrowIfCancellationRequested();
            targetToken.ThrowIfCancellationRequested();
            captureToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_retired || generation != _generation) return false;
                if (result.IsFailure())
                {
                    LastAttemptedAt = DateTimeOffset.UtcNow;
                    LastError = result.Message;
                    return false;
                }
                var snapshot = result.Data
                    ?? throw new InvalidOperationException("Kafka capture returned no snapshot.");
                _snapshotTarget = target;
                LastAttemptedAt = capturedAt(snapshot);
                LastError = diagnostic(snapshot);
                if (!isUsable(snapshot)) return false;
                Snapshot = snapshot;
                _snapshotTarget = target;
                LastCapturedAt = LastAttemptedAt;
                return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested
            || targetToken.IsCancellationRequested || captureToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (!_retired && generation == _generation)
                {
                    LastAttemptedAt = DateTimeOffset.UtcNow;
                    LastError = exception.GetMessageRecursively();
                }
            }
            return false;
        }
        finally
        {
            lock (_sync) IsSampling = false;
            _captures.Release();
            Notify();
        }
    }

    private void StartLoop()
    {
        PeriodicTimer timer;
        CancellationToken token;
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_retired || !_enabled || _target is null || _loop is { IsCompleted: false }) return;
            _loopLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _loopLifetime.Token;
            timer = new PeriodicTimer(_interval);
            _timer = timer;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = completion.Task;
        }
        _ = CompleteLoopAsync(timer, token, completion);
    }

    private async Task CompleteLoopAsync(PeriodicTimer timer, CancellationToken token, TaskCompletionSource completion)
    {
        try { await RunLoopAsync(timer, token); completion.TrySetResult(); }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task RunLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                await CaptureCoreAsync(token, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task StopLoopAsync()
    {
        var timer = _timer;
        var lifetime = _loopLifetime;
        var loop = _loop;
        _timer = null;
        _loopLifetime = null;
        _loop = null;
        timer?.Dispose();
        if (lifetime is null) return;
        var failures = new List<Exception>();
        try
        {
            try { await lifetime.CancelAsync(); }
            catch (Exception exception) { failures.Add(exception); }
            if (loop is not null)
            {
                try { await loop; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception exception) { failures.Add(exception); }
            }
        }
        finally { lifetime.Dispose(); }
        if (failures.Count > 0) throw new AggregateException("Kafka sampling loop retirement failed.", failures);
    }

    private async Task RetireTargetAsync()
    {
        var lifetime = _targetLifetime;
        if (lifetime is null) return;
        Exception? cancellationFailure = null;
        try { await lifetime.CancelAsync(); }
        catch (Exception exception) { cancellationFailure = exception; }
        await _captures.WaitAsync();
        _captures.Release();
        lifetime.Dispose();
        _targetLifetime = null;
        if (cancellationFailure is not null) throw new AggregateException("Kafka sampling target retirement failed.", cancellationFailure);
    }

    private async Task CompleteRetirementAsync(Task[] admitted, TaskCompletionSource completion)
    {
        try { await RetireAsync(admitted); completion.TrySetResult(); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task RetireAsync(Task[] admitted)
    {
        var failures = new List<Exception>();
        await ObserveAsync(_lifetime.CancelAsync());
        await _controls.WaitAsync();
        try
        {
            await ObserveAsync(StopLoopAsync());
            await ObserveAsync(RetireTargetAsync());
        }
        finally { _controls.Release(); }
        foreach (var operation in admitted) await ObserveAsync(operation);
        _captures.Dispose();
        _controls.Dispose();
        _lifetime.Dispose();
        if (failures.Count > 0) throw new AggregateException("Kafka sampling retirement failed.", failures);

        async Task ObserveAsync(Task task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private void Notify()
    {
        Action? subscribers;
        lock (_sync)
        {
            if (_retired) return;
            subscribers = StateChanged;
        }
        subscribers?.Invoke();
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval) => TimeSpan.FromSeconds(Math.Clamp(
        Math.Round(interval.TotalSeconds),
        KafkaPerformancePollingState.MinimumRefreshIntervalSeconds,
        KafkaPerformancePollingState.MaximumRefreshIntervalSeconds));
}
