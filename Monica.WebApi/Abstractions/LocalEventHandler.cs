using Monica.EventBus.Abstractions.Handlers;

namespace Monica.WebApi.Abstractions;

/// <summary>
/// Base class for local event handlers.
/// </summary>
/// <remarks>
/// Monica matches local handlers by exact event type and topic. Do not use a base event
/// type as a catch-all listener for derived events. Resolve handlers through Monica dependency injection.
/// </remarks>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public abstract class LocalEventHandler<TEvent> :
    EventHandlerBase,
    ILocalEventHandler<TEvent>
{
    /// <summary>
    /// Handles the local event.
    /// </summary>
    /// <param name="eventData">The event payload.</param>
    /// <param name="cancellationToken">Signals that the publisher is no longer waiting.</param>
    public abstract Task HandleEventAsync(TEvent eventData, CancellationToken cancellationToken);
}
