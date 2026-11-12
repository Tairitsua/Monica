namespace Monica.EventBus.Abstractions.Handlers;

/// <summary>
/// Handles an event delivered through a Monica distributed event-bus provider.
/// </summary>
/// <typeparam name="TEvent">The handled event payload type.</typeparam>
public interface IDistributedEventHandler<in TEvent> : IEventHandler<TEvent>
{
    /// <summary>
    /// Handles a distributed event.
    /// </summary>
    /// <remarks>
    /// Built-in providers apply <see cref="Monica.Modules.ModuleEventBusOption.MessageRetryCount"/> to every
    /// processing failure, including business exceptions, timeout and independent handler cancellation. Each retry
    /// invokes the receive pipeline again with fresh scopes. Exhaustion logs and ends delivery, using the provider's
    /// configured dead-letter destination when available. Transport shutdown leaves delivery unacknowledged.
    /// Handle and persist an expected business outcome normally when an alarm, rejected record or Inbox receipt
    /// must commit. Earlier handlers can run again on retry, so protect repeatable side effects with idempotency.
    /// </remarks>
    /// <param name="eventData">Event payload.</param>
    /// <param name="cancellationToken">
    /// Signals that the message delivery is no longer waiting for this handler. Implementations should pass it
    /// to cancellable operations and stop promptly, but Monica cannot forcibly terminate non-cooperative code.
    /// </param>
    Task HandleEventAsync(TEvent eventData, CancellationToken cancellationToken);
}
