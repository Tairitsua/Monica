using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;

namespace Monica.Profiling.UIRuntimeMetrics.State;

/// <summary>
/// Owns one page's retained runtime metrics view and polling loop, not the host's collector.
/// </summary>
/// <remarks>
/// Only one trend query is admitted at a time. Successful reads detach the trend list and update the
/// previous/current points together with their view capture time. Failed reads retain that evidence.
/// Disposal drains admitted work; late calls are inert and cannot restart polling.
/// </remarks>
/// <param name="runtimeMetricsFacade">The scoped production runtime metrics facade.</param>
/// <param name="options">The page's polling configuration.</param>
/// <param name="timeProvider">The page clock, or the system clock when none is registered.</param>
public sealed class RuntimeMetricsPageState(
    RuntimeMetricsFacade runtimeMetricsFacade,
    IOptions<ModuleRuntimeMetricsUIOption> options,
    TimeProvider? timeProvider = null)
    : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Result<RuntimeMetricsTrend>> _getTrend = runtimeMetricsFacade.GetTrend;
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshLoop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates a page state with the system clock, preserving the original constructor contract.</summary>
    /// <param name="runtimeMetricsFacade">The scoped production runtime metrics facade.</param>
    /// <param name="options">The page polling configuration.</param>
    public RuntimeMetricsPageState(RuntimeMetricsFacade runtimeMetricsFacade, IOptions<ModuleRuntimeMetricsUIOption> options)
        : this(runtimeMetricsFacade, options, null)
    {
    }

    // A page-local read boundary permits a held/failing read without replacing the sealed facade or collector.
    // Production constructors always use the real scoped facade's GetTrend method.
    internal RuntimeMetricsPageState(
        RuntimeMetricsFacade runtimeMetricsFacade,
        IOptions<ModuleRuntimeMetricsUIOption> options,
        TimeProvider? timeProvider,
        Func<Result<RuntimeMetricsTrend>> getTrend)
        : this(runtimeMetricsFacade, options, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(getTrend);
        _getTrend = getTrend;
    }

    /// <summary>Notifies subscribers after a live page's loading, snapshot or polling state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Gets the latest point in the successful retained trend, or null for a successful empty history.</summary>
    public RuntimeMetricsPoint? CurrentDataPoint { get; private set; }

    /// <summary>
    /// Gets the latest point from the previous successful view read, including an unchanged latest sample.
    /// This is not necessarily the preceding collector sample; failures and rejected reads leave it unchanged.
    /// </summary>
    public RuntimeMetricsPoint? PreviousDataPoint { get; private set; }

    /// <summary>Gets the detached trend list and its original collector configuration from the last success.</summary>
    public RuntimeMetricsTrend? TrendData { get; private set; }

    /// <summary>Gets whether an admitted facade read is in progress.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Gets whether a trend read succeeded, including a genuinely empty retained history.</summary>
    public bool HasSnapshot { get; private set; }

    /// <summary>Gets the UTC page-clock time of the latest successful view read, not the latest sample time.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; private set; }

    /// <summary>
    /// Gets the latest actual read failure. Null means no outstanding failure; an empty message is still a failure.
    /// A failed refresh retains the trend, both latest points and their capture time until a later success.
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
    /// Reads retained history without forcing a new sample or changing the polling preference. Concurrent or disposed calls
    /// return Conflict with an empty message. Actual query failures retain the previous successful snapshot.
    /// </summary>
    public Task<Result> RefreshAsync() => Task.FromResult(Refresh());

    /// <summary>
    /// Stops and drains the page loop and any admitted read. Repeated calls share the same completion.
    /// The scoped facade and host-owned collector remain available to other pages.
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

        var trendResult = _getTrend();
        var trendError = trendResult.WithoutData();
        var trend = trendResult.Data;
        if (trendResult.IsFailure())
        {
            return RetainFailure(trendError, pollingCancellation);
        }
        if (trend is null)
            return RetainFailure(Result.Failure("The runtime metrics trend was unavailable."), pollingCancellation);

        var detachedTrend = new RuntimeMetricsTrend
        {
            DataPoints = [.. trend.DataPoints],
            MaxHistoryPoints = trend.MaxHistoryPoints,
            SampleIntervalMs = trend.SampleIntervalMs
        };

        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested)
            {
                return InactiveResult();
            }

            var capturedAt = _timeProvider.GetUtcNow();
            PreviousDataPoint = CurrentDataPoint;
            CurrentDataPoint = detachedTrend.DataPoints.LastOrDefault();
            TrendData = detachedTrend;
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
