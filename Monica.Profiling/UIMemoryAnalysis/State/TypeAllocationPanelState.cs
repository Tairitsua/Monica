using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.Profiling.TypeAllocation.Facades;
using Monica.Profiling.TypeAllocation.Models;

namespace Monica.Profiling.UIMemoryAnalysis.State;

/// <summary>Identifies a native ordering of the selected source's complete retained type registry.</summary>
public enum TypeAllocationSortOrder
{
    /// <summary>Largest attributed or enumerated byte value first.</summary>
    BytesDescending,
    /// <summary>Largest observed tick-event or enumerated object count first.</summary>
    CountDescending,
    /// <summary>Full type identity in ordinal, case-insensitive order.</summary>
    NameAscending
}

/// <summary>Identifies one explicit action against the host-owned allocation facilities.</summary>
public enum TypeAllocationOperation
{
    /// <summary>Start the shared live collector.</summary>
    Start,
    /// <summary>Stop the shared live collector.</summary>
    Stop,
    /// <summary>Reset live collector evidence, preserving a separately retained heap census.</summary>
    Reset,
    /// <summary>Capture a process-snapshot heap census.</summary>
    HeapCapture
}

/// <summary>Retains an action outcome independently of a subsequent live view read.</summary>
/// <param name="Operation">The explicit shared-provider action that was admitted.</param>
/// <param name="StartedAt">UTC time at admission, independent of evidence timestamps.</param>
/// <param name="CompletedAt">UTC time when the actual action envelope was adopted.</param>
/// <param name="Status">The action result status; a later live-read failure does not replace it.</param>
/// <param name="Message">The encoded, actual action message, if the facade supplied one.</param>
public sealed record TypeAllocationActionResult(
    TypeAllocationOperation Operation,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ResultStatus Status,
    string? Message)
{
    /// <summary>Gets whether the action succeeded, irrespective of a follow-up read failure.</summary>
    public bool IsSuccessful => Status is ResultStatus.Ok or ResultStatus.Created;
}

/// <summary>Freezes one type and the complete source totals from the view that opened its inspector.</summary>
/// <param name="TypeName">Complete type identity, without preview shortening or markup interpretation.</param>
/// <param name="Count">Observed allocation-tick events for ETW, or enumerated non-free objects for a heap census.</param>
/// <param name="Bytes">Attributed ETW allocation volume or enumerated heap-object bytes, unscaled.</param>
/// <param name="CountShare">The provider-recorded percentage of the complete source count, not the visible page.</param>
/// <param name="ByteShare">The provider-recorded percentage of complete source bytes, not a reachability claim.</param>
/// <param name="LastSeenUtc">The provider's recorded type timestamp, preserved without inferring object lifetime.</param>
/// <param name="Source">The recorded measurement basis; live traffic and heap census remain distinct.</param>
/// <param name="SnapshotTimestamp">Evidence time: ETW read/stop time or heap scan completion, not exact native clone creation.</param>
/// <param name="CapturedAt">Successful workspace adoption time frozen with this inspector.</param>
/// <param name="SourceCount">Complete source count, including ETW totals whose ranking rows may have been removed.</param>
/// <param name="SourceBytes">Complete unscaled source byte total used for the recorded shares.</param>
/// <param name="DroppedEventCount">Skipped unknown-type or invalid-payload current-process ticks, not ETW transport loss.</param>
/// <param name="Duration">Current/last ETW session window or heap scan duration; ETW totals can span earlier sessions.</param>
public sealed record TypeAllocationEvidence(
    string TypeName,
    long Count,
    long Bytes,
    double CountShare,
    double ByteShare,
    DateTime LastSeenUtc,
    TypeAllocationDataSource Source,
    DateTime SnapshotTimestamp,
    DateTimeOffset CapturedAt,
    long SourceCount,
    long SourceBytes,
    long DroppedEventCount,
    TimeSpan Duration);

