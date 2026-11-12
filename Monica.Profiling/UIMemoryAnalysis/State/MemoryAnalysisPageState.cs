using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Profiling.MemoryDiagnostics.Facades;
using Monica.Profiling.MemoryDiagnostics.Models;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;

namespace Monica.Profiling.UIMemoryAnalysis.State;

/// <summary>Identifies an explicit diagnostic action against the current host process.</summary>
public enum MemoryDiagnosticAction
{
    /// <summary>A forced collection using the facade's blocking, non-compacting defaults.</summary>
    GarbageCollection,
    /// <summary>A GC dump requested from the host's configured provider.</summary>
    GcDump
}

/// <summary>Retains an action's actual outcome independently of subsequent evidence reads.</summary>
/// <param name="Action">The action that completed.</param>
/// <param name="Generation">The requested collection generation, or null for a dump.</param>
/// <param name="StartedAt">The UTC page-clock admission time.</param>
/// <param name="CompletedAt">The UTC page-clock completion time, before any follow-up read.</param>
/// <param name="Status">The original action result status.</param>
/// <param name="Message">The original message, including an empty or null message.</param>
/// <param name="ServerDumpPath">The actual server file path; this is not a download URL.</param>
public sealed record MemoryDiagnosticActionResult(
    MemoryDiagnosticAction Action,
    int? Generation,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ResultStatus Status,
    string? Message,
    string? ServerDumpPath)
{
    /// <summary>Gets whether the action succeeded, irrespective of its follow-up read.</summary>
    public bool IsSuccessful => Status is ResultStatus.Ok or ResultStatus.Created;
}

