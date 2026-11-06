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
    private IReadOnlyList<LogLineViewModel> ViewLines => _context?.Lines ?? _snapshot?.Lines ?? [];
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
        if (_parametersLoaded && string.Equals(_loadedFile, File, StringComparison.Ordinal)) return Task.CompletedTask;
        var requested = File;
        _loadedFile = requested;
        _parametersLoaded = true;
        return RunCommandAsync(async () =>
        {
            var synchronized = await _session!.SynchronizeAsync(_retirement.Token);
            if (_disposed) return;
            var synchronizationError = synchronized.WithoutData();
            var current = synchronized.Data;
            if (synchronized.IsFailure() || current is null)
            {
                _error = synchronizationError.Message ?? "The log reader snapshot was unavailable.";
                return;
            }
            _snapshot = current;
            if (Workspace.IsInitialized && (string.IsNullOrEmpty(requested) ||
                string.Equals(requested, current.Source?.RelativePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                _initializing = false;
                return;
            }
            _context = null;
            _position = _livePosition = _pendingPosition = null;
            _initializing = true;
            var result = string.IsNullOrEmpty(requested)
                ? await _session!.InitializeAsync(_lineLimit, _retirement.Token)
                : await _session!.SwitchToFileAsync(requested, _lineLimit, _retirement.Token);
            Adopt(result, sourceAdopted: true);
            _initializing = false;
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
        _context = null;
        Adopt(await _session!.InitializeAsync(_lineLimit, _retirement.Token), sourceAdopted: true);
    });

    private Task ApplyFilterAsync() => RunCommandAsync(async () =>
    {
        _context = null;
        Adopt(await _session!.UpdateFilterAsync(_keyword, _onlyMatches, _severity));
    });

    private Task ClearFilterAsync() { _keyword = null; _severity = null; return ApplyFilterAsync(); }
    private Task SearchKeyDownAsync(KeyboardEventArgs args) => args.Key == "Enter" ? ApplyFilterAsync() : Task.CompletedTask;
    private Task ChangeSeverityAsync(ChangeEventArgs args) { _severity = Enum.TryParse<LogSeverity>(args.Value?.ToString(), out var severity) ? severity : null; return ApplyFilterAsync(); }
    private Task FilterSeverityAsync(LogSeverity severity) { _severity = severity; return ApplyFilterAsync(); }
    private Task ChangeMatchModeAsync(ChangeEventArgs args) { _onlyMatches = args.Value is true; return ApplyFilterAsync(); }
    private Task ApplySelectedTextAsync((LogLineViewModel line, string keyword) request) { _keyword = request.keyword; return ApplyFilterAsync(); }
    private Task ChangeLineLimitAsync(ChangeEventArgs args) { if (int.TryParse(args.Value?.ToString(), out var count)) _lineLimit = count; return ReadLatestAsync(); }

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

    private Task ShowContextAsync((LogLineViewModel line, int lineIndex) selection)
    {
        var source = ViewSource;
        if (source is null || selection.line.AbsoluteLineNumber is not { } number) return Task.CompletedTask;
        var request = new LogLineRequest(source, number, selection.line.Raw);
        return RunCommandAsync(async () =>
        {
            var result = await _session!.ReadContextAsync(request, 50, _retirement.Token);
            var error = result.WithoutData();
            var context = result.Data;
            if (result.IsFailure()) _error = error.Message;
            else if (!_disposed)
            {
                if (_context is null) _livePosition = _position;
                _context = context;
                _contextVersion++;
            }
        });
    }

    private void ExitContext() { _context = null; _pendingPosition = _livePosition; _returnFocus = true; }

    private void PositionChanged(LogViewerPosition position)
    {
        _position = position;
        if (_context is null) _livePosition = position;
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
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
