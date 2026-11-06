using Monica.EventBus.Abstractions.Handlers;

namespace Monica.WebApi.Abstractions;

/// <summary>Provides host-bound application services to an explicit distributed batch consumer.</summary>
/// <remarks>
/// Resolve through Monica dependency injection. Keep the DistributedEventHandler naming prefix and declare the
/// owning role, Inbox identity and transaction context as for ordinary distributed handlers. Native Kafka
/// supplies bounded groups; use a single-event handler when the provider cannot receive batches.
/// </remarks>
/// <typeparam name="TEvent">The exact event payload contract.</typeparam>
public abstract class DistributedBatchEventHandler<TEvent> : EventHandlerBase, IDistributedBatchEventHandler<TEvent>
    where TEvent : class
{
    /// <summary>Processes the ordered payloads selected by the receive policies in one execution transaction.</summary>
    /// <param name="events">The ordered event payloads admitted to the current batch execution.</param>
    /// <param name="cancellationToken">Signals cancellation of the batch delivery.</param>
    public abstract Task HandleBatchAsync(IReadOnlyList<TEvent> events, CancellationToken cancellationToken);
}
