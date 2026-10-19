using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>
/// Represents a single subscription to an event.
/// Subscriptions are first-class, queryable entities with lifecycle management.
/// </summary>
public interface IEventSubscription : IAsyncDisposable
{
    #region Identity

    /// <summary>
    /// Unique subscription identifier.
    /// </summary>
    EventSubscriptionId Id { get; }

    /// <summary>
    /// Service key for keyed EventBus instances. Null for default EventBus.
    /// </summary>
    string? ServiceKey { get; }

    /// <summary>Gets the stable logical consumer owner, or null for an unowned subscription.</summary>
    string? WorkloadOwner { get; }

    /// <summary>
    /// Event type this subscription listens to.
    /// </summary>
    Type EventType { get; }

    /// <summary>
    /// Topic name this subscription is bound to (can be customized).
    /// </summary>
    string TopicName { get; }

    #endregion

    #region Handler Information

    /// <summary>
    /// Handler type (if using type-based handler).
    /// </summary>
    Type? HandlerType { get; }

    /// <summary>
    /// Handler factory for creating/managing handler instances.
    /// </summary>
    IEventHandlerFactory HandlerFactory { get; }

    #endregion

    #region Scope Information

    /// <summary>
    /// Whether this subscription is for local or distributed events.
    /// </summary>
    EventSubscriptionScope Scope { get; }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Current state of the subscription.
    /// </summary>
    EventSubscriptionState State { get; }

    /// <summary>
    /// When the subscription was created (UTC).
    /// </summary>
    DateTime CreatedAt { get; }

    /// <summary>
    /// When the subscription was last activated (UTC).
    /// </summary>
    DateTime? ActivatedAt { get; }

    /// <summary>
    /// When the subscription was last deactivated (UTC).
    /// </summary>
    DateTime? DeactivatedAt { get; }

    /// <summary>
    /// Whether this subscription was auto-discovered during startup.
    /// </summary>
    bool IsAutoDiscovered { get; }

    #endregion

    #region Metadata

    /// <summary>
    /// Custom metadata associated with this subscription.
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }

    /// <summary>
    /// Gets a metadata value.
    /// </summary>
    T? GetMetadata<T>(string key);

    #endregion

    #region State Transitions

    /// <summary>
    /// Activates this subscription (if pending or inactive).
    /// </summary>
    Task ActivateAsync();

    /// <summary>
    /// Deactivates this subscription without disposing (if active).
    /// </summary>
    Task DeactivateAsync();

    /// <summary>
    /// Reactivates this subscription (if deactivated).
    /// </summary>
    Task ReactivateAsync();

    #endregion
}
