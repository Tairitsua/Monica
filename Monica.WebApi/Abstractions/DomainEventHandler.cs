using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.Application.Abstractions;

namespace Monica.WebApi.Abstractions;

/// <summary>
/// Provides host-bound logging and mapping services to Monica event handlers.
/// </summary>
/// <remarks>
/// Event handlers must be resolved through Monica dependency injection so their host service provider is assigned.
/// </remarks>
public abstract class EventHandlerBase : ServiceBase, ITransientDependency;


/// <summary>
/// Base class for distributed domain event handlers.
/// </summary>
/// <remarks>
/// Monica matches distributed handlers by exact event type and topic. Do not use a base
/// event type as a catch-all listener for derived events.
/// </remarks>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public abstract class DomainEventHandler<TEvent> :
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

/// <summary>
/// Base class for local event handlers.
/// </summary>
/// <remarks>
/// Monica matches local handlers by exact event type and topic. Do not use a base event
/// type as a catch-all listener for derived events.
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
