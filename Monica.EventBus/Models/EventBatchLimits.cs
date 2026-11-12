using System.Text;

namespace Monica.EventBus.Models;

/// <summary>
/// Immutable send-group limits: 100 messages and 1 MiB of captured body plus UTF-8 metadata by default.
/// This byte budget excludes provider framing and is not a replacement for broker message/request limits.
/// An oversized individual message uses the ordinary single-send path and its provider's size policy.
/// </summary>
public sealed class EventBatchLimits
{
    /// <summary>Creates positive count and byte limits for one native send group.</summary>
    public EventBatchLimits(int maxMessages = 100, long maxBytes = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        MaxMessages = maxMessages;
        MaxBytes = maxBytes;
    }

    /// <summary>Maximum messages in a native send group.</summary>
    public int MaxMessages { get; }

    /// <summary>Maximum captured bytes in a native send group, excluding provider framing.</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// Partitions consecutive inputs by route, count and captured bytes, preserving input order.
    /// Each returned count describes the next slice; oversized messages form single-message slices.
    /// </summary>
    public IEnumerable<int> GetBatchSizes(IReadOnlyList<EventMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var start = 0;
        while (start < messages.Count)
        {
            var first = messages[start];
            var bytes = GetSize(first);
            var count = 1;
            while (start + count < messages.Count && count < MaxMessages)
            {
                var next = messages[start + count];
                var nextBytes = GetSize(next);
                if (!SameRoute(first, next) || nextBytes > MaxBytes - bytes) break;
                bytes += nextBytes;
                count++;
            }
            yield return count;
            start += count;
        }
    }

    /// <summary>Rejects a group that exceeds these limits or mixes routes, before transport I/O begins.</summary>
    public void Validate(IReadOnlyList<EventMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count > MaxMessages)
            throw new ArgumentException("The send group exceeds the transport's message-count limit.", nameof(messages));
        long bytes = 0;
        foreach (var message in messages)
        {
            if (!SameRoute(messages[0], message))
                throw new ArgumentException("A send group must have one scope, service key and topic.", nameof(messages));
            bytes += GetSize(message);
            if (bytes > MaxBytes)
                throw new ArgumentException("The send group exceeds the transport's captured-byte limit; use a single send for an oversized message.", nameof(messages));
        }
    }

    private static bool SameRoute(EventMessage left, EventMessage right) => left.Scope == right.Scope
        && left.Metadata.ServiceKey == right.Metadata.ServiceKey && left.Metadata.TopicName == right.Metadata.TopicName;

    private static long GetSize(EventMessage message)
    {
        var metadata = message.Metadata;
        return (long)message.Body.Length + Size(metadata.MessageId) + Size(metadata.Source) + Size(metadata.EventName)
            + Size(metadata.TopicName) + Size(metadata.ServiceKey) + Size(metadata.TransportKey)
            + Size(metadata.TraceParent) + Size(metadata.TraceState);
    }

    private static int Size(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}
