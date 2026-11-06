using Monica.EventBus.Abstractions.Handlers;

namespace Monica.WebApi.Abstractions;

/// <summary>
/// Provides the application execution boundary for one distributed event delivery.
/// </summary>
/// <remarks>
/// Monica matches distributed handlers by exact event type and topic. Do not use a base
/// event type as a catch-all listener for derived events. Resolve handlers through Monica dependency injection
/// and use the DistributedEventHandler concrete class prefix for both single-event and batch consumers.
/// </remarks>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public abstract class DistributedEventHandler<TEvent> :
    EventHandlerBase,
    IDistributedEventHandler<TEvent>
{
    /// <summary>
    /// Handles the distributed event.
    /// </summary>
    /// <param name="eventData">The event payload.</param>
    /// <param name="cancellationToken">Signals that the event delivery is no longer waiting.</param>
    public abstract Task HandleEventAsync(TEvent eventData, CancellationToken cancellationToken);
}
