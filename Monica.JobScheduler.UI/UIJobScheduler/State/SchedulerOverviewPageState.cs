using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>
/// Creates component-owned scheduler overview sessions from circuit-scoped dependencies.
/// </summary>
internal sealed class SchedulerOverviewPageStateFactory(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a fresh overview state owned by one rendered page.
    /// </summary>
    public SchedulerOverviewPageState Create(TimeSpan refreshInterval) =>
        new(facade.GetOverviewAsync, access.IsAuthorizedAsync, timeProvider, refreshInterval);
}

/// <summary>
/// Owns one scheduler overview snapshot, authorization boundary, refresh loop, and async lifetime.
/// </summary>
/// <remarks>
/// Initialization creates at most one refresh loop. Concurrent refresh requests join the same actual load.
/// Retirement rejects new work, cancels and drains admitted initialization, refresh, and loop operations,
/// then releases their resources. Delegates and timer work run outside the admission gate.
/// </remarks>
public sealed class SchedulerOverviewPageState : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Res<JobSchedulerOverview>>> _loadOverview;
    private readonly Func<CancellationToken, Task<bool>> _authorize;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _admissionGate = new();
    private PeriodicTimer? _timer;
    private TaskCompletionSource? _initialization;
    private TaskCompletionSource? _refresh;
    private TaskCompletionSource? _refreshLoop;
    private TaskCompletionSource? _retirement;
    private bool _retired;

    internal SchedulerOverviewPageState(
        Func<CancellationToken, Task<Res<JobSchedulerOverview>>> loadOverview,
        Func<CancellationToken, Task<bool>> authorize,
        TimeProvider timeProvider,
        TimeSpan refreshInterval)
    {
        _loadOverview = loadOverview;
        _authorize = authorize;
        _timeProvider = timeProvider;
        _refreshInterval = refreshInterval;
        AutoRefreshEnabled = refreshInterval > TimeSpan.Zero;
        _lifetimeToken = _lifetimeCancellation.Token;
    }

    /// <summary>
    /// Raised after an accepted state transition.
    /// </summary>
    /// <remarks>
    /// Handlers run outside the admission gate and must reject callbacks on retired UI owners.
    /// A handler must not await this state's current refresh or retirement, which await handler completion.
    /// </remarks>
    public event Func<Task>? Changed;

    /// <summary>
    /// Gets the latest bounded scheduler overview.
    /// </summary>
    public JobSchedulerOverview? Overview { get; private set; }

    /// <summary>Gets whether future timer ticks may admit a refresh. An already admitted read is allowed to finish.</summary>
    public bool AutoRefreshEnabled { get; private set; } = true;

    /// <summary>Pauses or resumes timer-triggered refreshes without changing the accepted snapshot or timer ownership.</summary>
    public void SetAutoRefreshEnabled(bool enabled)
    {
        lock (_admissionGate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            AutoRefreshEnabled = enabled && _refreshInterval > TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets whether the first access check completed.
    /// </summary>
    public bool AccessChecked { get; private set; }

    /// <summary>
    /// Gets whether the current circuit is authorized.
    /// </summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>
    /// Gets whether a refresh currently owns the facade boundary.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Gets the most recent load failure while preserving any prior snapshot.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Gets when the latest snapshot was accepted.
    /// </summary>
    public DateTimeOffset? LastRefreshedUtc { get; private set; }

    /// <summary>
    /// Performs the first access-checked load and starts one automatic refresh loop when enabled.
    /// </summary>
    /// <remarks>Repeated calls share initialization completion. A retired state admits no new work.</remarks>
    public Task InitializeAsync()
    {
        TaskCompletionSource completion;
        lock (_admissionGate)
        {
            if (_retired) return Task.CompletedTask;
            if (_initialization is not null) return _initialization.Task;
            completion = NewCompletion();
            _initialization = completion;
        }

        _ = CompleteInitializationAsync(completion);
        return completion.Task;
    }

    /// <summary>
    /// Re-evaluates authorization and refreshes the operational snapshot.
    /// </summary>
    /// <remarks>
    /// Concurrent callers share one load and its completion. A failed refresh preserves earlier evidence;
    /// a retired state returns without accessing released resources or starting another load.
    /// </remarks>
    public Task RefreshAsync()
    {
        TaskCompletionSource completion;
        lock (_admissionGate)
        {
            if (_retired) return Task.CompletedTask;
            if (_refresh is not null) return _refresh.Task;
            completion = NewCompletion();
            _refresh = completion;
        }

        _ = CompleteRefreshAsync(completion);
        return completion.Task;
    }

    private async Task CompleteInitializationAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await RefreshAsync();
            if (_refreshInterval > TimeSpan.Zero)
            {
                lock (_admissionGate)
                {
                    if (_retired) return;
                }

                var timer = new PeriodicTimer(_refreshInterval, _timeProvider);
                TaskCompletionSource? loopCompletion = null;
                lock (_admissionGate)
                {
                    if (!_retired)
                    {
                        _timer = timer;
                        loopCompletion = NewCompletion();
                        _refreshLoop = loopCompletion;
                    }
                }

                if (loopCompletion is null) timer.Dispose();
                else _ = CompleteRefreshLoopAsync(timer, loopCompletion);
            }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Complete(completion, failure);
        }
    }

    private async Task CompleteRefreshAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            lock (_admissionGate)
            {
                if (_retired) return;
                IsLoading = true;
            }
            await NotifyChangedAsync();

            var isAuthorized = await _authorize(_lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            lock (_admissionGate)
            {
                if (_retired) return;
                IsAuthorized = isAuthorized;
                AccessChecked = true;
                if (!isAuthorized)
                {
                    Overview = null;
                    Error = null;
                    return;
                }
            }

            var result = await _loadOverview(_lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var overview))
            {
                lock (_admissionGate)
                {
                    if (!_retired) Error = error.Message;
                }
                return;
            }

            var refreshedAtUtc = _timeProvider.GetUtcNow();
            lock (_admissionGate)
            {
                if (_retired) return;
                Overview = overview;
                Error = null;
                LastRefreshedUtc = refreshedAtUtc;
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            bool notify;
            lock (_admissionGate)
            {
                IsLoading = false;
                notify = !_retired;
            }
            if (notify)
            {
                try { await NotifyChangedAsync(); }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
            }

            lock (_admissionGate)
            {
                Complete(completion, failure);
                _refresh = null;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        PeriodicTimer? timer;
        Task[] operations;
        lock (_admissionGate)
        {
            if (_retirement is not null) return new ValueTask(_retirement.Task);
            _retired = true;
            Changed = null;
            completion = NewCompletion();
            _retirement = completion;
            timer = _timer;
            operations = new[] { _initialization?.Task, _refresh?.Task, _refreshLoop?.Task }
                .OfType<Task>().ToArray();
        }

        _ = CompleteRetirementAsync(timer, operations, completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteRetirementAsync(
        PeriodicTimer? timer, Task[] operations, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { await _lifetimeCancellation.CancelAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        try { timer?.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        foreach (var operation in operations)
        {
            try { await operation; }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { _lifetimeCancellation.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }

        Complete(completion, failures.Count == 0
            ? null
            : new AggregateException("Scheduler overview retirement failed.", failures));
    }

    private async Task CompleteRefreshLoopAsync(PeriodicTimer timer, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            while (await timer.WaitForNextTickAsync(_lifetimeToken))
            {
                bool enabled;
                lock (_admissionGate) enabled = AutoRefreshEnabled;
                if (enabled) await RefreshAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // Expected during page navigation or host shutdown.
        }
        catch (Exception exception) { failure = exception; }
        finally { Complete(completion, failure); }
    }

    private async Task NotifyChangedAsync()
    {
        Func<Task>[] handlers;
        lock (_admissionGate)
        {
            handlers = _retired ? [] : Changed?.GetInvocationList().Cast<Func<Task>>().ToArray() ?? [];
        }
        foreach (var handler in handlers)
        {
            await handler();
        }
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Complete(TaskCompletionSource completion, Exception? failure)
    {
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }
}
