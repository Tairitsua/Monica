using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.Abstractions;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>
/// Creates component-owned scheduler runtime sessions from circuit-scoped dependencies.
/// </summary>
internal sealed class SchedulerRuntimePageStateFactory(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    TimeProvider timeProvider,
    IServiceProvider serviceProvider)
{
    /// <summary>
    /// Creates a fresh runtime state owned by one rendered page. Worker-instance insight is attached only when an
    /// integration provider is registered; otherwise the page degrades to its setup hint.
    /// </summary>
    public SchedulerRuntimePageState Create(TimeSpan refreshInterval) =>
        new(
            facade.GetRuntimeOverviewAsync,
            access.IsAuthorizedAsync,
            timeProvider,
            refreshInterval,
            serviceProvider.GetService<IJobSchedulerWorkerInsightProvider>());
}

/// <summary>
/// Owns one scheduler runtime snapshot, its optional worker-instance insight, authorization boundary, refresh
/// loop, and async lifetime.
/// </summary>
/// <remarks>
/// Concurrent refreshes join one authorization and read round. Core and optional registry evidence are accepted
/// independently; a failed refresh retains the corresponding prior evidence and its acceptance time. Denied or
/// failed authorization clears both evidence planes. Subscriber publication is owned separately from the shared
/// read completion, allowing a subscriber to await another refresh without joining its own notification.
/// Subscribers must not await initialization or retirement that is waiting for those subscribers to complete.
/// A subscriber that requests another refresh must bound its reentry to avoid an unconditional feedback loop.
/// </remarks>
public sealed class SchedulerRuntimePageState : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Result<JobSchedulerRuntimeOverview>>> _loadOverview;
    private readonly Func<CancellationToken, Task<bool>> _authorize;
    private readonly IJobSchedulerWorkerInsightProvider? _workerInsight;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly SchedulerOperationLifetime _lifetime = new();
    private TaskCompletionSource? _initialization;
    private TaskCompletionSource? _refreshRead;
    private PeriodicTimer? _timer;
    private Task? _refreshLoop;
    private bool _isLoading;

    internal SchedulerRuntimePageState(
        Func<CancellationToken, Task<Result<JobSchedulerRuntimeOverview>>> loadOverview,
        Func<CancellationToken, Task<bool>> authorize,
        TimeProvider timeProvider,
        TimeSpan refreshInterval,
        IJobSchedulerWorkerInsightProvider? workerInsight)
    {
        _loadOverview = loadOverview;
        _authorize = authorize;
        _timeProvider = timeProvider;
        _refreshInterval = refreshInterval;
        _workerInsight = workerInsight;
    }

    /// <summary>
    /// Raised after an accepted state transition.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// Gets the latest host-scoped runtime snapshot.
    /// </summary>
    public JobSchedulerRuntimeOverview? Overview { get; private set; }

    /// <summary>Gets whether a valid core snapshot, including an empty owner inventory, has been accepted.</summary>
    public bool HasLoadedSnapshot { get; private set; }

    /// <summary>Gets whether the latest core read or authorization provider failed.</summary>
    /// <remarks>A failed or null result remains explicit even when it supplies no diagnostic message.</remarks>
    public bool IsQueryFailed { get; private set; }

    /// <summary>
    /// Gets the latest worker-instance insight; empty until a provider-backed refresh succeeds.
    /// </summary>
    public IReadOnlyList<JobSchedulerWorkerInstanceView> Workers { get; private set; } = [];

    /// <summary>
    /// Gets whether a worker-instance insight provider is registered for this host.
    /// </summary>
    public bool HasWorkerInsight => _workerInsight is not null;

    /// <summary>Gets whether a valid optional registry list, including an empty one, has been accepted.</summary>
    /// <remarks>A missing provider does not constitute a successful empty registry read.</remarks>
    public bool HasLoadedWorkerInsight { get; private set; }

    /// <summary>Gets whether the latest configured registry read failed or returned no list.</summary>
    public bool IsWorkerInsightFailed { get; private set; }

    /// <summary>Gets when the independently accepted registry list was observed by this session.</summary>
    /// <remarks>Core refreshes and failed registry reads do not advance this time or reclassify retained rows.</remarks>
    public DateTimeOffset? WorkerInsightObservedAtUtc { get; private set; }

    /// <summary>
    /// Gets the most recent worker-insight load failure while preserving any prior instance list.
    /// </summary>
    public string? WorkerInsightError { get; private set; }

    /// <summary>
    /// Gets whether the latest access provider returned a decision, including denial.
    /// </summary>
    /// <remarks>An access provider fault sets this to false and clears inaccessible evidence.</remarks>
    public bool AccessChecked { get; private set; }

    /// <summary>
    /// Gets whether the current circuit is authorized.
    /// </summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>
    /// Gets whether a refresh currently owns the facade boundary.
    /// </summary>
    public bool IsLoading => !IsDisposed && _isLoading;

    /// <summary>
    /// Gets the most recent runtime load failure while preserving any prior snapshot.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Gets when the latest valid core snapshot was accepted by this session.
    /// </summary>
    /// <remarks>Use <see cref="JobSchedulerRuntimeOverview.CapturedAtUtc"/> for owner-observation freshness.
    /// A failed refresh retains this acceptance time; lost access clears it.</remarks>
    public DateTimeOffset? LastRefreshedUtc { get; private set; }

    /// <summary>
    /// Performs one shared first load and starts one automatic refresh loop when the interval is positive.
    /// </summary>
    /// <remarks>Repeated initialization joins its original completion, including a fault. A nonpositive interval
    /// disables automatic refresh. Subscribers are published before the loop starts.</remarks>
    /// <exception cref="OperationCanceledException">The session has retired or an admitted read is canceled.</exception>
    public Task InitializeAsync() => _lifetime.RunAsync(JoinInitializationAsync);

    private Task JoinInitializationAsync(CancellationToken token)
    {
        TaskCompletionSource? completion = null;
        var startsInitialization = false;
        if (!_lifetime.TryApply(() =>
            {
                if (_initialization is null)
                {
                    _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    startsInitialization = true;
                }
                completion = _initialization;
            })) throw new OperationCanceledException(token);
        return startsInitialization ? CompleteInitializationAsync(completion!, token) : completion!.Task;
    }

    private async Task CompleteInitializationAsync(TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            await RefreshAsync();
            token.ThrowIfCancellationRequested();
            if (_refreshInterval > TimeSpan.Zero)
            {
                var timer = new PeriodicTimer(_refreshInterval, _timeProvider);
                if (!_lifetime.TryApply(() => _timer = timer))
                {
                    timer.Dispose();
                    throw new OperationCanceledException(token);
                }

                // Initialization owns publication of this task until retirement can safely observe it.
                _refreshLoop = _lifetime.RunAsync(loopToken => RunRefreshLoopAsync(timer, loopToken));
            }
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
        await completion.Task;
    }

    /// <summary>
    /// Re-evaluates authorization and refreshes the runtime snapshot and optional worker insight.
    /// </summary>
    /// <remarks>Overlapping calls share the actual reads. The initiating call also awaits its loading and final
    /// notifications; joiners await the read completion. Every notification remains owned until retirement drains it.
    /// Each authorized round attempts both producers independently. Provider faults become panel diagnostics;
    /// subscriber faults remain observable to the initiating caller.</remarks>
    /// <exception cref="OperationCanceledException">The session has retired or an admitted read is canceled.</exception>
    public Task RefreshAsync() => _lifetime.RunAsync(JoinRefreshAsync);

    private Task JoinRefreshAsync(CancellationToken token)
    {
        TaskCompletionSource? completion = null;
        var startsRead = false;
        if (!_lifetime.TryApply(() =>
            {
                if (_refreshRead is null)
                {
                    _refreshRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _isLoading = true;
                    startsRead = true;
                }
                completion = _refreshRead;
            })) throw new OperationCanceledException(token);
        return startsRead ? CompleteRefreshAsync(completion!, token) : completion!.Task;
    }

    private async Task CompleteRefreshAsync(TaskCompletionSource completion, CancellationToken token)
    {
        // A loading subscriber can join this read. Awaiting it before producing the read would deadlock.
        var loadingNotification = _lifetime.RunAsync(_ => NotifyChangedAsync());
        Exception? readFailure = null;
        try
        {
            await ReadAsync(token);
        }
        catch (Exception exception) { readFailure = exception; }
        finally
        {
            if (!_lifetime.TryApply(() =>
                {
                    _refreshRead = null;
                    _isLoading = false;
                }) && readFailure is null) readFailure = new OperationCanceledException(token);
            if (readFailure is OperationCanceledException canceled)
                completion.TrySetCanceled(canceled.CancellationToken);
            else if (readFailure is not null) completion.TrySetException(readFailure);
            else completion.TrySetResult();
        }

        var failures = new List<Exception>();
        OperationCanceledException? cancellation = null;
        await ObserveAsync(completion.Task);
        await ObserveAsync(loadingNotification);
        if (!token.IsCancellationRequested) await ObserveAsync(NotifyChangedAsync());
        if (failures.Count != 0) ThrowFailures(failures);
        if (cancellation is not null) ExceptionDispatchInfo.Capture(cancellation).Throw();

        async Task ObserveAsync(Task task)
        {
            try { await task; }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested)
            {
                cancellation ??= exception;
            }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private async Task ReadAsync(CancellationToken token)
    {
        bool authorized;
        try
        {
            authorized = await _authorize(token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            Apply(() =>
            {
                ClearEvidence();
                AccessChecked = false;
                IsAuthorized = false;
                IsQueryFailed = true;
                Error = exception.Message;
            }, token);
            return;
        }

        Apply(() =>
        {
            AccessChecked = true;
            IsAuthorized = authorized;
            if (!authorized) ClearEvidence();
        }, token);
        if (!authorized) return;

        await RefreshOverviewAsync(token);
        await RefreshWorkerInsightAsync(token);
    }

    private async Task RefreshOverviewAsync(CancellationToken token)
    {
        try
        {
            var result = await _loadOverview(token);
            token.ThrowIfCancellationRequested();
            var overview = result.Data;
            if (result.IsFailure())
            {
                Apply(() => { IsQueryFailed = true; Error = result.Message; }, token);
                return;
            }
            if (overview is null)
            {
                Apply(() => { IsQueryFailed = true; Error = null; }, token);
                return;
            }
            var observedAt = _timeProvider.GetUtcNow();
            Apply(() =>
            {
                Overview = overview;
                HasLoadedSnapshot = true;
                IsQueryFailed = false;
                Error = null;
                LastRefreshedUtc = observedAt;
            }, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            Apply(() => { IsQueryFailed = true; Error = exception.Message; }, token);
        }
    }

    // Worker insight is an optional enrichment: its failure degrades only its own panel and never the runtime
    // snapshot that the rest of the page renders.
    private async Task RefreshWorkerInsightAsync(CancellationToken token)
    {
        if (_workerInsight is null)
        {
            return;
        }

        try
        {
            var workers = await _workerInsight.GetWorkerInstancesAsync(token);
            token.ThrowIfCancellationRequested();
            if (workers is null)
            {
                Apply(() => { IsWorkerInsightFailed = true; WorkerInsightError = null; }, token);
                return;
            }
            var observedAt = _timeProvider.GetUtcNow();
            Apply(() =>
            {
                Workers = workers;
                HasLoadedWorkerInsight = true;
                IsWorkerInsightFailed = false;
                WorkerInsightError = null;
                WorkerInsightObservedAtUtc = observedAt;
            }, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            Apply(() => { IsWorkerInsightFailed = true; WorkerInsightError = exception.Message; }, token);
        }
    }

    private void ClearEvidence()
    {
        Overview = null;
        HasLoadedSnapshot = false;
        LastRefreshedUtc = null;
        IsQueryFailed = false;
        Error = null;
        Workers = [];
        HasLoadedWorkerInsight = false;
        WorkerInsightObservedAtUtc = null;
        IsWorkerInsightFailed = false;
        WorkerInsightError = null;
    }

    private void Apply(Action update, CancellationToken token)
    {
        if (!_lifetime.TryApply(update)) throw new OperationCanceledException(token);
    }

    /// <summary>Gets whether retirement has begun and new admissions are canceled.</summary>
    public bool IsDisposed => _lifetime.IsRetired;

    /// <summary>Cancels new admissions and joins the actual completion of all reads, notifications, and the loop.</summary>
    /// <remarks>Repeated callers share retirement completion. Late producers cannot replace accepted evidence;
    /// cancellation or subscriber faults do not skip independent timer and lifetime cleanup.</remarks>
    /// <exception cref="AggregateException">Cancellation, admitted work, or independent cleanup failed.</exception>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync(async () =>
    {
        Changed = null;
        var failures = new List<Exception>();
        try { _timer?.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (_refreshLoop is not null)
        {
            try { await _refreshLoop; }
            catch (OperationCanceledException) when (IsDisposed) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) ThrowFailures(failures);
    });

    private async Task RunRefreshLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during page navigation or host shutdown.
        }
    }

    private async Task NotifyChangedAsync()
    {
        var handlers = _lifetime.SnapshotHandlers(Changed);
        var failures = new List<Exception>();
        foreach (var handler in handlers)
        {
            if (IsDisposed) break;
            try { await handler(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Scheduler runtime publication or cleanup failed.", failures);
    }
}
