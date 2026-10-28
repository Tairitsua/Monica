using Monica.EventBus.Models;

namespace Monica.Repository.Outbox.Models;

/// <summary>A committed event snapshot and its independent, at-least-once delivery state.</summary>
public sealed class OutboxMessage
{
    /// <summary>Database-generated storage key; it has no application ordering semantics.</summary>
    public long Sequence { get; set; }
    /// <summary>Stable identity retained through every delivery attempt.</summary>
    public string MessageId { get; set; } = string.Empty;
    /// <summary>Producer identity retained through every delivery attempt.</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>Stable EventBus event name.</summary>
    public string EventName { get; set; } = string.Empty;
    /// <summary>Resolved delivery topic.</summary>
    public string TopicName { get; set; } = string.Empty;
    /// <summary>Optional keyed transport selection.</summary>
    public string? ServiceKey { get; set; }
    /// <summary>Stable provider partition key captured with the event contract.</summary>
    public string? TransportKey { get; set; }
    /// <summary>Original trace parent, when provided.</summary>
    public string? TraceState { get; set; }
    /// <summary>Original trace state, when provided.</summary>
    public string? TraceParent { get; set; }
    /// <summary>Local or distributed delivery destination.</summary>
    public EventSubscriptionScope Scope { get; set; }
    /// <summary>
    /// Captured shared-store delivery capability: transport identity or complete typed local handler set.
    /// Null identifies an unqualified context-owned/legacy row, which shared workers never claim.
    /// </summary>
    public string? DeliveryTarget { get; set; }
    /// <summary>Immutable UTF-8 JSON payload captured when the application published.</summary>
    public byte[] Body { get; set; } = [];
    /// <summary>UTC capture time.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>UTC acknowledgment time, or null while pending.</summary>
    public DateTime? DeliveredAtUtc { get; set; }
    /// <summary>Current conditional claim token.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC expiration of the current claim.</summary>
    public DateTime? LeaseUntilUtc { get; set; }
    /// <summary>Number of delivery attempts, including interrupted sends.</summary>
    public int Attempts { get; set; }
    /// <summary>Earliest UTC retry time after a failed attempt.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }
    /// <summary>Bounded diagnostic detail from the most recent delivery failure.</summary>
    public string? LastError { get; set; }

    internal EventMessage ToEventMessage() => new(
        new EventDeliveryMetadata(MessageId, Source, EventName, TopicName, ServiceKey, TraceParent, TraceState)
        { TransportKey = TransportKey },
        Scope, Body.ToArray());

    internal static OutboxMessage Capture(EventMessage message, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Metadata.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Metadata.Source);
        return new OutboxMessage
        {
            MessageId = message.Metadata.MessageId,
            Source = message.Metadata.Source,
            EventName = message.Metadata.EventName,
            TopicName = message.Metadata.TopicName,
            ServiceKey = message.Metadata.ServiceKey,
            TransportKey = message.Metadata.TransportKey,
            TraceParent = message.Metadata.TraceParent,
            TraceState = message.Metadata.TraceState,
            Scope = message.Scope,
            Body = message.Body.ToArray(),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
        };
    }
}
