using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Monica.Framework.UI.UILogging.Models;

/// <summary>
/// Log matching fragments for front-end highlighting
/// </summary>
/// <param name="Start">Start position</param>
/// <param name="Length">Match length</param>
public readonly record struct LogMatchSegment(int Start, int Length);

/// <summary>
/// Log level
/// </summary>
public enum LogSeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    Unknown
}

/// <summary>
/// Log filter status
/// </summary>
public sealed class LogFilterState
{
    private LogFilterState()
    {
    }

    public string? Keyword { get; private init; }

    public bool OnlyCapture { get; private init; }

    public DateTimeOffset LastUpdated { get; private init; } = DateTimeOffset.UtcNow;

    public LogSeverity? Severity { get; private init; }

    public bool HasKeyword => !string.IsNullOrWhiteSpace(Keyword);

    public bool IsActive => HasKeyword || Severity.HasValue;

    public static LogFilterState Disabled => new()
    {
        Keyword = null,
        OnlyCapture = false
    };

    public static LogFilterState Create(string? keyword, bool onlyCapture, LogSeverity? severity = null)
    {
        return new LogFilterState
        {
            Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            OnlyCapture = onlyCapture,
            Severity = severity,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    public LogFilterState WithToggleOnlyCapture(bool onlyCapture)
    {
        return Create(Keyword, onlyCapture, Severity);
    }

    public override string ToString()
    {
        return IsActive ? $"Keyword={Keyword}, OnlyCapture={OnlyCapture}, Severity={Severity}" : "Disabled";
    }
}

/// <summary>
/// Screen log snapshot
/// </summary>
public sealed record ScreenLogSnapshot
{
    public required IReadOnlyList<LogLineViewModel> Lines { get; init; }

    /// <summary>All retained raw evidence, independent of display limits and filters.</summary>
    public IReadOnlyList<LogLineViewModel> RetainedLines { get; init; } = [];

    public required long Version { get; init; }

    public required int TotalStoredLines { get; init; }

    public required LogFilterState Filter { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The file and observed content generation; absent only on standalone legacy buffers.</summary>
    public LogSourceIdentity? Source { get; init; }

    /// <summary>Whether the owning reader is following appended complete physical lines.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Whether the source existed at the last successful read.</summary>
    public bool FileExists { get; init; }

    /// <summary>Complete physical lines observed in this file generation, including discarded history.</summary>
    public long TotalFileLineCount { get; init; }

    /// <summary>Absolute number of the first retained physical line; retention may contain gaps after earlier-history loading.</summary>
    public long? FirstRetainedLineNumber { get; init; }
    /// <summary>Absolute number of the last retained physical line.</summary>
    public long? LastRetainedLineNumber { get; init; }

    /// <summary>The latest read failure, without discarding the last successful evidence.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Log lines for interface display
/// </summary>
public sealed record LogLineViewModel
{
    private LogLineViewModel(
        string raw,
        string message,
        string header,
        string levelText,
        LogSeverity severity,
        string thread,
        DateTimeOffset? timestamp)
    {
        Raw = raw;
        Message = message;
        Header = header;
        LevelText = levelText;
        Severity = severity;
        Thread = thread;
        Timestamp = timestamp;
    }

    public string Raw { get; }

    public string Message { get; }

    public string Header { get; }

    public string LevelText { get; }

    public LogSeverity Severity { get; }

    public string Thread { get; }

    public DateTimeOffset? Timestamp { get; }

    public IReadOnlyList<LogMatchSegment>? MatchSegments { get; private init; }

    public bool IsMatch { get; private init; }

    /// <summary>
    /// Absolute line number (line number in the original log file)
    /// </summary>
    public long? AbsoluteLineNumber { get; private init; }

    /// <summary>
    /// Buffer index (position in unfiltered buffer, starting from 1)
    /// </summary>
    public int? BufferIndex { get; private init; }

    /// <summary>
    /// Index after filtering (position in filtered view, starting from 1, only has value when filtering)
    /// </summary>
    public int? FilteredIndex { get; private init; }

    /// <summary>
    /// Whether it is the target line of the context jump
    /// </summary>
    public bool IsContextTarget { get; private init; }

    public static LogLineViewModel FromRaw(string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return new LogLineViewModel(rawLine, rawLine, string.Empty, string.Empty, LogSeverity.Unknown, string.Empty, null);
        }

        var parsed = LogLineParser.Parse(rawLine);
        return new LogLineViewModel(
            rawLine,
            parsed.Message,
            parsed.Header,
            parsed.LevelText,
            parsed.Severity,
            parsed.Thread,
            parsed.Timestamp);
    }

    /// <summary>Returns an immutable copy with keyword match state. Keyword matching uses the verbatim physical line.</summary>
    public LogLineViewModel WithFilter(LogFilterState filter)
    {
        var segments = filter.HasKeyword ? Highlight(Message, filter.Keyword!) : null;
        return this with
        {
            MatchSegments = segments,
            IsMatch = filter.HasKeyword && Raw.Contains(filter.Keyword!, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<LogMatchSegment>? Highlight(string message, string keyword)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(keyword))
        {
            return null;
        }

        var list = ImmutableArray.CreateBuilder<LogMatchSegment>();

        var index = 0;
        while (index < message.Length)
        {
            var matchIndex = message.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            list.Add(new LogMatchSegment(matchIndex, keyword.Length));
            index = matchIndex + keyword.Length;
        }

        return list.Count == 0 ? null : list.ToImmutable();
    }

    /// <summary>
    /// Set location
    /// </summary>
    public LogLineViewModel WithPosition(long? absoluteLineNumber, int? bufferIndex, int? filteredIndex = null)
    {
        return this with { AbsoluteLineNumber = absoluteLineNumber, BufferIndex = bufferIndex, FilteredIndex = filteredIndex };
    }

    /// <summary>
    /// Mark as contextual target row
    /// </summary>
    public LogLineViewModel WithContextTarget(bool isTarget)
    {
        return this with { IsContextTarget = isTarget };
    }

    /// <summary>
    /// Get formatted copied text
    /// </summary>
    public string GetCopyText(LogCopyFormat format)
    {
        return format switch
        {
            LogCopyFormat.Raw => Raw,
            LogCopyFormat.MessageOnly => Message,
            LogCopyFormat.WithTimestamp => Timestamp.HasValue
                ? $"[{Timestamp.Value:yyyy-MM-dd HH:mm:ss.fff}] {Message}"
                : Message,
            LogCopyFormat.WithLineNumber => AbsoluteLineNumber.HasValue
                ? $"Line {AbsoluteLineNumber}: {Raw}"
                : Raw,
            _ => Raw
        };
    }

}

/// <summary>
/// Log replication format
/// </summary>
public enum LogCopyFormat
{
    /// <summary>
    /// Original full log line
    /// </summary>
    Raw,

    /// <summary>
    /// Message content only
    /// </summary>
    MessageOnly,

    /// <summary>
    /// timestamped messages
    /// </summary>
    WithTimestamp,

    /// <summary>
    /// Full log with line numbers
    /// </summary>
    WithLineNumber
}

/// <summary>
/// Log file information
/// </summary>
/// <param name="FileName">File name</param>
/// <param name="Size">File size</param>
/// <param name="LastModified">Last modified time in UTC</param>
/// <param name="RelativePath">Relative path</param>
public sealed record class LogFileDescriptor(string FileName, long Size, DateTime LastModified, string RelativePath);

/// <summary>
/// Export results
/// </summary>
/// <param name="FileName">File name</param>
/// <param name="ContentType">Content Type</param>
/// <param name="Content">File content</param>
public readonly record struct LogExportResult(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Context log reading results
/// </summary>
/// <param name="Lines">Log line list</param>
/// <param name="TargetLineNumber">Target line number</param>
/// <param name="StartLineNumber">Start line number</param>
/// <param name="EndLineNumber">End line number</param>
public readonly record struct ContextLogResult(
    IReadOnlyList<LogLineViewModel> Lines,
    long TargetLineNumber,
    long StartLineNumber,
    long EndLineNumber)
{
    public LogSourceIdentity? Source { get; init; }
    public DateTimeOffset ReadAt { get; init; }
}

/// <summary>Identifies an observed file generation. It is not an operating-system file identity or archive guarantee.</summary>
public sealed record LogSourceIdentity(string FilePath, string RelativePath, string Generation);

/// <summary>A frozen physical-line request bound to an observed source generation and verbatim target text.</summary>
public sealed record LogLineRequest(LogSourceIdentity Source, long AbsoluteLineNumber, string Raw);

internal static class LogLineParser
{
    private static readonly Regex LogRegex = new(
        @"^\[(?<timestamp>.+?)\s+(?<level>[A-Z]+)\s+(?<thread>[^\]]*?)\]\s*(?<message>.*)$",
        RegexOptions.Compiled);

    public static ParsedLogLine Parse(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return new ParsedLogLine(rawLine, string.Empty, string.Empty, LogSeverity.Unknown, string.Empty, null, string.Empty);
        }

        var match = LogRegex.Match(rawLine);
        if (!match.Success)
        {
            return new ParsedLogLine(rawLine, string.Empty, rawLine, LogSeverity.Unknown, string.Empty, null, rawLine);
        }

        var timestampText = match.Groups["timestamp"].Value;
        var levelText = match.Groups["level"].Value;
        var thread = match.Groups["thread"].Value;
        var message = match.Groups["message"].Value;

        DateTimeOffset? timestamp = null;
        if (DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTimestamp))
        {
            timestamp = parsedTimestamp;
        }

        return new ParsedLogLine(
            rawLine,
            $"[{timestampText} {levelText} {thread}]",
            message,
            MapSeverity(levelText),
            levelText,
            timestamp,
            thread);
    }

    private static LogSeverity MapSeverity(string? levelText)
    {
        if (string.IsNullOrWhiteSpace(levelText))
        {
            return LogSeverity.Unknown;
        }

        return levelText.ToUpperInvariant() switch
        {
            "VRB" or "TRC" or "TRACE" => LogSeverity.Trace,
            "DBG" or "DEBUG" => LogSeverity.Debug,
            "INF" or "INFO" or "INFORMATION" => LogSeverity.Information,
            "WRN" or "WARN" or "WARNING" => LogSeverity.Warning,
            "ERR" or "ERROR" => LogSeverity.Error,
            "FTL" or "CRT" or "CRITICAL" or "FATAL" => LogSeverity.Critical,
            _ => LogSeverity.Unknown
        };
    }
}

internal readonly record struct ParsedLogLine(
    string Raw,
    string Header,
    string Message,
    LogSeverity Severity,
    string LevelText,
    DateTimeOffset? Timestamp,
    string Thread);