/// <summary>Provides one detached, consistently filtered and paged source view for rendering.</summary>
/// <remarks>Type rows are detached from both the host provider and the state's retained snapshots.</remarks>
/// <param name="Source">The selected workspace lens, which may have no successful snapshot yet.</param>
/// <param name="Snapshot">A deep copy of the selected retained source, or null when no result has been adopted.</param>
/// <param name="CapturedAt">Successful adoption time associated with that source, retained across read failures.</param>
/// <param name="MatchingTypes">All detached rows matching full-name search, in the selected deterministic order.</param>
/// <param name="VisibleTypes">The native page's rows, referencing this view's detached snapshot for exact inspection.</param>
/// <param name="Page">The reconciled one-based page; empty registries use page one.</param>
/// <param name="PageSize">The bounded rows-per-page limit, independent of provider retention.</param>
public sealed record TypeAllocationView(
    TypeAllocationDataSource Source,
    TypeAllocationSnapshot? Snapshot,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<TypeAllocationInfo> MatchingTypes,
    IReadOnlyList<TypeAllocationInfo> VisibleTypes,
    int Page,
    int PageSize)
{
    /// <summary>Gets the total number of matching rows, before pagination.</summary>
    public int Count => MatchingTypes.Count;
    /// <summary>Gets the number of native pages needed for all matching rows.</summary>
    public int PageCount => Math.Max(1, (Count + PageSize - 1) / PageSize);
    /// <summary>Gets the first displayed one-based row number, or zero for no matches.</summary>
    public int FirstVisible => Count == 0 ? 0 : (Page - 1) * PageSize + 1;
    /// <summary>Gets the last displayed one-based row number, or zero for no matches.</summary>
    public int LastVisible => Math.Min(Page * PageSize, Count);

    /// <summary>Copies exact scalar evidence from a row belonging to this detached source view.</summary>
    public TypeAllocationEvidence? Inspect(TypeAllocationInfo row) =>
        Snapshot is { } snapshot && CapturedAt is { } capturedAt && snapshot.Types.Contains(row)
            ? new TypeAllocationEvidence(row.TypeName, row.AllocationCount, row.TotalBytes,
                row.AllocationPercentage, row.BytesPercentage, row.LastSeenUtc, snapshot.DataSource,
                snapshot.Timestamp, capturedAt, snapshot.TotalAllocationCount, snapshot.TotalBytes,
                snapshot.DroppedEventCount, snapshot.Duration)
            : null;
}

