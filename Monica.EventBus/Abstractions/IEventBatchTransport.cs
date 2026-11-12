using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>Optional transport capability for sending a bounded group of prepared, independent messages.</summary>
public interface IEventBatchTransport : IEventTransport
{
    /// <summary>Limits used by ordinary bulk publication and Outbox grouping on this transport.</summary>
    EventBatchLimits BatchLimits { get; }

    /// <summary>
    /// Sends messages for one topic and service key. Returns exactly one result per input, in input order;
    /// an accepted result means transport acknowledgment, never consumer completion or batch atomicity.
    /// </summary>
    /// <remarks>
    /// Validate the complete group before sending. Empty input is a no-op. Cancellation stops admission;
    /// the task must retain ownership until all admitted native sends have finished, even after cancellation.
    /// Report partial failures as results. If the operation throws, callers must treat every unreported outcome
    /// as unknown. Neither retries nor new message identities belong to this operation.
    /// </remarks>
    Task<IReadOnlyList<EventSendResult>> SendBatchAsync(IReadOnlyList<EventMessage> messages,
        CancellationToken cancellationToken);
}
