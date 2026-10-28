using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>Dispatches a received transport message through ordinary EventBus handlers.</summary>
public interface IEventReceiveDispatcher
{
    /// <summary>Deserializes a known subscription type and awaits its matching handlers.</summary>
    /// <remarks>An absent active receiver or cancellation fails delivery so the transport cannot acknowledge it.</remarks>
    Task DispatchAsync(EventMessage message, CancellationToken cancellationToken);

    /// <summary>Dispatches only to active handlers belonging to the specified logical consumer owner.</summary>
    /// <param name="message">The received transport message, with its original producer identity.</param>
    /// <param name="workloadOwner">The intended owner; null targets only unowned subscriptions.</param>
    /// <param name="cancellationToken">Cancels dispatch and prevents successful acknowledgment.</param>
    /// <remarks>Other owners in a combined host do not participate in this delivery.</remarks>
    Task DispatchAsync(EventMessage message, string? workloadOwner, CancellationToken cancellationToken);

    /// <summary>Delivers to exactly the selected active subscriptions, failing if any selection is unavailable or incompatible.</summary>
    /// <remarks>
    /// Used after durable destination qualification. IDs are a host-local snapshot, never persisted identities.
    /// Every selected handler must finish before acknowledgment; later removal or deactivation fails delivery.
    /// Custom dispatchers must implement this capability to deliver qualified shared Outbox local messages.
    /// </remarks>
    Task DispatchSelectedAsync(EventMessage message, IReadOnlyCollection<EventSubscriptionId> subscriptionIds,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This dispatcher does not support exact active subscription delivery.");

    /// <summary>Delivers a test message only to the explicitly selected local subscriptions.</summary>
    /// <remarks>
    /// Active and held subscriptions are eligible; disabled, inactive, missing or incompatible selections fail
    /// before any handler runs. This never publishes to a broker, activates subscriptions or acknowledges offsets.
    /// The ordinary scoped invoker, transaction and Inbox pipeline still applies, including real business effects.
    /// Prepare a new message through IEventMessageFactory for a new delivery; reuse its identity for intentional replay.
    /// </remarks>
    Task DispatchTestAsync(EventMessage message, IReadOnlyCollection<EventSubscriptionId> subscriptionIds,
        CancellationToken cancellationToken = default);
}