/// <summary>Owns one allocation workspace's subscription, detached source views and explicit operation admission.</summary>
/// <remarks>
/// Live traffic and heap census are retained separately. Updates never switch away from a selected heap view.
/// One read or action is admitted at a time; retirement unsubscribes and drains admitted work without stopping
/// or disposing the borrowed host-owned providers. AllocationTick counts are events, not object instances.
/// </remarks>
/// <param name="typeAllocationFacade">The scoped production allocation facade.</param>
/// <param name="timeProvider">The successful view/action clock, or the system clock when none is supplied.</param>
/// <param name="logger">Optional cleanup diagnostics; a failed retirement still faults the shared asynchronous drain.</param>
public sealed class TypeAllocationPanelState(
    TypeAllocationFacade typeAllocationFacade,
    TimeProvider? timeProvider = null,
    ILogger<TypeAllocationPanelState>? logger = null) : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Result<TypeAllocationSnapshot>> _getLive = typeAllocationFacade.GetCurrentSnapshot;
    private readonly Func<Task<Result<TypeAllocationSnapshot>>> _captureHeap = typeAllocationFacade.TakeHeapSnapshotAsync;
    private readonly Func<Action<TypeAllocationSnapshot>, Action> _subscribe = typeAllocationFacade.SubscribeToUpdates;
    private Action? _unsubscribe;
    private Task? _disposeTask;
    private bool _disposed;
    private TypeAllocationDataSource _selectedSource = TypeAllocationDataSource.AllocationTracking;
    private AllocationSamplingMode _selectedMode = AllocationSamplingMode.Low;
    private TypeAllocationSortOrder _sortOrder;
    private string _searchQuery = string.Empty;
    private int _page = 1;
    private int _topN = 30;
    private bool _showFullTypeName;
    private DateTimeOffset _actionStartedAt;

    /// <summary>Preserves the original constructor with the system clock.</summary>
    public TypeAllocationPanelState(TypeAllocationFacade typeAllocationFacade) : this(typeAllocationFacade, null) { }

    // Test boundaries can hold genuine reads/actions and capture subscription ownership without replacing providers.
    internal TypeAllocationPanelState(
        TypeAllocationFacade typeAllocationFacade,
        TimeProvider? timeProvider,
        Func<Result<TypeAllocationSnapshot>> getLive,
        Func<Task<Result<TypeAllocationSnapshot>>> captureHeap,
        Func<Action<TypeAllocationSnapshot>, Action> subscribe)
        : this(typeAllocationFacade, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(getLive);
        ArgumentNullException.ThrowIfNull(captureHeap);
        ArgumentNullException.ThrowIfNull(subscribe);
        _getLive = getLive;
        _captureHeap = captureHeap;
        _subscribe = subscribe;
    }

    /// <summary>Notifies subscribers only while the workspace is live.</summary>
    public event Action? StateChanged;
    /// <summary>Gets the detached latest live provider evidence. Consumers should treat its rows as read-only.</summary>
    public TypeAllocationSnapshot? LiveSnapshot { get; private set; }
    /// <summary>Gets the separately retained detached heap census, unchanged by live updates or live resets.</summary>
    public TypeAllocationSnapshot? HeapSnapshot { get; private set; }
    /// <summary>Gets the last successful live view read/update time.</summary>
    public DateTimeOffset? LiveCapturedAt { get; private set; }
    /// <summary>Gets the successful heap result adoption time.</summary>
    public DateTimeOffset? HeapCapturedAt { get; private set; }
    /// <summary>Gets the selected detached source, preserving the original read-only snapshot API.</summary>
    public TypeAllocationSnapshot? Snapshot { get { lock (_sync) return SelectedSnapshot(); } }
    /// <summary>Gets the successful adoption time associated with the selected source.</summary>
    public DateTimeOffset? CapturedAt { get { lock (_sync) return SelectedCapture(); } }
    /// <summary>Gets whether a read or action is admitted.</summary>
    public bool IsLoading { get; private set; }
    /// <summary>Gets whether an admitted heap capture is incomplete.</summary>
    public bool IsSnapshotting { get; private set; }
    /// <summary>Gets the collector state observed by the latest live read/update.</summary>
    public bool IsCollecting { get; private set; }
    /// <summary>Gets the provider mode observed by the latest live read/update.</summary>
    public AllocationSamplingMode CurrentMode { get; private set; }
    /// <summary>Gets the most recent actual live-read failure. Empty still means failure; success clears it.</summary>
    public string? LoadError { get; private set; }
    /// <summary>Gets the most recent explicit action outcome, separate from follow-up read errors.</summary>
    public TypeAllocationActionResult? LastAction { get; private set; }
    /// <summary>Gets whether the host platform supports ETW collection.</summary>
    public bool CanStartCollection => OperatingSystem.IsWindows();
    /// <summary>Gets whether the host platform supports the configured process-snapshot heap provider.</summary>
    public bool CanTakeHeapSnapshot => OperatingSystem.IsWindowsVersionAtLeast(6, 3);

    /// <summary>Gets or sets the displayed source. Selecting an empty source never triggers a capture.</summary>
    public TypeAllocationDataSource SelectedSource
    {
        get { lock (_sync) return _selectedSource; }
        set
        {
            lock (_sync)
            {
                if (_disposed || _selectedSource == value || value is not (TypeAllocationDataSource.AllocationTracking or TypeAllocationDataSource.HeapSnapshot)) return;
                _selectedSource = value;
                _page = 1;
            }
            NotifyStateChanged();
        }
    }

    /// <summary>Gets or sets the compatibility mode. Low and High use the provider's same AllocationTick strategy.</summary>
    public AllocationSamplingMode SelectedMode
    {
        get { lock (_sync) return _selectedMode; }
        set { lock (_sync) { if (_disposed) return; _selectedMode = value; } NotifyStateChanged(); }
    }

    /// <summary>Gets or sets the rows per native page, retaining the original TopN API while exposing every retained row.</summary>
    public int TopN
    {
        get { lock (_sync) return _topN; }
        set { lock (_sync) { if (_disposed) return; _topN = Math.Clamp(value, 5, 200); _page = 1; } NotifyStateChanged(); }
    }

    /// <summary>Gets or sets whether rows preview full names. Search and inspector always use complete identity.</summary>
    public bool ShowFullTypeName
    {
        get { lock (_sync) return _showFullTypeName; }
        set { lock (_sync) { if (_disposed) return; _showFullTypeName = value; } NotifyStateChanged(); }
    }

    /// <summary>Gets or sets an ordinal case-insensitive query against complete type identity.</summary>
    public string SearchQuery
    {
        get { lock (_sync) return _searchQuery; }
        set { lock (_sync) { if (_disposed) return; _searchQuery = value ?? string.Empty; _page = 1; } NotifyStateChanged(); }
    }

    /// <summary>Gets or sets the registry ordering, with full identity as a deterministic tiebreaker.</summary>
    public TypeAllocationSortOrder SortOrder
    {
        get { lock (_sync) return _sortOrder; }
        set { lock (_sync) { if (_disposed) return; _sortOrder = value; _page = 1; } NotifyStateChanged(); }
    }

    /// <summary>Subscribes once and reads actual provider state without automatically starting collection.</summary>
    public Task<Result> InitializeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return Task.FromResult(InactiveResult());
            _unsubscribe ??= _subscribe(OnSnapshotUpdated);
        }
        return Task.FromResult(RefreshData());
    }

    /// <summary>Reads live evidence without replacing a selected heap census. Busy/retired calls return empty Conflict.</summary>
    public Result RefreshData()
    {
        if (!TryAdmit()) return InactiveResult();
        try { NotifyStateChanged(); return IsRetired() ? InactiveResult() : ReadLive(); }
        finally { FinishOperation(); }
    }

    /// <summary>Starts the shared live provider in a supported compatibility mode, then reads its actual state.</summary>
    public Result StartCollection() => RunAction(TypeAllocationOperation.Start, () =>
        !CanStartCollection ? Result.Failure(string.Empty, ResultStatus.ServiceUnavailable)
        : SelectedMode is not (AllocationSamplingMode.Low or AllocationSamplingMode.High)
            ? Result.Failure(string.Empty, ResultStatus.BadRequest)
            : typeAllocationFacade.StartCollection(SelectedMode));

    /// <summary>Stops the shared live provider explicitly. Workspace retirement never calls this action.</summary>
    public Result StopCollection() => RunAction(TypeAllocationOperation.Stop, typeAllocationFacade.StopCollection);

    /// <summary>Resets only inactive live evidence. A separately retained heap census and current filters survive.</summary>
    public Result ResetData() => RunAction(TypeAllocationOperation.Reset, () =>
        typeAllocationFacade.IsCollecting ? Result.Failure(string.Empty, ResultStatus.Forbidden) : typeAllocationFacade.ResetData());

    /// <summary>
    /// Captures a supported heap census and selects it on success. Live events continue into their separate cache.
    /// The provider has no cancellation token; retirement drains it and prevents late result publication.
    /// </summary>
    public async Task<Result<TypeAllocationSnapshot>> TakeHeapSnapshotAsync()
    {
        if (!TryAdmit(TypeAllocationOperation.HeapCapture)) return Result.FromFailure<TypeAllocationSnapshot>(InactiveResult());
        try
        {
            NotifyStateChanged();
            if (IsRetired()) return Result.FromFailure<TypeAllocationSnapshot>(InactiveResult());
            Result<TypeAllocationSnapshot> result = CanTakeHeapSnapshot
                ? await _captureHeap().ConfigureAwait(false)
                : Result.Failure<TypeAllocationSnapshot>(string.Empty, ResultStatus.ServiceUnavailable);
            lock (_sync)
            {
                if (_disposed) return Result.FromFailure<TypeAllocationSnapshot>(InactiveResult());
                LastAction = new(TypeAllocationOperation.HeapCapture, _actionStartedAt, _timeProvider.GetUtcNow(), result.Status, result.Message);
                if (result.IsSuccess())
                {
                    HeapSnapshot = Detach(result.Data ?? throw new InvalidOperationException("The heap capture returned no data."));
                    HeapCapturedAt = _timeProvider.GetUtcNow();
                    _selectedSource = TypeAllocationDataSource.HeapSnapshot;
                    _page = 1;
                }
            }
            return result;
        }
        finally { FinishOperation(); }
    }

    /// <summary>Returns one detached consistent source/filter/page view, including all matching rows.</summary>
    public TypeAllocationView GetView()
    {
        lock (_sync)
        {
            var snapshot = SelectedSnapshot() is { } selected ? Detach(selected) : null;
            var matches = Filter(snapshot).ToArray();
            var pageCount = Math.Max(1, (matches.Length + _topN - 1) / _topN);
            var page = Math.Clamp(_page, 1, pageCount);
            return new(_selectedSource, snapshot, SelectedCapture(), matches,
                matches.Skip((page - 1) * _topN).Take(_topN).ToArray(), page, _topN);
        }
    }

    /// <summary>Selects a valid native registry page without changing filters or source evidence.</summary>
    public void SetPage(int page)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var count = Filter(SelectedSnapshot()).Count();
            _page = Math.Clamp(page, 1, Math.Max(1, (count + _topN - 1) / _topN));
        }
        NotifyStateChanged();
    }

    /// <summary>Retires immediately for legacy callers. Use DisposeAsync to await admitted work and cleanup failures.</summary>
    public void Dispose()
    {
        var retirement = DisposeAsync();
        if (retirement.IsCompleted) retirement.GetAwaiter().GetResult();
        else
        {
            // Legacy disposal cannot await admitted provider work. Log in the drain and observe its fault here;
            // callers of DisposeAsync still receive the same failure rather than a swallowed action outcome.
            _ = retirement.AsTask().ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Unsubscribes once and shares the drain of admitted work without disposing or stopping providers.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                IsLoading = false;
                IsSnapshotting = false;
                var unsubscribe = _unsubscribe;
                _unsubscribe = null;
                _disposeTask = DisposeCoreAsync(unsubscribe);
            }
            return new(_disposeTask);
        }
    }

    private bool TryAdmit(TypeAllocationOperation? action = null)
    {
        lock (_sync)
        {
            if (_disposed || !_operationGate.Wait(0)) return false;
            IsLoading = true;
            IsSnapshotting = action == TypeAllocationOperation.HeapCapture;
            if (action.HasValue) _actionStartedAt = _timeProvider.GetUtcNow();
            return true;
        }
    }

    private Result RunAction(TypeAllocationOperation operation, Func<Result> action)
    {
        if (!TryAdmit(operation)) return InactiveResult();
        try
        {
            NotifyStateChanged();
            if (IsRetired()) return InactiveResult();
            var result = action();
            lock (_sync)
            {
                if (_disposed) return InactiveResult();
                LastAction = new(operation, _actionStartedAt, _timeProvider.GetUtcNow(), result.Status, result.Message);
            }
            ReadLive();
            return IsRetired() ? InactiveResult() : result;
        }
        finally { FinishOperation(); }
    }

    private Result ReadLive()
    {
        if (IsRetired()) return InactiveResult();
        var result = _getLive();
        lock (_sync)
        {
            if (_disposed) return InactiveResult();
            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure())
            {
                LoadError = error.Message ?? string.Empty;
                return error;
            }
            if (snapshot is null)
            {
                LoadError = "The type allocation snapshot was unavailable.";
                return Result.Failure(LoadError);
            }
            PublishLive(snapshot);
            LoadError = null;
            return Result.Success();
        }
    }

    private void OnSnapshotUpdated(TypeAllocationSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_disposed) return;
            PublishLive(snapshot);
        }
        NotifyStateChanged();
    }

    private void PublishLive(TypeAllocationSnapshot snapshot)
    {
        LiveSnapshot = Detach(snapshot);
        LiveCapturedAt = _timeProvider.GetUtcNow();
        IsCollecting = snapshot.IsCollecting;
        CurrentMode = snapshot.SamplingMode;
        if (_selectedSource == TypeAllocationDataSource.AllocationTracking) ReconcilePage();
    }

    private IEnumerable<TypeAllocationInfo> Filter(TypeAllocationSnapshot? snapshot)
    {
        var query = _searchQuery.Trim();
        var types = (snapshot?.Types ?? []).Where(row => query.Length == 0 || row.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase));
        return _sortOrder switch
        {
            TypeAllocationSortOrder.CountDescending => types.OrderByDescending(row => row.AllocationCount).ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.TypeName, StringComparer.Ordinal),
            TypeAllocationSortOrder.NameAscending => types.OrderBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.TypeName, StringComparer.Ordinal),
            _ => types.OrderByDescending(row => row.TotalBytes).ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.TypeName, StringComparer.Ordinal)
        };
    }

    private void ReconcilePage()
    {
        var count = Filter(SelectedSnapshot()).Count();
        _page = Math.Clamp(_page, 1, Math.Max(1, (count + _topN - 1) / _topN));
    }

    private TypeAllocationSnapshot? SelectedSnapshot() =>
        _selectedSource == TypeAllocationDataSource.HeapSnapshot ? HeapSnapshot : LiveSnapshot;
    private DateTimeOffset? SelectedCapture() =>
        _selectedSource == TypeAllocationDataSource.HeapSnapshot ? HeapCapturedAt : LiveCapturedAt;
    private bool IsRetired() { lock (_sync) return _disposed; }

    private void FinishOperation()
    {
        lock (_sync) { if (!_disposed) { IsLoading = false; IsSnapshotting = false; } }
        _operationGate.Release();
        NotifyStateChanged();
    }

    private async Task DisposeCoreAsync(Action? unsubscribe)
    {
        try
        {
            try { unsubscribe?.Invoke(); }
            finally
            {
                await _operationGate.WaitAsync().ConfigureAwait(false);
                _operationGate.Release();
                lock (_sync) StateChanged = null;
            }
        }
        catch (Exception exception)
        {
            if (logger is not null) logger.LogError(exception, "Allocation workspace retirement failed.");
            else System.Diagnostics.Trace.TraceError("Allocation workspace retirement failed: {0}", exception);
            throw;
        }
    }

    private void NotifyStateChanged()
    {
        lock (_sync) { if (!_disposed) StateChanged?.Invoke(); }
    }

    private static Result InactiveResult() => Result.Failure(string.Empty, ResultStatus.Conflict);

    private static TypeAllocationSnapshot Detach(TypeAllocationSnapshot snapshot) => new()
    {
        Timestamp = snapshot.Timestamp,
        Duration = snapshot.Duration,
        Types = snapshot.Types.Select(row => new TypeAllocationInfo
        {
            TypeName = row.TypeName,
            AllocationCount = row.AllocationCount,
            TotalBytes = row.TotalBytes,
            AllocationPercentage = row.AllocationPercentage,
            BytesPercentage = row.BytesPercentage,
            LastSeenUtc = row.LastSeenUtc
        }).ToArray(),
        TotalAllocationCount = snapshot.TotalAllocationCount,
        TotalBytes = snapshot.TotalBytes,
        IsCollecting = snapshot.IsCollecting,
        SamplingMode = snapshot.SamplingMode,
        DroppedEventCount = snapshot.DroppedEventCount,
        DataSource = snapshot.DataSource,
        ErrorMessage = snapshot.ErrorMessage
    };
}
