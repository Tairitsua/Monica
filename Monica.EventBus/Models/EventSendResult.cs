namespace Monica.EventBus.Models;

/// <summary>One prepared message's transport outcome; only Accepted permits Outbox acknowledgment.</summary>
public enum EventSendStatus
{
    /// <summary>The transport acknowledged the message.</summary>
    Accepted,
    /// <summary>The transport reported a failed send. Retrying can still require receiver deduplication.</summary>
    Failed,
    /// <summary>No definitive acknowledgment is available. The message may already have been accepted.</summary>
    Unknown
}

/// <summary>Immutable outcome aligned with one input of <see cref="Abstractions.IEventBatchTransport"/>.</summary>
public sealed class EventSendResult
{
    private EventSendResult(EventSendStatus status, Exception? error) => (Status, Error) = (status, error);

    /// <summary>Gets the delivery classification.</summary>
    public EventSendStatus Status { get; }

    /// <summary>Gets the original failure, or null for an accepted message.</summary>
    public Exception? Error { get; }

    /// <summary>A transport-acknowledged message.</summary>
    public static EventSendResult Accepted { get; } = new(EventSendStatus.Accepted, null);

    /// <summary>Creates a failed-send result, retaining the provider's original error.</summary>
    public static EventSendResult Failed(Exception error) => new(EventSendStatus.Failed,
        error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Creates an ambiguous-send result, retaining the provider's original error.</summary>
    public static EventSendResult Unknown(Exception error) => new(EventSendStatus.Unknown,
        error ?? throw new ArgumentNullException(nameof(error)));
}
