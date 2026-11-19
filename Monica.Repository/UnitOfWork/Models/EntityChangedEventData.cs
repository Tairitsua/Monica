
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;

namespace Monica.Repository.UnitOfWork.Models;

/// <summary>
/// Used to pass data for an event when an entity (<see cref="IEntity"/>) is changed (created, updated or deleted).
/// See <see cref="EntityCreatedEventData{TEntity}"/>, <see cref="EntityDeletedEventData{TEntity}"/> and <see cref="EntityUpdatedEventData{TEntity}"/> classes.
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
/// <remarks>
/// This base type is only a shared payload abstraction. Monica dispatches entity change
/// events by the exact concrete event type, so a handler registered for
/// <see cref="EntityChangedEventData{TEntity}"/> will not receive create, update, or delete
/// events. Use <see cref="EntityChangedLocalEventHandler{TEntity}"/> for a single local handler
/// that reacts to all three event types.
/// </remarks>
/// <param name="entity">Changed entity in this event</param>
[Serializable]
public class EntityChangedEventData<TEntity>(TEntity entity)
{
    /// <summary>
    /// Gets the entity instance that changed.
    /// </summary>
    public TEntity Entity { get; } = entity;
}
/// <summary>
/// This type of event can be used to notify just after creation of an Entity.
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
/// <remarks>
/// Constructor.
/// </remarks>
/// <param name="entity">The entity which is created</param>
[Serializable]
public class EntityCreatedEventData<TEntity>(TEntity entity) : EntityChangedEventData<TEntity>(entity)
{
}
/// <summary>
/// This type of event can be used to notify just after update of an Entity.
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
/// <remarks>
/// Constructor.
/// </remarks>
/// <param name="entity">The entity which is updated</param>
[Serializable]
public class EntityUpdatedEventData<TEntity>(TEntity entity) : EntityChangedEventData<TEntity>(entity)
{
}

/// <summary>
/// This type of event can be used to notify just after deletion of an Entity.
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
/// <remarks>
/// Constructor.
/// </remarks>
/// <param name="entity">The entity which is deleted</param>
[Serializable]
public class EntityDeletedEventData<TEntity>(TEntity entity) : EntityChangedEventData<TEntity>(entity)
{
}
