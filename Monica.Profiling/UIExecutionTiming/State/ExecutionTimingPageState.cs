using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Profiling.ExecutionTiming.Facades;
using Monica.Profiling.ExecutionTiming.Models;

namespace Monica.Profiling.UIExecutionTiming.State;

/// <summary>
/// Owns a rendered page's retained timing snapshot and polling loop, not the host's measurements.
/// </summary>
/// <remarks>
/// Only one refresh is admitted at a time. Both facade reads must succeed before either list and its
/// capture time are replaced. Disposal drains admitted work; late calls are inert and cannot restart polling.
/// </remarks>
/// <param name="executionTimingFacade">The host-owned timing diagnostics facade.</param>
/// <param name="options">The page's polling configuration.</param>
/// <param name="timeProvider">The page clock, or the system clock when none is registered.</param>
public sealed class ExecutionTimingPageState(
    ExecutionTimingFacade executionTimingFacade,
    IOptions<ModuleExecutionTimingUIOption> options,
    TimeProvider? timeProvider = null)
    : IAsyncDisposable
{
    /// <summary>Creates page-owned state using the system clock.</summary>
    /// <param name="facade">The host-owned timing diagnostics facade.</param>
    /// <param name="configuration">The page polling configuration.</param>
    public ExecutionTimingPageState(ExecutionTimingFacade facade, IOptions<ModuleExecutionTimingUIOption> configuration)
        : this(facade, configuration, null)
    {
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshLoop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Notifies subscribers after a live page's loading, snapshot or polling state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Gets the completed statistics from the latest successful pair of reads.</summary>
    public IReadOnlyList<ExecutionTimingStatistics> Statistics { get; private set; } = [];

    /// <summary>Gets running invocation evidence from the latest successful pair of reads.</summary>
    public IReadOnlyList<RunningExecutionTimingInfo> RunningOperations { get; private set; } = [];

    /// <summary>Gets whether an admitted facade read is in progress.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Gets whether at least one pair of reads succeeded, including a genuinely empty snapshot.</summary>
    public bool HasSnapshot { get; private set; }

    /// <summary>Gets the UTC page-clock time after the latest successful pair of reads.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; private set; }

    /// <summary>
    /// Gets the latest actual read failure. Null means no outstanding failure; an empty message is still a failure.
    /// A failed refresh retains both lists and their capture time until a later success clears the error.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>Gets the configured page polling interval. Non-positive values require manual refresh.</summary>
    public int RefreshIntervalMs { get; } = options.Value.AutoRefreshIntervalMs;

    /// <summary>Gets whether this page's configuration permits timer-based refresh.</summary>
    public bool CanAutoRefresh => RefreshIntervalMs > 0;

    /// <summary>Gets the effective polling preference; manual-only pages always return false.</summary>
    public bool AutoRefresh { get; private set; } = options.Value.AutoRefreshIntervalMs > 0;

    /// <summary>
    /// Captures current evidence and starts configured polling. A busy or disposed page returns Conflict
    /// with an empty message; it does not change the retained snapshot or error.
    /// </summary>
    public async Task<Result> InitializeAsync()
    {
        var result = await RefreshAsync();
        await UpdateAutoRefreshAsync();
        return result;
    }

    /// <summary>
    /// Serializes polling transitions. Pausing preserves the snapshot and does not stop host measurements.
    /// Enabling a manual-only or disposed page is inert.
    /// </summary>
    public Task SetAutoRefreshAsync(bool value) => UpdateAutoRefreshAsync(value);

    /// <summary>
    /// Captures both timing lists without changing the polling preference. Concurrent or disposed calls
    /// return Conflict with an empty message. Actual query failures retain the previous successful snapshot.
    /// </summary>
    public Task<Result> RefreshAsync() => Task.FromResult(Refresh());

    /// <summary>
    /// Stops and drains the page loop and any admitted read. Repeated calls share the same completion.
    /// The facade and host-owned timing collector remain available to other pages.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                AutoRefresh = false;
                IsLoading = false;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private Result Refresh(CancellationToken pollingCancellation = default)
    {
        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested || !_refreshGate.Wait(0))
            {
                return InactiveResult();
            }

            IsLoading = true;
        }

        try
        {
            NotifyStateChanged();
            return LoadData(pollingCancellation);
        }
        finally
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    IsLoading = false;
                }
            }

            _refreshGate.Release();
            NotifyStateChanged();
        }
    }

    private Result LoadData(CancellationToken pollingCancellation)
    {
        lock (_sync)
        {
            // A loading subscriber can retire the page before the first synchronous query begins.
            if (_disposed || pollingCancellation.IsCancellationRequested)
            {
                return InactiveResult();
            }
        }

        var statisticsResult = executionTimingFacade.GetStatistics();
        var statisticsError = statisticsResult.WithoutData();
        var statistics = statisticsResult.Data;
        if (statisticsResult.IsFailure())
        {
            return RetainFailure(statisticsError, pollingCancellation);
        }
        if (statistics is null)
            return RetainFailure(Result.Failure("The execution timing statistics were unavailable."), pollingCancellation);

        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested)
            {
                return InactiveResult();
            }
        }

        var runningResult = executionTimingFacade.GetRunningOperations();
        var runningError = runningResult.WithoutData();
        var runningOperations = runningResult.Data;
        if (runningResult.IsFailure())
        {
            return RetainFailure(runningError, pollingCancellation);
        }
        if (runningOperations is null)
            return RetainFailure(Result.Failure("The running operations were unavailable."), pollingCancellation);

        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested)
            {
                return InactiveResult();
            }

            // These are two diagnostic reads, not an atomic collector transaction.
            var capturedAt = _timeProvider.GetUtcNow();
            Statistics = statistics;
            RunningOperations = runningOperations;
            SnapshotCapturedAt = capturedAt;
            HasSnapshot = true;
            LoadError = null;
            return Result.Success();
        }
    }

    private Result RetainFailure(Result error, CancellationToken pollingCancellation)
    {
        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested)
            {
                return InactiveResult();
            }

            LoadError = error.Message ?? string.Empty;
            return error;
        }
    }

    private async Task UpdateAutoRefreshAsync(bool? requestedValue = null)
    {
        await _transitionGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (requestedValue.HasValue)
                {
                    AutoRefresh = requestedValue.Value && CanAutoRefresh;
                }
            }

            NotifyStateChanged();

            if (!AutoRefresh)
            {
                await StopRefreshLoopAsync();
                return;
            }

            lock (_sync)
            {
                if (!_disposed && _refreshCancellation is null)
                {
                    _refreshCancellation = new CancellationTokenSource();
                    _refreshLoop = RunRefreshLoopAsync(_refreshCancellation.Token);
                }
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RefreshIntervalMs), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Refresh(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // Only the serialized transition owner calls this method. The loop never acquires that gate.
    private async Task StopRefreshLoopAsync()
    {
        var cancellation = _refreshCancellation;
        var refreshLoop = _refreshLoop;
        _refreshCancellation = null;
        cancellation?.Cancel();

        try
        {
            await refreshLoop;
        }
        finally
        {
            cancellation?.Dispose();
            _refreshLoop = Task.CompletedTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _transitionGate.WaitAsync();
        try
        {
            await StopRefreshLoopAsync();
        }
        finally
        {
            _transitionGate.Release();
        }

        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        lock (_sync)
        {
            StateChanged = null;
        }
    }

    private void NotifyStateChanged()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                StateChanged?.Invoke();
            }
        }
    }

    private static Result InactiveResult() => Result.Failure(string.Empty, ResultStatus.Conflict);
}