/// <summary>Owns one page's retained memory evidence, explicit actions and polling loop.</summary>
/// <remarks>
/// One read or action is admitted at a time. Successful views publish detached results from three
/// sequential facade reads, not an atomic backend transaction. Failures retain that successful view.
/// Retirement drains admitted work, including providers without cancellation, and never stops the
/// host's runtime collector or disposes borrowed facades.
/// </remarks>
/// <param name="memoryDiagnosticsFacade">The scoped production memory diagnostics facade.</param>
/// <param name="runtimeMetricsFacade">The scoped production runtime metrics facade.</param>
/// <param name="options">The host's polling and diagnostic action policy.</param>
/// <param name="timeProvider">The page clock, or the system clock when none is registered.</param>
/// <param name="autoRefresh">The initial polling preference; non-positive intervals always require manual reads.</param>
public sealed class MemoryAnalysisPageState(
    MemoryDiagnosticsFacade memoryDiagnosticsFacade,
    RuntimeMetricsFacade runtimeMetricsFacade,
    IOptions<ModuleMemoryAnalysisUIOption> options,
    TimeProvider? timeProvider = null,
    bool autoRefresh = true) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly ModuleMemoryAnalysisUIOption _option = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Result<MemorySnapshot>> _getSnapshot = memoryDiagnosticsFacade.GetCurrentSnapshot;
    private readonly Func<Result<GcDetails>> _getGcDetails = () => memoryDiagnosticsFacade.GetGcDetails();
    private readonly Func<Result<RuntimeMetricsTrend>> _getTrend = runtimeMetricsFacade.GetTrend;
    private readonly Func<int, Result> _forceGarbageCollection = generation => memoryDiagnosticsFacade.ForceGarbageCollection(generation);
    private readonly Func<Task<Result<string>>> _createGcDump = () => memoryDiagnosticsFacade.TriggerGcDumpAsync();
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshLoop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Preserves the original constructor using configured polling and the system clock.</summary>
    /// <param name="memoryDiagnosticsFacade">The scoped production memory diagnostics facade.</param>
    /// <param name="runtimeMetricsFacade">The scoped production runtime metrics facade.</param>
    /// <param name="options">The host's polling and diagnostic action policy.</param>
    public MemoryAnalysisPageState(
        MemoryDiagnosticsFacade memoryDiagnosticsFacade,
        RuntimeMetricsFacade runtimeMetricsFacade,
        IOptions<ModuleMemoryAnalysisUIOption> options)
        : this(memoryDiagnosticsFacade, runtimeMetricsFacade, options, null, true)
    {
    }

    // The page boundary holds/decorates genuine reads and simulates action outcomes in tests.
    // Public constructors always use real scoped facades; no collector or provider is substituted.
    internal MemoryAnalysisPageState(
        MemoryDiagnosticsFacade memoryDiagnosticsFacade,
        RuntimeMetricsFacade runtimeMetricsFacade,
        IOptions<ModuleMemoryAnalysisUIOption> options,
        TimeProvider? timeProvider,
        bool autoRefresh,
        Func<Result<MemorySnapshot>> getSnapshot,
        Func<Result<GcDetails>> getGcDetails,
        Func<Result<RuntimeMetricsTrend>> getTrend,
        Func<int, Result> forceGarbageCollection,
        Func<Task<Result<string>>> createGcDump)
        : this(memoryDiagnosticsFacade, runtimeMetricsFacade, options, timeProvider, autoRefresh)
    {
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(getGcDetails);
        ArgumentNullException.ThrowIfNull(getTrend);
        ArgumentNullException.ThrowIfNull(forceGarbageCollection);
        ArgumentNullException.ThrowIfNull(createGcDump);
        _getSnapshot = getSnapshot;
        _getGcDetails = getGcDetails;
        _getTrend = getTrend;
        _forceGarbageCollection = forceGarbageCollection;
        _createGcDump = createGcDump;
    }

    /// <summary>Notifies subscribers when a live page's evidence, operation or polling state changes.</summary>
    public event Action? StateChanged;
    /// <summary>Gets the detached immediate observation from the last successful view read.</summary>
    public MemorySnapshot? CurrentSnapshot { get; private set; }
    /// <summary>Gets detached last-GC evidence; GCIndex zero is a valid observation.</summary>
    public GcDetails? CurrentGcDetails { get; private set; }
    /// <summary>Gets the detached runtime history; successful empty history is valid.</summary>
    public RuntimeMetricsTrend? TrendData { get; private set; }
    /// <summary>Gets whether an admitted read or action, including its follow-up read, is in progress.</summary>
    public bool IsLoading { get; private set; }
    /// <summary>Gets whether all three sequential view reads have succeeded at least once.</summary>
    public bool HasSnapshot { get; private set; }
    /// <summary>Gets the UTC page-clock time of the last successful three-read publication.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; private set; }
    /// <summary>
    /// Gets the latest actual read failure. Null means none; an empty message is still a failure.
    /// Failed reads retain all three prior observations and their successful capture time.
    /// </summary>
    public string? LoadError { get; private set; }
    /// <summary>Gets the configured polling interval; non-positive values require manual refresh.</summary>
    public int RefreshIntervalMs { get; } = options.Value.AutoRefreshIntervalMs;
    /// <summary>Gets whether configuration permits timer-based page reads.</summary>
    public bool CanAutoRefresh => RefreshIntervalMs > 0;
    /// <summary>Gets the effective polling preference, independent of host-owned collection.</summary>
    public bool AutoRefresh { get; private set; } = autoRefresh && options.Value.AutoRefreshIntervalMs > 0;
    /// <summary>Gets or sets the legacy tab selection; route owners do not use it for polling.</summary>
    public int ActiveTabIndex { get; set; }
    /// <summary>Gets the host options used to enforce diagnostic admission.</summary>
    public ModuleMemoryAnalysisUIOption Options => _option;
    /// <summary>Gets whether this host enabled its optional allocation workspace.</summary>
    public bool IsTypeAllocationTabEnabled => _option.EnableTypeAllocationTab;
    /// <summary>Gets whether an admitted diagnostic provider call is still running.</summary>
    public bool IsActionRunning { get; private set; }
    /// <summary>Gets the running action, or null during reads and after completion.</summary>
    public MemoryDiagnosticAction? ActiveAction { get; private set; }
    /// <summary>Gets the running action's UTC admission time, or null when none is running.</summary>
    public DateTimeOffset? ActionStartedAt { get; private set; }
    /// <summary>Gets immutable actual action evidence, unaffected by a later view-read failure.</summary>
    public MemoryDiagnosticActionResult? LastAction { get; private set; }

    /// <summary>Reads current evidence and starts the effective polling preference.</summary>
    public async Task<Result> InitializeAsync()
    {
        var result = await RefreshAsync();
        await UpdateAutoRefreshAsync();
        return result;
    }

    /// <summary>Serializes pause/resume; retired and manual-only pages cannot start a timer.</summary>
    public Task SetAutoRefreshAsync(bool value) => UpdateAutoRefreshAsync(value);

    /// <summary>
    /// Reads without forcing a collection. Busy or retired calls return Conflict with an empty
    /// message and leave prior evidence and failures unchanged.
    /// </summary>
    public Task<Result> RefreshAsync() => Task.FromResult(Refresh());

    /// <summary>
    /// Runs permitted collection, then attempts a view read on success. Its returned envelope and
    /// LastAction describe collection; LoadError describes a separate follow-up failure.
    /// </summary>
    /// <param name="generation">The requested generation, including -1 for a full collection.</param>
    public Task<Result> ForceGarbageCollectionAsync(int generation)
    {
        var admission = TryAdmit(MemoryDiagnosticAction.GarbageCollection);
        if (admission is not null) return Task.FromResult(admission);
        try
        {
            NotifyStateChanged();
            if (IsRetired()) return Task.FromResult(InactiveResult());
            var result = _forceGarbageCollection(generation);
            if (!CompleteAction(MemoryDiagnosticAction.GarbageCollection, generation, result.Status, result.Message, null))
                return Task.FromResult(InactiveResult());
            if (result.IsSuccess()) LoadData();
            return Task.FromResult(IsRetired() ? InactiveResult() : result);
        }
        finally
        {
            FinishOperation();
        }
    }

    /// <summary>
    /// Creates a permitted dump and retains its actual server path, then attempts a view read on
    /// success. Disposal drains the provider; the facade supplies no cancellation token.
    /// </summary>
    public async Task<Result<string>> CreateGcDumpAsync()
    {
        var admission = TryAdmit(MemoryDiagnosticAction.GcDump);
        if (admission is not null) return Result.FromFailure<string>(admission);
        try
        {
            NotifyStateChanged();
            if (IsRetired()) return Result.FromFailure<string>(InactiveResult());
            var result = await _createGcDump();
            if (!CompleteAction(MemoryDiagnosticAction.GcDump, null, result.Status, result.Message, result.IsSuccess() ? result.Data : null))
                return Result.FromFailure<string>(InactiveResult());
            if (result.IsSuccess()) LoadData();
            if (IsRetired()) return Result.FromFailure<string>(InactiveResult());
            return result;
        }
        finally
        {
            FinishOperation();
        }
    }

    /// <summary>
    /// Retires the page and shares one completion draining its loop and admitted read or action.
    /// Late calls cannot create timers, invoke facades, publish evidence or notify subscribers.
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
                IsActionRunning = false;
                ActiveAction = null;
                ActionStartedAt = null;
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private Result Refresh(CancellationToken pollingCancellation = default)
    {
        var admission = TryAdmit(pollingCancellation: pollingCancellation);
        if (admission is not null) return admission;
        try
        {
            NotifyStateChanged();
            return LoadData(pollingCancellation);
        }
        finally
        {
            FinishOperation();
        }
    }

    private Result? TryAdmit(MemoryDiagnosticAction? action = null, CancellationToken pollingCancellation = default)
    {
        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested) return InactiveResult();
            if (action == MemoryDiagnosticAction.GarbageCollection && !_option.AllowManualGC ||
                action == MemoryDiagnosticAction.GcDump && !_option.AllowGcDump)
                return Result.Failure(string.Empty, ResultStatus.Forbidden);
            if (!_operationGate.Wait(0)) return InactiveResult();
            IsLoading = true;
            IsActionRunning = action.HasValue;
            ActiveAction = action;
            ActionStartedAt = action.HasValue ? _timeProvider.GetUtcNow() : null;
            return null;
        }
    }

    private Result LoadData(CancellationToken pollingCancellation = default)
    {
        if (IsRetired(pollingCancellation)) return InactiveResult();
        var snapshotResult = _getSnapshot();
        var snapshot = snapshotResult.Data;
        if (snapshotResult.IsFailure())
            return RetainFailure(snapshotResult, pollingCancellation);
        if (snapshot is null)
            return RetainFailure(Result.Failure<MemorySnapshot>("The memory snapshot was unavailable."), pollingCancellation);
        if (IsRetired(pollingCancellation)) return InactiveResult();
        var gcResult = _getGcDetails();
        var gcDetails = gcResult.Data;
        if (gcResult.IsFailure())
            return RetainFailure(gcResult, pollingCancellation);
        if (gcDetails is null)
            return RetainFailure(Result.Failure<GcDetails>("The GC details were unavailable."), pollingCancellation);
        if (IsRetired(pollingCancellation)) return InactiveResult();
        var trendResult = _getTrend();
        var trend = trendResult.Data;
        if (trendResult.IsFailure())
            return RetainFailure(trendResult, pollingCancellation);
        if (trend is null)
            return RetainFailure(Result.Failure<RuntimeMetricsTrend>("The runtime metrics trend was unavailable."), pollingCancellation);
        var detachedSnapshot = Detach(snapshot);
        var detachedGcDetails = Detach(gcDetails);
        var detachedTrend = new RuntimeMetricsTrend
        {
            DataPoints = [.. trend.DataPoints],
            MaxHistoryPoints = trend.MaxHistoryPoints,
            SampleIntervalMs = trend.SampleIntervalMs
        };
        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested) return InactiveResult();
            CurrentSnapshot = detachedSnapshot;
            CurrentGcDetails = detachedGcDetails;
            TrendData = detachedTrend;
            SnapshotCapturedAt = _timeProvider.GetUtcNow();
            HasSnapshot = true;
            LoadError = null;
            return Result.Success();
        }
    }

    private Result RetainFailure<T>(Result<T> error, CancellationToken pollingCancellation)
    {
        lock (_sync)
        {
            if (_disposed || pollingCancellation.IsCancellationRequested) return InactiveResult();
            LoadError = error.Message ?? string.Empty;
            return Result.FromFailure(error);
        }
    }

    private bool CompleteAction(MemoryDiagnosticAction action, int? generation, ResultStatus status, string? message, string? serverDumpPath)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            LastAction = new MemoryDiagnosticActionResult(action, generation, ActionStartedAt!.Value,
                _timeProvider.GetUtcNow(), status, message, serverDumpPath);
            IsActionRunning = false;
            ActiveAction = null;
            ActionStartedAt = null;
        }
        NotifyStateChanged();
        return true;
    }

    private void FinishOperation()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                IsLoading = false;
                IsActionRunning = false;
                ActiveAction = null;
                ActionStartedAt = null;
            }
        }
        _operationGate.Release();
        NotifyStateChanged();
    }

    private bool IsRetired(CancellationToken pollingCancellation = default)
    {
        lock (_sync) return _disposed || pollingCancellation.IsCancellationRequested;
    }

    private async Task UpdateAutoRefreshAsync(bool? requestedValue = null)
    {
        await _transitionGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (requestedValue.HasValue) AutoRefresh = requestedValue.Value && CanAutoRefresh;
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
                if (cancellationToken.IsCancellationRequested) break;
                Refresh(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // The serialized transition owner stops the loop; the loop never acquires that gate.
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
        await _operationGate.WaitAsync();
        _operationGate.Release();
        lock (_sync) StateChanged = null;
    }

    private void NotifyStateChanged()
    {
        lock (_sync)
        {
            if (!_disposed) StateChanged?.Invoke();
        }
    }

    private static Result InactiveResult() => Result.Failure(string.Empty, ResultStatus.Conflict);

    private static MemorySnapshot Detach(MemorySnapshot value) => new()
    {
        Timestamp = value.Timestamp,
        WorkingSetBytes = value.WorkingSetBytes,
        TotalManagedMemory = value.TotalManagedMemory,
        GcHeapSizeBytes = value.GcHeapSizeBytes,
        CommittedBytes = value.CommittedBytes,
        Gen0Collections = value.Gen0Collections,
        Gen1Collections = value.Gen1Collections,
        Gen2Collections = value.Gen2Collections,
        IsServerGC = value.IsServerGC,
        LatencyMode = value.LatencyMode
    };

    private static GcDetails Detach(GcDetails value) => new()
    {
        GCIndex = value.GCIndex,
        Generation = value.Generation,
        WasCompacting = value.WasCompacting,
        WasConcurrent = value.WasConcurrent,
        HeapSizeBytes = value.HeapSizeBytes,
        FragmentedBytes = value.FragmentedBytes,
        CommittedBytes = value.CommittedBytes,
        PromotedBytes = value.PromotedBytes,
        PinnedObjectsCount = value.PinnedObjectsCount,
        FinalizationPendingCount = value.FinalizationPendingCount,
        PauseDuration = value.PauseDuration,
        PauseTimePercentage = value.PauseTimePercentage,
        HighMemoryLoadThresholdBytes = value.HighMemoryLoadThresholdBytes,
        MemoryLoadBytes = value.MemoryLoadBytes,
        TotalAvailableMemoryBytes = value.TotalAvailableMemoryBytes,
        GenerationDetails = value.GenerationDetails.Select(static generation => new GcGenerationDetails
        {
            Generation = generation.Generation,
            SizeBeforeBytes = generation.SizeBeforeBytes,
            SizeAfterBytes = generation.SizeAfterBytes,
            FragmentationBeforeBytes = generation.FragmentationBeforeBytes,
            FragmentationAfterBytes = generation.FragmentationAfterBytes
        }).ToArray()
    };
}
