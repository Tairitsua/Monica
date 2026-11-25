using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.UILogging.Components;
using Monica.Framework.UI.UILogging.State;
using Monica.Framework.UI.UILogging.Support;

namespace Monica.Framework.UI.Pages;

public partial class UILoggingMonitorPage
{
    public const string PAGE_URL = "/logging-monitor";

    [SupplyParameterFromQuery(Name = "file")]
    public string? File { get; set; }

    [SupplyParameterFromQuery(Name = "q")] public string? Keyword { get; set; }
    [SupplyParameterFromQuery(Name = "severity")] public string? SeverityQuery { get; set; }
    [SupplyParameterFromQuery(Name = "only")] public string? Only { get; set; }
    [SupplyParameterFromQuery(Name = "span")] public string? Span { get; set; }
    [SupplyParameterFromQuery(Name = "line")] public string? Line { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? Context { get; set; }
    [SupplyParameterFromQuery(Name = "generation")] public string? Generation { get; set; }
    private string? _lastRoute;
    private int? _fileCount;
    private LogLineEvidence? _initialEvidence;
    private LogLineRequest? _contextRequest;
    private (long From, long To)? _range;
    private IReadOnlyList<LogLineViewModel> _spanLines = [];
    private (ScreenLogSnapshot? Snapshot, (long, long)? Range) _projection;
    private long? _jumpAfterRender;
    private readonly object _ownership = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _retirement = new();
    private readonly HashSet<Task> _operations = [];
    private LogReadingSession? _session;
    private LoggingSubscription? _subscription;
    private Task? _listener;
    private LogBrowserTransfer? _transfer;
    private Task? _disposeTask;
    private ScreenLogSnapshot? _snapshot;
    private ContextLogResult? _context;
    private LogViewer? _viewer;
    private LogViewerPosition? _position;
    private LogViewerPosition? _livePosition;
    private LogViewerPosition? _pendingPosition;
    private bool _returnFocus;
    private string? _loadedFile;
    private string? _keyword;
    private string? _error;
    private LogSeverity? _severity;
    private int _lineLimit;
    private long _contextVersion;
    private bool _onlyMatches;
    private bool _autoScroll = true;
    private bool _initializing = true;
    private bool _parametersLoaded;
    private bool _busy;
    private volatile bool _disposed;

    private bool IsRunning => _snapshot?.IsRunning == true;
    private LogSourceIdentity? Source => _initializing ? null : _snapshot?.Source;
    private string ReaderUrl => NavigationManager.ToAbsoluteUri(PAGE_URL.TrimStart('/') + Query).ToString();
    private string FilesUrl => NavigationManager.ToAbsoluteUri(UILoggingFilesPage.PAGE_URL.TrimStart('/') + Query).ToString();
    private string Query => (Source?.RelativePath ?? File) is { Length: > 0 } path ? "?file=" + Uri.EscapeDataString(path) : string.Empty;
    private IReadOnlyList<LogLineViewModel> ViewLines
    {
        get
        {
            if (_context is { } context) return context.Lines;
            if (_snapshot is null || _range is not { } range) return _snapshot?.Lines ?? [];
            if (_projection != (_snapshot, _range))
            {
                var filter = _snapshot.Filter;
                var matching = _snapshot.RetainedLines.Where(line => line.AbsoluteLineNumber >= range.From && line.AbsoluteLineNumber <= range.To
                    && (!filter.Severity.HasValue || filter.Severity == line.Severity) && (!filter.HasKeyword || !filter.OnlyCapture || line.IsMatch));
                var cap = Math.Max(1, LoggingService.UIOption.MaxDisplayLines);
                matching = _snapshot.ShowsEarlierWindow ? matching.Take(cap) : matching.TakeLast(cap);
                _spanLines = matching.Select((line, index) => filter.HasKeyword && filter.OnlyCapture
                    ? line.WithPosition(line.AbsoluteLineNumber, line.BufferIndex, index + 1) : line).ToArray();
                _projection = (_snapshot, _range);
            }
            return _spanLines;
        }
    }

    private IReadOnlyList<LogLineViewModel> PageLines => _viewer?.VisiblePage ?? ViewLines;
    private IEnumerable<LogLineViewModel> FacetLines => (_snapshot?.RetainedLines ?? []).Where(line =>
        (_range is not { } r || line.AbsoluteLineNumber >= r.From && line.AbsoluteLineNumber <= r.To)
        && (_snapshot?.Filter is not { HasKeyword:true, OnlyCapture:true } || line.IsMatch));

    private LogSourceIdentity? ViewSource => _context?.Source ?? Source;
    private DateTimeOffset? ViewReadAt => _context?.ReadAt ?? _snapshot?.GeneratedAt;
    private long ViewVersion => _context is not null ? _contextVersion : _snapshot?.Version ?? 0;
    private IEnumerable<int> LineCounts => LoggingService.UIOption.PresetLineCounts.Append(_lineLimit).Where(n => n > 0).Distinct().Order();

    protected override void OnInitialized()
    {
        _session = Workspace.GetReader();
        _subscription = _session.Subscribe();
        _lineLimit = Math.Max(1, LoggingService.UIOption.DefaultFetchLines);
        _onlyMatches = LoggingService.UIOption.DefaultOnlyCapture;
        if (Workspace.ReaderViewState is { } state)
        {
            _keyword = state.Keyword;
            _severity = state.Severity;
            _onlyMatches = state.OnlyMatches;
            _autoScroll = state.AutoScroll;
            _lineLimit = state.LineLimit;
            _context = state.Context;
            _contextVersion = state.ContextVersion;
            _position = state.Position;
            _livePosition = state.LivePosition;
            _pendingPosition = state.Position;
        }
        _snapshot = _session.GetSnapshot();
        _transfer = new LogBrowserTransfer(JSRuntime);
        _listener = ListenAsync();
    }

    protected override Task OnParametersSetAsync()
    {
        if (_lastRoute == NavigationManager.Uri) return Task.CompletedTask;
        _lastRoute = NavigationManager.Uri;
        var requested = File;
        var first = !_parametersLoaded;
        var load = first || _loadedFile != requested;
        _parametersLoaded = true;
        _loadedFile = requested;
        var restore = first && Workspace.ReaderViewState is not null && Keyword is null && SeverityQuery is null && Only is null && Span is null && Context is null && Line is null;
        var keyword = restore ? _keyword : Keyword;
        var severity = restore ? _severity : Enum.TryParse<LogSeverity>(SeverityQuery, true, out var level) && Enum.IsDefined(level) ? level : (LogSeverity?)null;
        var only = restore ? _onlyMatches : Only is null ? LoggingService.UIOption.DefaultOnlyCapture : Only != "0";
        var context = Context;
        var generation = Generation;
        var span = Span?.Split('-', 2);
        _range = span?.Length == 2 && long.TryParse(span[0], out var from) && long.TryParse(span[1], out var to) && from > 0 && to >= from ? (from,to) : null;
        if (Line is null) _initialEvidence = null;
        return RunCommandAsync(async () =>
        {
            if (load)
            {
                if (_fileCount is null)
                {
                    var files = await FileQuery.ListFilesAsync(_retirement.Token);
                    if (!files.IsFailure()) _fileCount = files.Data?.Count;
                }
                var synchronized = await _session!.SynchronizeAsync(_retirement.Token);
                if (_disposed) return;
                if (synchronized.IsFailure() || synchronized.Data is not { } current)
                {
                    _error = synchronized.WithoutData().Message ?? L["Workbench:ReadFailed"].Value;
                    return;
                }
                _snapshot = current;
                if (!Workspace.IsInitialized || !string.IsNullOrEmpty(requested) &&
                    !string.Equals(requested, current.Source?.RelativePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    _context = null;
                    _position = _livePosition = _pendingPosition = null;
                    _initializing = true;
                    var read = string.IsNullOrEmpty(requested)
                        ? await _session.InitializeAsync(_lineLimit, _retirement.Token)
                        : await _session.SwitchToFileAsync(requested, _lineLimit, _retirement.Token);
                    Adopt(read, sourceAdopted: true);
                    if (read.IsFailure()) return;
                }
                _initializing = false;
            }
            if (_snapshot is null || _disposed) return;
            if (_snapshot.Filter.Keyword != keyword || _snapshot.Filter.Severity != severity || _snapshot.Filter.OnlyCapture != only)
                Adopt(await _session!.UpdateFilterAsync(keyword, only, severity));
            _keyword = keyword;
            _severity = severity;
            _onlyMatches = only;
            if (context is null)
            {
                if (!restore && _context is not null) { _context = null; _pendingPosition = _livePosition; _returnFocus = true; }
            }
            else if (!long.TryParse(context, out var number)) _error = L["Design:ContextStaleToast"].Value;
            else if (_context?.TargetLineNumber != number || (generation is not null && _context?.Source?.Generation != generation))
            {
                var request = _contextRequest;
                _contextRequest = null;
                if (request?.AbsoluteLineNumber != number) request = _snapshot.RetainedLines.FirstOrDefault(l => l.AbsoluteLineNumber == number) is { } target && _snapshot.Source is { } source
                    ? new LogLineRequest(source, number, target.Raw) : null;
                if (request is null || (generation is not null && generation != request.Source.Generation))
                {
                    _context = null;
                    _error = L["Design:ContextStaleToast"].Value;
                }
                else await ReadContextAsync(request);
            }
        });
    }

    private async Task ListenAsync()
    {
        try
        {
            await foreach (var snapshot in _subscription!.Reader.ReadAllAsync(_retirement.Token))
                await InvokeAsync(() => { if (!_disposed && (_snapshot is null || snapshot.Version >= _snapshot.Version)) { _snapshot = snapshot; StateHasChanged(); } });
        }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
    }

    private Task RunCommandAsync(Func<Task> action)
    {
        lock (_ownership)
        {
            if (_disposed) return Task.CompletedTask;
            var task = RunAdmittedAsync(action);
            _operations.Add(task);
            _ = ForgetAsync(task);
            return task;
        }
    }

    private async Task ForgetAsync(Task task)
    {
        try { await task; }
        catch { /* The admitted command owns its error projection. */ }
        finally { lock (_ownership) _operations.Remove(task); }
    }

    private async Task RunAdmittedAsync(Func<Task> action)
    {
        var entered = false;
        try
        {
            await _commands.WaitAsync(_retirement.Token);
            entered = true;
            if (_disposed) return;
            _busy = true;
            _error = null;
            await action();
        }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally
        {
            if (entered) _commands.Release();
            if (!_disposed) { _busy = false; _initializing = false; StateHasChanged(); }
        }
    }

    private void Adopt(Result<ScreenLogSnapshot> result, bool sourceAdopted = false)
    {
        if (_disposed) return;
        var error = result.WithoutData();
        var snapshot = result.Data;
        if (result.IsFailure()) _error = error.Message ?? L["Workbench:ReadFailed"].Value;
        else { _snapshot = snapshot; if (sourceAdopted) Workspace.MarkInitialized(_session!); }
    }

    private Task ToggleFollowingAsync() => RunCommandAsync(async () =>
    {
        var result = IsRunning ? await _session!.PauseAsync() : await _session!.StartAsync(_retirement.Token);
        var error = result;
        if (result.IsFailure()) _error = error.Message;
        if (!_disposed) _snapshot = _session!.GetSnapshot();
    });

    private Task ReadLatestAsync() => RunCommandAsync(async () =>
    {
        var result = _snapshot?.FileExists == true
            ? await _session!.RefreshAsync(_retirement.Token)
            : await _session!.InitializeAsync(_lineLimit, _retirement.Token);
        Adopt(result, sourceAdopted: true);
    });

    private void Navigate(IReadOnlyDictionary<string, object?> values, bool replace = false) =>
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(values), replace: replace);

    private void ApplyFilter() => Navigate(new Dictionary<string, object?>
    {
        ["q"] = string.IsNullOrWhiteSpace(_keyword) ? null : _keyword, ["severity"] = _severity?.ToString(),
        ["only"] = _onlyMatches == LoggingService.UIOption.DefaultOnlyCapture ? null : _onlyMatches ? "1" : "0",
        ["context"] = null, ["generation"] = null
    }, replace:true);

    private bool CanChangeFilters => !_busy && !_initializing && _context is null && _snapshot?.FileExists == true;

    private void SubmitKeyword()
    {
        if (CanChangeFilters) ApplyFilter();
    }

    private void ClearKeyword()
    {
        if (!CanChangeFilters) return;
        _keyword = null;
        ApplyFilter();
    }
    private void FilterSeverity(LogSeverity severity) { _severity = _severity == severity ? null : severity; ApplyFilter(); }
    private void ApplyRowSeverity(LogSeverity severity)
    {
        _severity = severity;
        ApplyFilter();
    }

    private void ChangeMatchMode(bool only) { _onlyMatches = only; ApplyFilter(); }
    private EventCallback<T> LiveReaderAction<T>(Action<T> action) =>
        _context is null ? EventCallback.Factory.Create(this, action) : default;

    private void ApplySelectedText((LogLineViewModel line, string keyword) request) { _keyword = request.keyword; ApplyFilter(); }
    private Task ChangeLineLimitAsync(int count) => RunCommandAsync(async () =>
    {
        _lineLimit = count;
        Adopt(await _session!.InitializeAsync(count, _retirement.Token), sourceAdopted: true);
    });
    private void SetSpan(string? span) => Navigate(new Dictionary<string, object?> { ["span"] = span, ["context"] = null }, replace:true);

    private void Inspect(LogLineEvidence evidence)
    {
        _initialEvidence = evidence;
        Navigate(new Dictionary<string, object?> { ["file"] = evidence.Source?.RelativePath, ["line"] = evidence.Line.AbsoluteLineNumber, ["generation"] = evidence.Source?.Generation });
    }

    private void InspectRetained(LogLineViewModel line) =>
        Inspect(LogLineEvidence.Capture(line, Source, _snapshot?.GeneratedAt, (line.BufferIndex ?? 1) - 1));

    private void NavigateLine(long? line) => Navigate(new Dictionary<string, object?> { ["line"] = line,
        ["generation"] = line.HasValue || Context is not null ? Generation : null }, replace:line.HasValue);

    private async Task JumpAsync(long number)
    {
        if (_viewer is null) return;
        var index = ViewLines.ToList().FindIndex(l => l.AbsoluteLineNumber == number);
        if (index >= 0) await _viewer.ScrollToLineAsync(index);
        else if (_context is null && _snapshot?.RetainedLines.Any(l => l.AbsoluteLineNumber == number) == true)
        {
            _jumpAfterRender = number;
            var to = number + Math.Max(1, LoggingService.UIOption.MaxDisplayLines) - 1;
            Navigate(new Dictionary<string, object?> { ["span"] = $"{number}-{Math.Min(to, _snapshot.TotalFileLineCount)}",
                ["severity"] = null, ["only"] = "0" });
        }
    }

    private async Task<int> LoadEarlierAsync(int count)
    {
        var loaded = 0;
        await RunCommandAsync(async () =>
        {
            if (_context is not null) return;
            var result = await _session!.LoadMoreLinesAsync(count, _retirement.Token);
            var error = result.WithoutData();
            var value = result.Data;
            if (result.IsFailure()) _error = error.Message;
            else loaded = value;
            if (!_disposed) _snapshot = _session!.GetSnapshot();
        });
        return loaded;
    }

    private void ShowContext((LogLineViewModel line,int lineIndex) selection)
    {
        if (ViewSource is not { } source || selection.line.AbsoluteLineNumber is not { } number) return;
        _contextRequest = new LogLineRequest(source, number, selection.line.Raw);
        Navigate(new Dictionary<string, object?> { ["context"] = number, ["generation"] = source.Generation });
    }

    private void ShowContextFromEvidence(LogLineEvidence evidence)
    {
        if (evidence.Source is not { } source || evidence.Line.AbsoluteLineNumber is not { } number) return;
        _contextRequest = new LogLineRequest(source, number, evidence.Line.Raw);
        Navigate(new Dictionary<string, object?> {
            ["file"] = source.RelativePath, ["line"] = null, ["context"] = number, ["generation"] = source.Generation
        });
    }

    private void FilterFromEvidence(LogSeverity severity)
    {
        _severity = severity;
        Navigate(new Dictionary<string, object?> { ["line"] = null, ["context"] = null, ["generation"] = null, ["severity"] = severity.ToString() });
    }

    private async Task ReadContextAsync(LogLineRequest request)
    {
        var result = await _session!.ReadContextAsync(request, 50, _retirement.Token);
        if (result.IsFailure()) { _error = result.WithoutData().Message; _context = null; }
        else if (!_disposed)
        {
            if (_context is null) _livePosition = _position;
            _context = result.Data;
            _contextVersion++;
        }
    }

    private void ExitContext() => Navigate(new Dictionary<string, object?> { ["context"] = null, ["generation"] = null });
    private void PositionChanged(LogViewerPosition position)
    {
        _position = position;
        if (_context is null) _livePosition = position;
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_disposed && !_initializing && _viewer is not null && _jumpAfterRender is { } jump)
        {
            var index = ViewLines.ToList().FindIndex(l => l.AbsoluteLineNumber == jump);
            if (index >= 0) { _jumpAfterRender = null; return _viewer.ScrollToLineAsync(index); }
        }
        if (_disposed || _initializing || _viewer is null || (_pendingPosition is null && !_returnFocus)) return Task.CompletedTask;
        var position = _pendingPosition;
        var returnFocus = _returnFocus;
        _pendingPosition = null;
        _returnFocus = false;
        return RunCommandAsync(() => position is not null ? _viewer.RestorePositionAsync(position, returnFocus) : _viewer.FocusHeaderAsync());
    }

