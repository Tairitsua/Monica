using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.InProcessCollector.Models;
using Monica.OpenTelemetry.UI.UIOpenTelemetry.Support;

namespace Monica.OpenTelemetry.UI.UIOpenTelemetry.State;

/// <summary>Owns one detached metrics view, exact selections and page polling, without owning the host collector.</summary>
/// <remarks>
/// Reads admit one operation without queuing. Successful empty results remain valid evidence; failures retain the
/// previous view and both capture times. Disposal drains admitted work and polling transitions; late calls are inert.
/// </remarks>
public sealed class OpenTelemetryDashboardPageState : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly Func<Task<Result<OpenTelemetrySnapshot>>> _getSnapshot;
    private readonly string _loadFailedMessage;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _pollingCancellation;
    private Task _pollingLoop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;
    private bool _initialSelectionMade;
    private string _meterFilter = string.Empty;
    private string _nameFilter = string.Empty;

    /// <summary>Creates a component-owned session borrowing the scoped production facade.</summary>
    /// <param name="facade">The current host's snapshot facade; this session never disposes it.</param>
    /// <param name="snapshotLoadFailedMessage">Localized failure text for a successful envelope without data.</param>
    public OpenTelemetryDashboardPageState(OpenTelemetryFacade facade, string snapshotLoadFailedMessage)
        : this(facade, snapshotLoadFailedMessage, TimeProvider.System, facade.GetSnapshotAsync) { }

    // This boundary can hold or fail a genuine facade query in structural tests without replacing host composition.
    internal OpenTelemetryDashboardPageState(OpenTelemetryFacade facade, string snapshotLoadFailedMessage,
        TimeProvider clock, Func<Task<Result<OpenTelemetrySnapshot>>> getSnapshot)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        _loadFailedMessage = snapshotLoadFailedMessage;
        _clock = clock;
        _getSnapshot = getSnapshot;
    }

    /// <summary>Notifies the owner synchronously; the component observes and drains its rendering tasks.</summary>
    /// <remarks>Handlers must not block on async retirement. A handler failure does not become a facade failure.</remarks>
    public event Action? StateChanged;
    /// <summary>Gets the immutable detached graph from the last successful read.</summary>
    public OpenTelemetrySnapshot? Snapshot { get; private set; }
    /// <summary>Gets whether a snapshot read succeeded, including an empty registry.</summary>
    public bool HasSnapshot { get; private set; }
    /// <summary>Gets the UTC successful view-adoption time, independent of source/sample times.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; private set; }
    /// <summary>Gets whether an admitted query is in progress.</summary>
    public bool IsLoading { get; private set; }
    /// <summary>Gets the actual read failure; an empty message remains a failure. Null means no outstanding failure.</summary>
    public string? Error { get; private set; }
    /// <summary>Gets the exact selected instrument from the adopted detached graph.</summary>
    public InstrumentSnapshot? SelectedInstrument { get; private set; }
    /// <summary>Gets the exact selected tag series within the selected instrument.</summary>
    public TagSetSamples? SelectedSeries { get; private set; }
    /// <summary>Gets the last isolated notification failure for structural diagnostics.</summary>
    internal Exception? NotificationError { get; private set; }
    /// <summary>Gets or sets an exact ordinal meter-equality filter. An empty value includes every meter.</summary>
    /// <remarks>The historical property name does not imply prefix matching.</remarks>
    public string MeterPrefixFilter
    {
        get { lock (_sync) return _meterFilter; }
        set { lock (_sync) { if (_disposed || _meterFilter == value) return; _meterFilter = value ?? string.Empty; ReconcileFilter(); } Notify(); }
    }
    /// <summary>Gets or sets an ordinal-ignore-case instrument-name substring filter.</summary>
    public string InstrumentNameFilter
    {
        get { lock (_sync) return _nameFilter; }
        set { lock (_sync) { if (_disposed || _nameFilter == value) return; _nameFilter = value ?? string.Empty; ReconcileFilter(); } Notify(); }
    }
    /// <summary>Gets the effective page interval; null means manual reads. The initial default is five seconds.</summary>
    public TimeSpan? AutoRefreshInterval { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>Reads the initial snapshot and starts the configured page polling loop.</summary>
    public async Task<Result> LoadAsync() { var result = await RefreshAsync(); await UpdatePollingAsync(null, false); return result; }
    /// <summary>Reads retained evidence without forcing observable sampling. Busy or retired calls return empty Conflict.</summary>
    public Task<Result> RefreshAsync() => RefreshCoreAsync();
    /// <summary>Serializes polling stop/drain/start. Non-positive or null intervals mean manual reads.</summary>
    /// <remarks>Positive intervals must fit PeriodicTimer's inclusive 1–4294967294ms range. Host collection continues.</remarks>
    public Task SetAutoRefreshIntervalAsync(TimeSpan? interval)
    {
        lock (_sync) { if (_disposed) return Task.CompletedTask; }
        if (interval > TimeSpan.Zero && (interval.Value.TotalMilliseconds < 1 || interval.Value.TotalMilliseconds > uint.MaxValue - 1d))
            throw new ArgumentOutOfRangeException(nameof(interval), "A positive polling interval must fit the supported timer range.");
        return UpdatePollingAsync(interval > TimeSpan.Zero ? interval : null, true);
    }
    /// <summary>Gets whole-view meter names, independent of name filtering.</summary>
    public IReadOnlyList<string> GetMeterNames()
    {
        lock (_sync) return Snapshot?.Instruments.Select(instrument => instrument.MeterName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
    }
    /// <summary>Gets all matching actual instruments without truncation.</summary>
    public IReadOnlyList<InstrumentSnapshot> FilteredInstruments() { lock (_sync) return FilteredCore(); }
    /// <summary>Groups all matching instruments by their exact meter name.</summary>
    public IReadOnlyList<IGrouping<string, InstrumentSnapshot>> FilteredInstrumentGroups() => FilteredInstruments().GroupBy(instrument => instrument.MeterName).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
    /// <summary>Selects only an exact object from this view's matching registry; null deliberately clears selection.</summary>
    public void SelectInstrument(InstrumentSnapshot? instrument)
    {
        lock (_sync)
        {
            if (_disposed || instrument is not null && !FilteredCore().Any(candidate => ReferenceEquals(candidate, instrument))) return;
            if (ReferenceEquals(SelectedInstrument, instrument)) { if (instrument is null) _initialSelectionMade = true; return; }
            _initialSelectionMade = true; SelectedInstrument = instrument; SelectedSeries = instrument?.TagSets.FirstOrDefault();
        }
        Notify();
    }
    /// <summary>Selects only an exact series within the current instrument; null deliberately clears selection.</summary>
    public void SelectSeries(TagSetSamples? series)
    {
        lock (_sync)
        {
            if (_disposed || series is not null && SelectedInstrument?.TagSets.Any(candidate => ReferenceEquals(candidate, series)) != true) return;
            if (ReferenceEquals(SelectedSeries, series)) return;
            SelectedSeries = series;
        }
        Notify();
    }
    /// <summary>Freezes an exact current selected row; stale series references or invalid indices return null.</summary>
    public OpenTelemetrySampleEvidence? InspectSample(TagSetSamples series, int sourceIndex)
    {
        lock (_sync)
        {
            return !_disposed && ReferenceEquals(SelectedSeries, series) && SelectedInstrument is { } instrument && Snapshot is { } snapshot &&
                SnapshotCapturedAt is { } capturedAt && sourceIndex >= 0 && sourceIndex < series.Samples.Count
                ? new(instrument, series, series.Samples[sourceIndex], sourceIndex, snapshot.TimestampUtc, capturedAt) : null;
        }
    }
    /// <summary>Exports the whole adopted view as current-state JSON, omitting retained sample history.</summary>
    public string ExportJson() { lock (_sync) return Snapshot is null ? string.Empty : OpenTelemetryJsonSummaryExporter.Export(Snapshot); }
    /// <summary>Exports whole-view diagnostic Prometheus gauges; this is not the SDK scrape exporter.</summary>
    public string ExportPrometheusText() { lock (_sync) return Snapshot is null ? string.Empty : PrometheusTextExporter.Export(Snapshot); }
    /// <summary>Retires and drains this session exactly once without stopping or disposing its borrowed collector.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null) { _disposed = true; IsLoading = false; AutoRefreshInterval = null; StateChanged = null; _disposeTask = DisposeCoreAsync(); }
            return new(_disposeTask);
        }
    }

    private async Task<Result> RefreshCoreAsync(CancellationToken pollingCancellation = default)
    {
        lock (_sync) { if (_disposed || pollingCancellation.IsCancellationRequested || !_refreshGate.Wait(0)) return Inactive(); IsLoading = true; }
        try
        {
            Notify();
            lock (_sync) { if (_disposed || pollingCancellation.IsCancellationRequested) return Inactive(); }
            var result = await _getSnapshot();
            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure()) return RetainFailure(error, pollingCancellation);
            if (snapshot is null) return RetainFailure(Result.Failure(_loadFailedMessage, ResultStatus.InternalError), pollingCancellation);
            var detached = OpenTelemetrySnapshotOwnership.Detach(snapshot);
            var capturedAt = _clock.GetUtcNow();
            lock (_sync)
            {
                if (_disposed || pollingCancellation.IsCancellationRequested) return Inactive();
                var previousInstrument = SelectedInstrument; var previousSeries = SelectedSeries;
                var candidates = detached.Instruments.Where(MatchesFilter).ToArray();
                InstrumentSnapshot? selectedInstrument = null;
                TagSetSamples? selectedSeries = null;
                var initialSelectionMade = _initialSelectionMade;
                if (previousInstrument is not null)
                {
                    selectedInstrument = OpenTelemetrySelectionIdentity.ReconcileInstrument(previousInstrument, candidates);
                    selectedSeries = selectedInstrument is not null && previousSeries is not null ? OpenTelemetrySelectionIdentity.ReconcileSeries(previousSeries, selectedInstrument.TagSets) : null;
                }
                else if (!initialSelectionMade && candidates.Length > 0)
                {
                    initialSelectionMade = true; selectedInstrument = candidates[0]; selectedSeries = selectedInstrument.TagSets.FirstOrDefault();
                }
                Snapshot = detached; HasSnapshot = true; SnapshotCapturedAt = capturedAt; Error = null;
                SelectedInstrument = selectedInstrument; SelectedSeries = selectedSeries; _initialSelectionMade = initialSelectionMade;
                return Result.Success();
            }
        }
        catch (Exception exception) { return RetainFailure(Result.Failure(exception.Message, ResultStatus.InternalError), pollingCancellation); }
        finally { lock (_sync) { if (!_disposed) IsLoading = false; } _refreshGate.Release(); Notify(); }
    }
    private Result RetainFailure(Result error, CancellationToken cancellation)
    {
        lock (_sync) { if (_disposed || cancellation.IsCancellationRequested) return Inactive(); Error = error.Message ?? string.Empty; return error; }
    }
    private IReadOnlyList<InstrumentSnapshot> FilteredCore() => Snapshot?.Instruments.Where(MatchesFilter).ToArray() ?? [];
    private bool MatchesFilter(InstrumentSnapshot instrument) => (string.IsNullOrWhiteSpace(_meterFilter) || instrument.MeterName.Equals(_meterFilter, StringComparison.Ordinal)) &&
        (string.IsNullOrWhiteSpace(_nameFilter) || instrument.Name.Contains(_nameFilter, StringComparison.OrdinalIgnoreCase));
    private void ReconcileFilter() { if (SelectedInstrument is not null && !MatchesFilter(SelectedInstrument)) { SelectedInstrument = null; SelectedSeries = null; } }
    private async Task UpdatePollingAsync(TimeSpan? interval, bool requested)
    {
        await _transitionGate.WaitAsync();
        try
        {
            lock (_sync) { if (_disposed) return; if (requested) AutoRefreshInterval = interval; }
            await StopPollingAsync();
            lock (_sync)
            {
                if (!_disposed && AutoRefreshInterval is { } selected)
                {
                    _pollingCancellation = new(); _pollingLoop = RunPollingAsync(selected, _pollingCancellation.Token);
                }
            }
        }
        finally { _transitionGate.Release(); Notify(); }
    }
    private async Task RunPollingAsync(TimeSpan interval, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(interval, _clock);
        try { while (await timer.WaitForNextTickAsync(cancellation)) await RefreshCoreAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    // The serialized transition owner stops the loop; the loop never acquires the transition gate.
    private async Task StopPollingAsync()
    {
        var cancellation = _pollingCancellation; var loop = _pollingLoop; _pollingCancellation = null;
        if (cancellation is not null) await cancellation.CancelAsync();
        try { await loop; } finally { cancellation?.Dispose(); _pollingLoop = Task.CompletedTask; }
    }
    private async Task DisposeCoreAsync()
    {
        await _transitionGate.WaitAsync();
        try { await StopPollingAsync(); } finally { _transitionGate.Release(); }
        await _refreshGate.WaitAsync(); _refreshGate.Release();
    }
    private void Notify()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var handlers = StateChanged?.GetInvocationList().Cast<Action>().ToArray() ?? [];
            foreach (var handler in handlers)
            {
                if (_disposed) return;
                try { handler(); } catch (Exception exception) { NotificationError = exception; }
            }
        }
    }
    private static Result Inactive() => Result.Failure(string.Empty, ResultStatus.Conflict);
}
