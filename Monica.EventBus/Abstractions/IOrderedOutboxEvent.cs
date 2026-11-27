namespace Monica.EventBus.Abstractions;

/// <summary>Opts a distributed Outbox contract into ordered publication for each stream.</summary>
/// <remarks>Commit positions in increasing order within a stream. The dispatcher can only observe committed
/// predecessors. Use a stable stream identity within the event contract and a new identity when numbering restarts.</remarks>
public interface IOrderedOutboxEvent
{
    /// <summary>Returns the stream identity and positive business position to capture with this publication.</summary>
    OutboxOrder GetOutboxOrder();
}

/// <summary>Identifies a durable event stream and its application-assigned position.</summary>
public readonly record struct OutboxOrder(Guid StreamId, long Position);
