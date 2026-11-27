using System.Collections.ObjectModel;
using Monica.Core.Extensions;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Models;
using Monica.Tool.Diagnostics;
using Monica.Tool.Extensions;

namespace Monica.DataChannel.Middlewares;

/// <summary>
/// Middleware for inspecting messages in transit.
/// Captures and formats message content for debugging purposes.
/// </summary>
public class MessageDebuggerMiddleware : PipelineInfoDisplayMiddlewareBase
{
    internal const string SampleMetadataKey = "DataChannel.DebugSample";
    /// <summary>
    /// Represents the legacy live debug message view. Raw payload and nested metadata values may be mutable.
    /// </summary>
    public class DebugMessage
    {
        public DateTime Timestamp { get; set; }
        public ChannelSide Source { get; set; }
        public string FormattedContent { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public object? RawData { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Indicates whether debugging is enabled.
    /// </summary>
    private bool _isActive;

    /// <summary>
    /// Keyword used to filter captured messages.
    /// </summary>
    private string _filterKeyword = string.Empty;

    /// <summary>
    /// Maximum number of messages retained in the queue.
    /// </summary>
    private int _maxQueueSize = 100;

    /// <summary>
    /// Queue that stores captured debug messages.
    /// </summary>
    private readonly Queue<(DebugMessage Live, DebugMessageSnapshot Snapshot)> _debugMessages = new();
    private long _messageSequence;

    /// <summary>
    /// Synchronization lock for queue operations.
    /// </summary>
    private readonly object _queueLock = new();

    /// <summary>
    /// Gets or sets a value indicating whether debugging is active.
    /// </summary>
    public bool IsActive
    {
        get { lock (_queueLock) return _isActive; }
        set
        {
            lock (_queueLock)
            {
                _isActive = value;
                SetInfo("Debug status", value ? "Active" : "Inactive");
                SetInfo("Status updated at", DateTime.Now);
            }
        }
    }

    /// <summary>
    /// Gets or sets the filter keyword.
    /// </summary>
    public string FilterKeyword
    {
        get { lock (_queueLock) return _filterKeyword; }
        set
        {
            lock (_queueLock)
            {
                _filterKeyword = value ?? string.Empty;
                SetInfo("Current filter keyword", _filterKeyword);
            }
        }
    }

    /// <summary>
    /// Gets or sets the maximum queue size.
    /// </summary>
    public int MaxQueueSize
    {
        get { lock (_queueLock) return _maxQueueSize; }
        set
        {
            lock (_queueLock)
            {
                _maxQueueSize = Math.Max(1, value);
                SetInfo("Queue capacity", _maxQueueSize);
                TrimQueue();
                SetInfo("Captured message count", _debugMessages.Count);
            }
        }
    }

    /// <summary>
    /// Gets legacy queue-held message references. Raw payload and nested metadata values remain live.
    /// Use <see cref="GetDebugMessageSnapshots"/> for frozen evidence or retained selections.
    /// </summary>
    public List<DebugMessage> GetDebugMessages()
    {
        lock (_queueLock)
        {
            return _debugMessages.Select(message => message.Live).ToList();
        }
    }

    /// <summary>
    /// Gets immutable message evidence frozen at capture time, in queue order. Previously returned snapshots
    /// remain valid after trimming or clearing. Closing an inspector does not change this host-owned capture state.
    /// </summary>
    /// <returns>A detached read-only list with complete payload and metadata evidence.</returns>
    public IReadOnlyList<DebugMessageSnapshot> GetDebugMessageSnapshots()
    {
        lock (_queueLock) return Array.AsReadOnly(_debugMessages.Select(message => message.Snapshot).ToArray());
    }

    /// <summary>
    /// Clears all captured debug messages.
    /// </summary>
    public void ClearDebugMessages()
    {
        lock (_queueLock)
        {
            _debugMessages.Clear();
            SetInfo("Messages cleared at", DateTime.Now);
            SetInfo("Captured message count", 0);
        }
    }

    /// <summary>
    /// Processes the data context synchronously.
    /// </summary>
    public override ChannelDataContext Pass(ChannelDataContext context)
    {
        TryCapture(context);
        return context;
    }

    // The review command exercises the same filter, formatter and queue without dispatching a message.
    internal bool CaptureSample()
    {
        var sample = new ChannelDataContext(ChannelSide.Inner, new DebugCaptureSample(true, FilterKeyword));
        ((IDictionary<string, object?>)sample.Metadata)[SampleMetadataKey] = true;
        return TryCapture(sample);
    }

    private sealed record DebugCaptureSample(bool Sample, string Keyword);

    private bool TryCapture(ChannelDataContext context)
    {
        if (!IsActive) return false;
        try
        {
            var formattedContent = FormatMessage(context.Data);
            
            if (ShouldCapture(formattedContent))
            {
                var debugMessage = new DebugMessage
                {
                    Timestamp = DateTime.UtcNow,
                    Source = context.Source,
                    FormattedContent = formattedContent,
                    MessageType = context.Data?.GetType().GetCleanFullName() ?? "null",
                    RawData = context.Data,
                    Metadata = context.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value ?? "")
                };

                var metadata = new ReadOnlyDictionary<string, DataChannelValueSnapshot>(context.Metadata
                    .ToDictionary(entry => entry.Key, entry => DataChannelValueSnapshot.Capture(entry.Value), StringComparer.Ordinal));
                return AddDebugMessage(debugMessage, DataChannelValueSnapshot.Capture(context.Data), metadata);
            }
        }
        catch (Exception ex)
        {
            CollectException(ex, this, "Message debugging failed.");
        }

        return false;
    }

    /// <summary>
    /// Processes the data context asynchronously.
    /// </summary>
    public override async Task<ChannelDataContext> PassAsync(ChannelDataContext context)
    {
        return await Task.FromResult(Pass(context));
    }

    /// <summary>
    /// Formats the message content.
    /// Derived types can override this to provide custom formatting logic.
    /// </summary>
    /// <param name="data">The raw payload.</param>
    /// <returns>The formatted string representation.</returns>
    protected virtual string FormatMessage(object? data)
    {
        if (data == null)
        {
            return "null";
        }

        try
        {
            return data.ToJsonStringForce()!;
        }
        catch(Exception e)
        {
            return $"Failed to format {data.GetCleanFullName()}: {e.GetMessageRecursively()}";
        }
    }

    /// <summary>
    /// Determines whether the current message should be captured.
    /// Derived types can override this to implement custom matching logic.
    /// </summary>
    /// <param name="formattedContent">The formatted message content.</param>
    /// <returns><see langword="true"/> if the message should be captured; otherwise, <see langword="false"/>.</returns>
    protected virtual bool ShouldCapture(string formattedContent)
    {
        if (string.IsNullOrWhiteSpace(FilterKeyword))
        {
            return true;
        }

        return formattedContent.Contains(FilterKeyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds a debug message to the queue.
    /// </summary>
    private bool AddDebugMessage(DebugMessage message, DataChannelValueSnapshot payload,
        IReadOnlyDictionary<string, DataChannelValueSnapshot> metadata)
    {
        lock (_queueLock)
        {
            if (!_isActive) return false;
            var snapshot = new DebugMessageSnapshot(++_messageSequence, message.Timestamp, message.Source,
                message.FormattedContent, message.MessageType, payload, metadata);
            _debugMessages.Enqueue((message, snapshot));
            TrimQueue();
            
            var count = _debugMessages.Count;
            SetInfo("Captured message count", count);
            SetInfo("Last captured at", message.Timestamp);
            return true;
        }
    }

    /// <summary>
    /// Trims the queue so it stays within the configured size limit.
    /// </summary>
    private void TrimQueue()
    {
        while (_debugMessages.Count > _maxQueueSize)
        {
            _debugMessages.Dequeue();
        }
    }

    /// <summary>
    /// Initializes middleware state and statistics.
    /// </summary>
    public void Initialize()
    {
        SetInfo("Middleware name", "Message debugger");
        SetInfo("Debug status", "Inactive");
        SetInfo("Queue capacity", _maxQueueSize);
        SetInfo("Current filter keyword", "");
        SetInfo("Captured message count", 0);
        SetInfo("Initialized at", DateTime.Now);
    }
}
