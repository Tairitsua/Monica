using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Logging.Services.Support;
using Monica.Modules;

namespace Monica.Framework.UI.UILogging.Support;

/// <summary>Complete physical lines read from one observed file generation.</summary>
public readonly record struct LogReadResult(IReadOnlyList<string> Lines, long StartLineNumber)
{
    public bool FileExists { get; init; }
    public long TotalLineCount { get; init; }
    public string Generation { get; init; } = string.Empty;
    public bool SourceReset { get; init; }
    public DateTimeOffset ReadAt { get; init; }
}

/// <summary>
/// A reader-owned UTF-8 file cursor. Only LF-terminated physical lines are published; a CR before LF
/// is removed. Incomplete bytes and text remain pending until the writer completes that line.
/// </summary>
/// <remarks>
/// Reads are serialized. Cancellation throws and does not adopt an incomplete read. Truncation and
/// observable replacement invalidate the generation. Detection uses length, prior prefix/end bytes,
/// and Windows creation time; Unix creation timestamps can change on append when birth time is unavailable.
/// It is not an operating-system file identity and cannot prove every in-place rewrite.
/// A file read is bounded to its opening length, not a filesystem-atomic archive snapshot.
/// </remarks>
public sealed class LogTailService(
    IOptions<ModuleLoggingOption> loggingOptions,
    IOptions<ModuleLoggingUIOption> uiOptions,
    ILogger<LogTailService> logger)
{
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeSpan _pollingInterval = GetPollingInterval(uiOptions.Value);
    private readonly int _retainedTailLimit = (int)Math.Min(int.MaxValue, Math.Max(2L, (long)uiOptions.Value.MaxDisplayLines * 2));
    private string _logFilePath = ResolveLogFilePath(loggingOptions.Value, uiOptions.Value);
    private Cursor _cursor = Cursor.Empty(Guid.NewGuid().ToString("N"));

    public string LogFilePath { get { lock (_sync) return _logFilePath; } }
    public string LogDirectory => Path.GetDirectoryName(LogFilePath) ?? AppContext.BaseDirectory;
    public bool LogFileExists => File.Exists(LogFilePath);
    public long CurrentLineNumber { get { lock (_sync) return _cursor.LineCount; } }
    public long TotalFileLineCount => CurrentLineNumber;
    public string Generation { get { lock (_sync) return _cursor.Generation; } }
    public TimeSpan PollingInterval => _pollingInterval;

    /// <summary>Creates a fully independent cursor using the same immutable module configuration.</summary>
    internal LogTailService CreateReader() => new(loggingOptions, uiOptions, logger);

    /// <summary>Copies an adopted cursor for transactional session reads; no mutable byte arrays are shared with an in-progress read.</summary>
    internal LogTailService CreateCursorCopy()
    {
        var reader = CreateReader();
        lock (_sync) { reader._logFilePath = _logFilePath; reader._cursor = _cursor; }
        return reader;
    }

    /// <summary>Switches this cursor only; callers must stop their own following loop before switching.</summary>
    public void SwitchToFile(string newFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newFilePath);
        var path = Path.GetFullPath(newFilePath);
        _readGate.Wait();
        try { lock (_sync) { _logFilePath = path; _cursor = Cursor.Empty(Guid.NewGuid().ToString("N")); } }
        finally { _readGate.Release(); }
    }

    /// <summary>Reads the latest requested complete lines and adopts a fresh cursor after success.</summary>
    public Task<Result<LogReadResult>> ReadLatestLinesAsync(int lineCount, CancellationToken cancellationToken = default)
        => ReadAsync(Math.Max(1, lineCount), latest: true, cancellationToken);

    /// <summary>
    /// Polls appended complete lines once, retaining at most the effective raw retention limit from a burst.
    /// Counts and absolute numbering still include all complete lines. Replacement returns the initial window.
    /// </summary>
    public Task<Result<LogReadResult>> ReadNextAsync(CancellationToken cancellationToken = default)
        => ReadAsync(Math.Clamp(uiOptions.Value.DefaultFetchLines, 1, Math.Max(1, uiOptions.Value.MaxDisplayLines)), latest: false, cancellationToken);

    private async Task<Result<LogReadResult>> ReadAsync(int lineLimit, bool latest, CancellationToken token)
    {
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            string path;
            Cursor before;
            lock (_sync) { path = _logFilePath; before = _cursor; }
            if (!File.Exists(path))
            {
                var absent = Cursor.Empty(before.Exists ? Guid.NewGuid().ToString("N") : before.Generation) with { Initialized = true };
                token.ThrowIfCancellationRequested();
                lock (_sync) _cursor = absent;
                return Result.Success(CreateReadResult([], 1, absent, before.Exists));
            }

            await using var stream = OpenRead(path);
            var length = stream.Length;
            var creation = ReadCreationTime(path);
            var changed = before.Initialized && (!before.Exists || length < before.Position
                || creation != before.CreationTime
                || !await BytesMatchAsync(stream, 0, before.Prefix, length, token).ConfigureAwait(false)
                || !await BytesMatchAsync(stream, Math.Max(0, before.Position - before.Anchor.Length), before.Anchor, length, token).ConfigureAwait(false));
            var reset = latest || !before.Initialized || changed;
            var generation = changed ? Guid.NewGuid().ToString("N") : before.Generation;
            var position = reset ? 0 : before.Position;
            var lineCount = reset ? 0 : before.LineCount;
            var pending = reset ? new List<byte>() : new List<byte>(before.Pending);
            var queueLimit = reset ? lineLimit : _retainedTailLimit;
            var queue = new Queue<string>();
            stream.Position = position;
            var bytes = new byte[65536];
            while (position < length)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - position)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The log file changed extent while it was being read. Retry the read.");
                position += read;
                for (var index = 0; index < read; index++)
                {
                    if (bytes[index] != (byte)'\n') { pending.Add(bytes[index]); continue; }
                    if (pending.Count > 0 && pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
                    var lineBytes = pending.ToArray();
                    var skipBom = lineCount == 0 && lineBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
                    queue.Enqueue(Encoding.UTF8.GetString(lineBytes.AsSpan(skipBom)));
                    pending.Clear();
                    lineCount++;
                    if (queue.Count > queueLimit) queue.Dequeue();
                }
            }
            var prefix = await ReadBytesAsync(stream, 0, (int)Math.Min(256, length), token).ConfigureAwait(false);
            var anchor = await ReadBytesAsync(stream, Math.Max(0, length - 256), (int)Math.Min(256, length), token).ConfigureAwait(false);
            var after = new Cursor(generation, true, true, length, lineCount, pending.ToArray(), creation, prefix, anchor);
            token.ThrowIfCancellationRequested();
            lock (_sync) _cursor = after;
            return Result.Success(CreateReadResult(Array.AsReadOnly(queue.ToArray()), Math.Max(1, lineCount - queue.Count + 1), after, changed || !before.Initialized));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read log file {FilePath}.", LogFilePath);
            return Result.Failure<LogReadResult>($"Failed to read log file: {ex.Message}");
        }
        finally { _readGate.Release(); }
    }

    /// <summary>Reads earlier complete physical lines without moving this cursor or changing its source generation.</summary>
    public async Task<Result<LogReadResult>> ReadLinesBeforeAsync(long beforeLineNumber, int lineCount, CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Math.Max(1, beforeLineNumber - Math.Max(1, lineCount));
            var lines = await ReadRangeAsync(LogFilePath, start, beforeLineNumber - 1, cancellationToken).ConfigureAwait(false);
            return Result.Success(new LogReadResult(lines, start) { Generation = Generation, FileExists = File.Exists(LogFilePath), TotalLineCount = TotalFileLineCount, ReadAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure<LogReadResult>($"Failed to read earlier log lines: {ex.Message}"); }
        finally { _readGate.Release(); }
    }

    /// <summary>Compatibility line stream. Source-aware readers should use ReadNextAsync instead.</summary>
    public async IAsyncEnumerable<string> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await ReadNextAsync(cancellationToken).ConfigureAwait(false);
            var error = result.WithoutData();
            var batch = result.Data;
            if (result.IsFailure()) throw new IOException(error.Message);
            foreach (var line in batch.Lines) yield return line;
            await Task.Delay(_pollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks the last adopted fingerprint without changing the cursor.</summary>
    internal async Task<bool> IsCurrentAsync(string generation, CancellationToken token)
    {
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Cursor current;
            string path;
            lock (_sync) { current = _cursor; path = _logFilePath; }
            if (current.Generation != generation || !current.Exists || !File.Exists(path)) return false;
            await using var stream = OpenRead(path);
            return stream.Length >= current.Position && ReadCreationTime(path) == current.CreationTime
                && await BytesMatchAsync(stream, 0, current.Prefix, stream.Length, token).ConfigureAwait(false)
                && await BytesMatchAsync(stream, Math.Max(0, current.Position - current.Anchor.Length), current.Anchor, stream.Length, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The observed log source could not be verified: {FilePath}.", LogFilePath);
            return false;
        }
        finally { _readGate.Release(); }
    }

    internal static async Task<IReadOnlyList<string>> ReadRangeAsync(string path, long start, long end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (start > end || !File.Exists(path)) return [];
        await using var stream = OpenRead(path);
        var length = stream.Length;
        var bytes = new byte[65536];
        var pending = new List<byte>();
        var lines = new List<string>();
        long position = 0, number = 0;
        while (position < length && number < end)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - position)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The log file changed extent while reading context.");
            position += read;
            for (var index = 0; index < read && number < end; index++)
            {
                token.ThrowIfCancellationRequested();
                if (bytes[index] != (byte)'\n') { pending.Add(bytes[index]); continue; }
                if (pending.Count > 0 && pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
                var raw = pending.ToArray();
                var skipBom = number == 0 && raw.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
                number++;
                if (number >= start) lines.Add(Encoding.UTF8.GetString(raw.AsSpan(skipBom)));
                pending.Clear();
            }
        }
        return Array.AsReadOnly(lines.ToArray());
    }

    internal static string ResolveLogFilePath(ModuleLoggingOption logging, ModuleLoggingUIOption ui)
        => !string.IsNullOrWhiteSpace(ui.LogDirectory)
            ? Path.Combine(Path.GetFullPath(ui.LogDirectory), Path.GetFileName(LogFileLocation.Resolve(logging)))
            : LogFileLocation.Resolve(logging);

    private static TimeSpan GetPollingInterval(ModuleLoggingUIOption options)
    {
        if (options.PollingInterval <= TimeSpan.Zero || options.PollingInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options.PollingInterval), "Polling interval must be positive and supported by Task.Delay.");
        return options.PollingInterval < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : options.PollingInterval;
    }

    private static LogReadResult CreateReadResult(IReadOnlyList<string> lines, long start, Cursor cursor, bool reset)
        => new(lines, start) { Generation = cursor.Generation, FileExists = cursor.Exists, TotalLineCount = cursor.LineCount, SourceReset = reset, ReadAt = DateTimeOffset.UtcNow };

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    private static DateTime ReadCreationTime(string path)
        => OperatingSystem.IsWindows() ? File.GetCreationTimeUtc(path) : default;

    private static async Task<bool> BytesMatchAsync(FileStream stream, long offset, byte[] expected, long length, CancellationToken token)
    {
        if (expected.Length == 0) return true;
        if (offset + expected.Length > length) return false;
        var bytes = await ReadBytesAsync(stream, offset, expected.Length, token).ConfigureAwait(false);
        return bytes.AsSpan().SequenceEqual(expected);
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream stream, long offset, int count, CancellationToken token)
    {
        stream.Position = offset;
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private sealed record Cursor(string Generation, bool Initialized, bool Exists, long Position, long LineCount,
        byte[] Pending, DateTime CreationTime, byte[] Prefix, byte[] Anchor)
    {
        public static Cursor Empty(string generation) => new(generation, false, false, 0, 0, [], default, [], []);
    }
}
