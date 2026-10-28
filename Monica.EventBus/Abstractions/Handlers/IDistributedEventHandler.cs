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
    /// Throw <see cref="Monica.Core.ExceptionHandling.Exceptions.BusinessException"/> for a permanent business
    /// rejection. Built-in providers log and discard that delivery, or forward it to a configured dead-letter topic,
    /// after the handler's transaction and scope have unwound. Other exceptions remain retryable. A rejection ends
    /// the current transport delivery, so later handlers for the same topic are not invoked. Handle and persist an
    /// expected business outcome inside the handler when it must commit an alarm, rejected record, or Inbox receipt.
    /// </remarks>
    /// <param name="eventData">Event payload.</param>
    /// <param name="cancellationToken">
    /// Signals that the message delivery is no longer waiting for this handler. Implementations should pass it
    /// to cancellable operations and stop promptly, but Monica cannot forcibly terminate non-cooperative code.
    /// </param>
    Task HandleEventAsync(TEvent eventData, CancellationToken cancellationToken);
}
