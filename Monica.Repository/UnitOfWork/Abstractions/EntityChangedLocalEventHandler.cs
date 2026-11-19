using Monica.EventBus.Abstractions.Handlers;
using Monica.Repository.UnitOfWork.Models;

namespace Monica.Repository.UnitOfWork.Abstractions;

/// <summary>
/// Base class for local handlers that react to any tracked change for one entity type.
/// </summary>
/// <typeparam name="TEntity">The entity type carried by the entity change event.</typeparam>
/// <remarks>
/// Monica dispatches events by the exact published event type. This base class registers
/// create, update, and delete handlers explicitly while exposing one shared change callback.
/// Prefer this class over subscribing directly to <see cref="EntityChangedEventData{TEntity}"/>,
/// because parent event types are not catch-all listeners for derived event types.
/// </remarks>
public abstract class EntityChangedLocalEventHandler<TEntity> :
    EventHandlerBase,
    ILocalEventHandler<EntityCreatedEventData<TEntity>>,
    ILocalEventHandler<EntityUpdatedEventData<TEntity>>,
    ILocalEventHandler<EntityDeletedEventData<TEntity>>
{
    /// <summary>
    /// Handles an entity creation event.
    /// </summary>
    /// <param name="eventData">The entity creation event payload.</param>
    /// <param name="cancellationToken">Signals that the publisher is no longer waiting.</param>
    public virtual Task HandleEventAsync(
        EntityCreatedEventData<TEntity> eventData,
        CancellationToken cancellationToken)
    {
        return HandleChangedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Handles an entity update event.
    /// </summary>
    /// <param name="eventData">The entity update event payload.</param>
    /// <param name="cancellationToken">Signals that the publisher is no longer waiting.</param>
    public virtual Task HandleEventAsync(
        EntityUpdatedEventData<TEntity> eventData,
        CancellationToken cancellationToken)
    {
        return HandleChangedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Handles an entity deletion event.
    /// </summary>
    /// <param name="eventData">The entity deletion event payload.</param>
    /// <param name="cancellationToken">Signals that the publisher is no longer waiting.</param>
    public virtual Task HandleEventAsync(
        EntityDeletedEventData<TEntity> eventData,
        CancellationToken cancellationToken)
    {
        return HandleChangedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Handles any create, update, or delete event for the configured entity type.
    /// </summary>
    /// <param name="eventData">The normalized entity change event payload.</param>
    /// <param name="cancellationToken">Signals that the publisher is no longer waiting.</param>
    protected abstract Task HandleChangedAsync(
        EntityChangedEventData<TEntity> eventData,
        CancellationToken cancellationToken);
}
