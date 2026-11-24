using System.Text;
using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.Framework.UI.UILogging.Models;

/// <summary>
/// A reader-owned bounded physical-line buffer. Snapshots and their lines are detached and immutable.
/// </summary>
public sealed class ScreenLogBuffer(IOptions<ModuleLoggingUIOption> options)
{
    private readonly LinkedList<StoredLine> _rawLines = new();
    private readonly object _syncRoot = new();
    private readonly int _maxDisplayLines = Math.Max(1, options.Value.MaxDisplayLines);
    private readonly int _maxRetainedLines = (int)Math.Min(int.MaxValue, Math.Max(2L, (long)options.Value.MaxDisplayLines * 2));
    private LogFilterState _filter = LogFilterState.Disabled;
    private long _version;
    private long _nextLineNumber = 1;
    private bool _showEarlier;
    private DateTimeOffset _generatedAt = DateTimeOffset.UtcNow;

    /// <summary>Effective display limit; nonpositive option values are clamped to one.</summary>
    public int MaxDisplayLines => _maxDisplayLines;

    /// <summary>Effective raw retention limit, twice the configured display limit and at least two.</summary>
    public int MaxRetainedLines => _maxRetainedLines;

    public LogFilterState CurrentFilter { get { lock (_syncRoot) return _filter; } }

    /// <summary>Replaces evidence and establishes the absolute number of its first complete physical line.</summary>
    public ScreenLogSnapshot Reset(IEnumerable<string> lines, long startLineNumber = 1)
    {
        lock (_syncRoot)
        {
            _rawLines.Clear();
            _nextLineNumber = Math.Max(1, startLineNumber);
            _showEarlier = false;
            foreach (var line in lines)
                if (line is not null) _rawLines.AddLast(new StoredLine(_nextLineNumber++, line));
            TrimFirst();
            return BuildSnapshot(true);
        }
    }

    /// <summary>Appends the next complete physical line, preserving numbering across history retention gaps.</summary>
    public ScreenLogSnapshot Append(string line)
    {
        lock (_syncRoot)
        {
            _rawLines.AddLast(new StoredLine(_nextLineNumber++, line));
            _showEarlier = false;
            TrimFirst();
            return BuildSnapshot(true);
        }
    }

    /// <summary>Appends one complete read batch with source-qualified absolute numbering and builds only one snapshot.</summary>
    public ScreenLogSnapshot AppendRange(IReadOnlyList<string> lines, long startLineNumber)
    {
        lock (_syncRoot)
        {
            if (lines.Count == 0) return BuildSnapshot(false);
            foreach (var line in lines) _rawLines.AddLast(new StoredLine(startLineNumber++, line));
            _nextLineNumber = startLineNumber;
            _showEarlier = false;
            TrimFirst();
            return BuildSnapshot(true);
        }
    }

    /// <summary>Prepends earlier complete physical lines and shows the earlier window until another append.</summary>
    public ScreenLogSnapshot Prepend(IEnumerable<string> lines, long startLineNumber)
    {
        lock (_syncRoot)
        {
            var incoming = lines.Select((raw, index) => new StoredLine(startLineNumber + index, raw))
                .Where(line => line.Raw is not null && line.Number < (_rawLines.First?.Value.Number ?? _nextLineNumber)).ToArray();
            foreach (var line in incoming.Reverse()) _rawLines.AddFirst(line);
            while (_rawLines.Count > _maxRetainedLines) _rawLines.RemoveLast();
            _showEarlier = true;
            return BuildSnapshot(incoming.Length > 0);
        }
    }

    public ScreenLogSnapshot ApplyFilter(LogFilterState filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_syncRoot)
        {
            _filter = filter;
            return BuildSnapshot(true);
        }
    }

    public ScreenLogSnapshot Snapshot() { lock (_syncRoot) return BuildSnapshot(false); }

    /// <summary>Exports the legacy host buffer with timestamp and retention headers, independent of display filters.</summary>
    public string ExportCurrent()
    {
        lock (_syncRoot)
        {
            var text = new StringBuilder();
            text.AppendLine($"# Exported at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            if (_filter.IsActive)
            {
                text.Append($"# Filter: Keyword={_filter.Keyword}, OnlyCapture={_filter.OnlyCapture}");
                if (_filter.Severity is { } severity)
                {
                    text.Append($", Severity={severity}");
                }
                text.AppendLine();
            }
            text.AppendLine($"# Total Stored Lines: {_rawLines.Count}").AppendLine();
            foreach (var line in _rawLines) text.AppendLine(line.Raw);
            return text.ToString();
        }
    }

    private ScreenLogSnapshot BuildSnapshot(bool changed)
    {
        if (changed) { _version++; _generatedAt = DateTimeOffset.UtcNow; }
        var retained = _rawLines.Select((line, index) => LogLineViewModel.FromRaw(line.Raw)
            .WithFilter(_filter).WithPosition(line.Number, index + 1)).ToArray();
        var matching = retained.Where(line => (!_filter.Severity.HasValue || line.Severity == _filter.Severity.Value)
            && (!_filter.HasKeyword || !_filter.OnlyCapture || line.IsMatch));
        var visible = (_showEarlier ? matching.Take(_maxDisplayLines) : matching.TakeLast(_maxDisplayLines)).ToArray();
        if (_filter.HasKeyword && _filter.OnlyCapture)
            visible = visible.Select((line, index) => line.WithPosition(line.AbsoluteLineNumber, line.BufferIndex, index + 1)).ToArray();
        return new ScreenLogSnapshot
        {
            Lines = Array.AsReadOnly(visible),
            ShowsEarlierWindow = _showEarlier,
            RetainedLines = Array.AsReadOnly(retained),
            Version = _version,
            TotalStoredLines = retained.Length,
            Filter = _filter,
            GeneratedAt = _generatedAt,
            FirstRetainedLineNumber = _rawLines.First?.Value.Number,
            LastRetainedLineNumber = _rawLines.Last?.Value.Number
        };
    }

    private void TrimFirst() { while (_rawLines.Count > _maxRetainedLines) _rawLines.RemoveFirst(); }

    private readonly record struct StoredLine(long Number, string Raw);
}
