using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.UILogging.Support;
using Monica.Modules;

namespace Monica.Framework.UI.UILogging.State;

/// <summary>
/// A caller-owned local UTF-8 file reader. File selection, filters, cursor, retention, subscriptions and
/// following work belong to this session. Built-in pages borrow the circuit-scoped LoggingWorkspaceSession
/// reader; custom callers must asynchronously dispose independently created sessions.
/// </summary>
/// <remarks>
/// Mutations are serialized. Cancellation throws before evidence adoption; a failed read preserves the
/// last successful source and lines and exposes Error. DisposeAsync is cached, cancels/drains admitted work
/// and completes subscriptions. The shared file-query service and host logging provider are borrowed.
/// </remarks>
public sealed class LogReadingSession : IAsyncDisposable
{
    private readonly ScreenLogBuffer _buffer;
    private LogTailService _tail;
    private readonly LogFileQueryService _files;
    private readonly IOptions<ModuleLoggingUIOption> _options;
    private readonly ILogger<LoggingService> _logger;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Channel<ScreenLogSnapshot>> _subscribers = [];
    private ScreenLogSnapshot _snapshot;
    private CancellationTokenSource? _followingCts;
    private Task? _followingTask;
    private Task? _disposeTask;
    private bool _retired;
    private bool _running;
    private long _version;
    private int _admitted;
    private TaskCompletionSource _drained = CompletedSource();
    private DateTimeOffset _readAt = DateTimeOffset.UtcNow;
    private bool _fileExists;
    private string? _error;

    internal LogReadingSession(ScreenLogBuffer buffer, LogTailService tail, LogFileQueryService files,
        IOptions<ModuleLoggingUIOption> options, ILogger<LoggingService> logger)
    {
        _buffer = buffer;
        _tail = tail;
        _files = files;
        _options = options;
        _logger = logger;
        _snapshot = Decorate(buffer.Snapshot());
    }

    /// <summary>Composition-time UI defaults; this reader does not mutate these shared options.</summary>
    public ModuleLoggingUIOption UIOption => _options.Value;
    /// <summary>The shared catalog's allowed inspection directory.</summary>
    public string LogDirectory => _files.LogDirectory;
    /// <summary>The full path of the last adopted source, rather than a pending selection.</summary>
    public string CurrentLogFilePath { get { lock (_sync) return _snapshot.Source!.FilePath; } }
    /// <summary>Whether this reader's following loop is currently enabled.</summary>
    public bool IsRunning { get { lock (_sync) return _snapshot.IsRunning; } }
    /// <summary>Total complete physical lines observed in the adopted file generation.</summary>
    public long TotalFileLineCount { get { lock (_sync) return _snapshot.TotalFileLineCount; } }
    /// <summary>Whether the source existed at the last successful read; this getter performs no filesystem query.</summary>
    public bool LogFileExists { get { lock (_sync) return _snapshot.FileExists; } }

    /// <summary>Returns the last adopted immutable evidence graph without reading the file.</summary>
    public ScreenLogSnapshot GetSnapshot()
    {
        lock (_sync) { ThrowIfRetired(); return _snapshot; }
    }

