using Monica.EventBus.Abstractions.Handlers;

namespace Monica.EventBus.Models;

/// <summary>
/// Descriptor for creating a new subscription.
/// Contains all necessary information to create and activate a subscription.
/// </summary>
public sealed record EventSubscriptionDescriptor
{
    /// <summary>
    /// Service key for keyed EventBus instances. Null for default EventBus.
    /// </summary>
    public string? ServiceKey { get; init; }

    /// <summary>Gets the stable logical consumer owner, or null for an unowned subscription.</summary>
    /// <remarks>
    /// This identity groups competing replicas independently of their host AppId. It does not replace
    /// a keyed bus identifier or an Inbox handler's durable deduplication identity.
    /// When omitted on a typed distributed subscription, the registry uses the handler's owner annotation
    /// or the host type activation policy. A supplied value must not be empty.
    /// </remarks>
    public string? WorkloadOwner { get; init; }

    /// <summary>
    /// Event type to subscribe to.
    /// </summary>
    public required Type EventType { get; init; }

    /// <summary>
    /// Topic name for routing the event (can be customized).
    /// </summary>
    public required string TopicName { get; init; }

    /// <summary>
    /// Handler factory for creating handler instances.
    /// </summary>
    public required IEventHandlerFactory HandlerFactory { get; init; }

    /// <summary>
    /// EventSubscription scope (Local or Distributed).
    /// </summary>
    public required EventSubscriptionScope Scope { get; init; }

    /// <summary>
    /// Whether this subscription was auto-discovered during startup.
    /// </summary>
    public bool IsAutoDiscovered { get; init; }

    /// <summary>
    /// Custom metadata associated with the subscription.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