    private Task ExportRetainedAsync() => RunCommandAsync(async () =>
    {
        var result = await _session!.ExportBufferAsync();
        var error = result.WithoutData();
        var export = result.Data;
        if (result.IsFailure()) _error = error.Message;
        else await _transfer!.ExportAsync(export);
    });

    private Task ExportVisibleAsync() => RunCommandAsync(() => _snapshot is null ? Task.CompletedTask :
        _transfer!.ExportAsync(LogReadingSession.ExportSnapshot(_snapshot with { Lines = ViewLines, Source = ViewSource, GeneratedAt = ViewReadAt ?? _snapshot.GeneratedAt })));

    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            if (!_disposed && _session is not null)
                Workspace.SaveReaderViewState(_session, new LogReaderViewState(_keyword, _severity, _onlyMatches,
                    _autoScroll, _lineLimit, _context, _contextVersion, _position, _livePosition));
            _disposed = true;
            return new ValueTask(_disposeTask ??= RetireAsync());
        }
    }

    private async Task RetireAsync()
    {
        await _retirement.CancelAsync();
        if (_subscription is not null) await _subscription.DisposeAsync();
        if (_transfer is not null) await _transfer.DisposeAsync();
        Task[] admitted;
        lock (_ownership) admitted = _operations.ToArray();
        try { await Task.WhenAll(admitted); }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        if (_listener is not null) await _listener;
        _commands.Dispose();
        _retirement.Dispose();
    }
}