    /// <summary>
    /// Waits behind previously admitted mutations and returns the current immutable adopted snapshot
    /// without reading the file or changing its version. Shared-workspace components use this barrier
    /// before comparing a new route's requested source with the reader's source.
    /// </summary>
    /// <remarks>Cancellation and retirement follow the same admission contract as other session operations.</remarks>
    public Task<Result<ScreenLogSnapshot>> SynchronizeAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success(GetSnapshot()));
        }, cancellationToken);

    /// <summary>Replaces this reader's window with up to the effective display limit of latest complete physical lines.</summary>
    public Task<Result<ScreenLogSnapshot>> InitializeAsync(int requestedLines, CancellationToken cancellationToken = default)
        => ExecuteAsync(async token =>
        {
            var candidate = _tail.CreateCursorCopy();
            var result = await candidate.ReadLatestLinesAsync(Math.Clamp(requestedLines, 1, _buffer.MaxDisplayLines), token).ConfigureAwait(false);
            if (result.IsFailure()) { RecordFailure(result.Message); return Result.FromFailure<ScreenLogSnapshot>(result); }
            var read = result.Data;
            token.ThrowIfCancellationRequested();
            _tail = candidate;
            _buffer.Reset(read.Lines, read.StartLineNumber);
            return Result.Success(AdoptRead(read));
        }, cancellationToken);

    /// <summary>Polls this reader's selected source once, independently of whether following is enabled.</summary>
    public Task<Result<ScreenLogSnapshot>> RefreshAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(RefreshCoreAsync, cancellationToken);

    /// <summary>Starts this reader's following loop. The request token controls admission; the reader lifetime owns ongoing work.</summary>
    public Task<Result> StartAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (_running) return Result.Success();
            await StopFollowingAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _followingCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var followingToken = _followingCts.Token;
            _running = true;
            Publish();
            _followingTask = Task.Run(() => FollowAsync(followingToken), CancellationToken.None);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Stops and drains this session's following loop; other readers are unaffected.</summary>
    public Task<Result> PauseAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(async _ => { await StopFollowingAsync().ConfigureAwait(false); Publish(); return Result.Success(); }, cancellationToken);

    /// <summary>
    /// Prepares a new independent cursor and adopts it only after a successful read. Missing sources are
    /// an explicit successful missing state. A canceled or failed read preserves the previous evidence.
    /// The previous following loop is drained before the new source is adopted; the new source starts paused.
    /// </summary>
    public Task<Result<ScreenLogSnapshot>> SwitchToFileAsync(string relativePath, int initialLines, CancellationToken cancellationToken = default)
        => ExecuteAsync(async token =>
        {
            LogTailService candidate;
            try
            {
                var path = _files.ResolveFilePath(relativePath);
                candidate = _tail.CreateReader();
                candidate.SwitchToFile(path);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { RecordFailure(ex.Message); return Result.Failure<ScreenLogSnapshot>($"Failed to select log file: {ex.Message}"); }
            var result = await candidate.ReadLatestLinesAsync(Math.Clamp(initialLines, 1, _buffer.MaxDisplayLines), token).ConfigureAwait(false);
            if (result.IsFailure()) { RecordFailure(result.Message); return Result.FromFailure<ScreenLogSnapshot>(result); }
            var read = result.Data;
            token.ThrowIfCancellationRequested();
            await StopFollowingAsync().ConfigureAwait(false);
            if (token.IsCancellationRequested) { Publish(); token.ThrowIfCancellationRequested(); }
            _tail = candidate;
            _buffer.Reset(read.Lines, read.StartLineNumber);
            return Result.Success(AdoptRead(read));
        }, cancellationToken);

    /// <summary>Matches keyword text ordinally across the verbatim physical line; severity is an independent exact parsed-level filter.</summary>
    public Task<Result<ScreenLogSnapshot>> UpdateFilterAsync(string? keyword, bool onlyCapture, LogSeverity? severity = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            _buffer.ApplyFilter(LogFilterState.Create(keyword, onlyCapture, severity));
            return Task.FromResult(Result.Success(Publish()));
        }, cancellationToken);

    /// <summary>Loads earlier physical lines from the same verified observed generation, leaving following state unchanged.</summary>
    public Task<Result<int>> LoadMoreLinesAsync(int lineCount = 50, CancellationToken cancellationToken = default)
        => ExecuteAsync<Result<int>>(async token =>
        {
            var before = GetSnapshot();
            if (before.FirstRetainedLineNumber is not { } first || first <= 1) return Result.Success(0);
            if (!await _tail.IsCurrentAsync(before.Source!.Generation, token).ConfigureAwait(false))
                return Result.Failure<int>("The log source changed; refresh before loading earlier lines.");
            var result = await _tail.ReadLinesBeforeAsync(first, Math.Clamp(lineCount, 1, _buffer.MaxRetainedLines), token).ConfigureAwait(false);
            if (result.IsFailure()) { RecordFailure(result.Message); return Result.FromFailure<int>(result); }
            var read = result.Data;
            if (!await _tail.IsCurrentAsync(before.Source.Generation, token).ConfigureAwait(false))
                return Result.Failure<int>("The log source changed during the history read; no history was adopted.");
            token.ThrowIfCancellationRequested();
            if (read.Lines.Count == 0) return Result.Success(0);
            _buffer.Prepend(read.Lines, read.StartLineNumber);
            Publish();
            return Result.Success(read.Lines.Count);
        }, cancellationToken);

    /// <summary>Freezes a retained physical line and its observed source generation for later context inspection.</summary>
    public LogLineRequest? CreateLineRequest(long absoluteLineNumber)
    {
        lock (_sync)
        {
            ThrowIfRetired();
            var line = _snapshot.RetainedLines.FirstOrDefault(line => line.AbsoluteLineNumber == absoluteLineNumber);
            return line is null ? null : new LogLineRequest(_snapshot.Source!, absoluteLineNumber, line.Raw);
        }
    }

    /// <summary>
    /// Reads a frozen line's surrounding complete physical lines without replacing the reader buffer.
    /// Source generation and verbatim target text are verified before adoption. Appends are allowed;
    /// source switches, observable replacement and changed target text return a failed result.
    /// </summary>
    public Task<Result<ContextLogResult>> ReadContextAsync(LogLineRequest request, int contextLines = 50,
        CancellationToken cancellationToken = default)
        => ExecuteAsync<Result<ContextLogResult>>(async token =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.AbsoluteLineNumber < 1 || contextLines < 0) return Result.Failure<ContextLogResult>("Log context requires a positive target line and nonnegative context count.");
            var source = GetSnapshot().Source;
            if (source is null || source != request.Source || !await _tail.IsCurrentAsync(request.Source.Generation, token).ConfigureAwait(false))
                return Result.Failure<ContextLogResult>("This line belongs to a changed log source. Refresh and select the line again.");
            var result = await _files.ReadContextAsync(source.FilePath, request.AbsoluteLineNumber, contextLines, token).ConfigureAwait(false);
            if (result.IsFailure()) return result;
            var context = result.Data;
            var target = context.Lines.FirstOrDefault(line => line.IsContextTarget);
            if (target?.Raw != request.Raw || !await _tail.IsCurrentAsync(source.Generation, token).ConfigureAwait(false))
                return Result.Failure<ContextLogResult>("The log source or target text changed during the context read; no context was adopted.");
            token.ThrowIfCancellationRequested();
            return Result.Success(context with { Source = source, ReadAt = DateTimeOffset.UtcNow });
        }, cancellationToken);

    /// <summary>Exports a frozen copy of all retained raw lines from this reader, independent of visible filters.</summary>
    public Task<Result<LogExportResult>> ExportBufferAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            var snapshot = GetSnapshot();
            return Task.FromResult(Result.Success(ExportLines(snapshot, snapshot.RetainedLines)));
        }, cancellationToken);

    /// <summary>Exports only the visible physical lines in the supplied immutable snapshot, normalized as UTF-8 with LF separators.</summary>
    public static LogExportResult ExportSnapshot(ScreenLogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ExportLines(snapshot, snapshot.Lines);
    }

    /// <summary>Lists the shared catalog without changing this reader's source or following state.</summary>
    public Task<Result<IReadOnlyList<LogFileDescriptor>>> ListFilesAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(token => _files.ListFilesAsync(token), cancellationToken);

    /// <summary>The caller owns and must dispose a returned live download stream.</summary>
    public Task<Result<FileStream>> OpenFileAsync(string relativePath, CancellationToken cancellationToken = default)
        => ExecuteAsync(token => _files.OpenFileAsync(relativePath, token), cancellationToken);

    /// <summary>Subscribes to this reader's immutable snapshots. Disposing the subscription does not stop following.</summary>
    public LoggingSubscription Subscribe()
    {
        lock (_sync)
        {
            ThrowIfRetired();
            var channel = Channel.CreateBounded<ScreenLogSnapshot>(new BoundedChannelOptions(1)
            {
                SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.DropOldest
            });
            _subscribers.Add(channel);
            channel.Writer.TryWrite(_snapshot);
            return new LoggingSubscription(this, channel);
        }
    }

    internal void RemoveSubscriber(Channel<ScreenLogSnapshot> channel)
    {
        lock (_sync) { _subscribers.Remove(channel); channel.Writer.TryComplete(); }
    }

    /// <summary>Cancels and drains owned work once; repeated callers observe the same retirement task.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _retired = true;
            _disposeTask = RetireAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task RetireAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task drained;
        lock (_sync) drained = _drained.Task;
        await drained.ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopFollowingAsync().ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var channel in _subscribers) channel.Writer.TryComplete();
                _subscribers.Clear();
            }
        }
        finally { _operations.Release(); _lifetime.Dispose(); }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        CancellationTokenSource admission;
        lock (_sync)
        {
            ThrowIfRetired();
            cancellationToken.ThrowIfCancellationRequested();
            admission = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            if (_admitted++ == 0) _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var acquired = false;
        try
        {
            await _operations.WaitAsync(admission.Token).ConfigureAwait(false);
            acquired = true;
            admission.Token.ThrowIfCancellationRequested();
            return await action(admission.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _operations.Release();
            admission.Dispose();
            lock (_sync) if (--_admitted == 0) _drained.TrySetResult();
        }
    }

    private async Task<Result<ScreenLogSnapshot>> RefreshCoreAsync(CancellationToken token)
    {
        var candidate = _tail.CreateCursorCopy();
        var result = await candidate.ReadNextAsync(token).ConfigureAwait(false);
        if (result.IsFailure()) { RecordFailure(result.Message); return Result.FromFailure<ScreenLogSnapshot>(result); }
        var read = result.Data;
        token.ThrowIfCancellationRequested();
        _tail = candidate;
        if (read.SourceReset) _buffer.Reset(read.Lines, read.StartLineNumber);
        else _buffer.AppendRange(read.Lines, read.StartLineNumber);
        return Result.Success(AdoptRead(read));
    }

    private ScreenLogSnapshot AdoptRead(LogReadResult read)
    {
        _fileExists = read.FileExists;
        _readAt = read.ReadAt;
        _error = null;
        return Publish();
    }

    private async Task FollowAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_tail.PollingInterval, token).ConfigureAwait(false);
                await RefreshAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_retired) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log reading session following failed.");
            lock (_sync)
            {
                if (!_retired) { _running = false; _error = ex.Message; Publish(); }
            }
        }
    }

    private async Task StopFollowingAsync()
    {
        var cancellation = _followingCts;
        if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        if (_followingTask is not null) await _followingTask.ConfigureAwait(false);
        _followingTask = null;
        _followingCts = null;
        cancellation?.Dispose();
        _running = false;
    }

    private void RecordFailure(string? error) { _error = error ?? "The log read failed."; Publish(); }

    private ScreenLogSnapshot Publish()
    {
        lock (_sync)
        {
            var current = Decorate(_buffer.Snapshot()) with { Version = ++_version };
            if (_retired) return current;
            _snapshot = current;
            foreach (var channel in _subscribers) channel.Writer.TryWrite(current);
            return current;
        }
    }

    private ScreenLogSnapshot Decorate(ScreenLogSnapshot buffer)
        => buffer with
        {
            Source = new LogSourceIdentity(_tail.LogFilePath, Path.GetRelativePath(_files.LogDirectory, _tail.LogFilePath)
                .Replace(Path.DirectorySeparatorChar, '/'), _tail.Generation),
            IsRunning = _running, FileExists = _fileExists, TotalFileLineCount = _tail.TotalFileLineCount,
            GeneratedAt = _readAt, Error = _error
        };

    private static LogExportResult ExportLines(ScreenLogSnapshot snapshot, IReadOnlyList<LogLineViewModel> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line.Raw).Append('\n');
        var source = snapshot.Source is { } identity ? Path.GetFileNameWithoutExtension(identity.FilePath) : "log";
        return new LogExportResult($"{source}-{snapshot.GeneratedAt:yyyyMMddHHmmss}-v{snapshot.Version}.log", "text/plain", Encoding.UTF8.GetBytes(text.ToString()));
    }

    private void ThrowIfRetired() => ObjectDisposedException.ThrowIf(_retired, this);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>Owns a single reader subscription; disposal is idempotent and completes that channel.</summary>
public sealed class LoggingSubscription : IAsyncDisposable
{
    private readonly LogReadingSession _session;
    private readonly Channel<ScreenLogSnapshot> _channel;
    private int _disposed;
    internal LoggingSubscription(LogReadingSession session, Channel<ScreenLogSnapshot> channel) { _session = session; _channel = channel; }
    public ChannelReader<ScreenLogSnapshot> Reader => _channel.Reader;
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _session.RemoveSubscriber(_channel);
        return ValueTask.CompletedTask;
    }
}
