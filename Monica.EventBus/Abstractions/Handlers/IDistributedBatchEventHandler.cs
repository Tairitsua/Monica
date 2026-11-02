namespace Monica.EventBus.Abstractions.Handlers;

/// <summary>Explicitly handles a bounded group of independent distributed events in one execution.</summary>
/// <remarks>
/// Each delivery owns a fresh scope and automatic transaction. Implement this contract instead of the single
/// handler contract for the same event. A topic, bus and workload owner may have only one batch subscription.
/// Native Kafka supports collection; other external providers must explicitly support batch receiving.
/// Groups may contain one event and do not preserve publisher batch boundaries. Throwing fails the whole group.
/// </remarks>
/// <typeparam name="TEvent">The exact event payload contract.</typeparam>
public interface IDistributedBatchEventHandler<TEvent> : IEventHandler where TEvent : class
{
    /// <summary>Processes the ordered payloads remaining after receive policies such as Inbox deduplication.</summary>
    /// <remarks>Await all work and honor cancellation. Metadata is available through IEventBatchDeliveryContext.</remarks>
    Task HandleBatchAsync(IReadOnlyList<TEvent> events, CancellationToken cancellationToken);
}
