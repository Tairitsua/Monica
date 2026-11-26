namespace Monica.EventBus.Models;

/// <summary>
/// Identifies the EventBus subscription responsible for one handler execution.
/// </summary>
/// <param name="SubscriptionId">The host-local subscription identifier.</param>
/// <param name="TopicName">The resolved topic name.</param>
/// <param name="Scope">Whether delivery is local or distributed.</param>
/// <param name="ServiceKey">The optional keyed EventBus identity.</param>
/// <param name="IsAutoDiscovered">Whether Monica discovered the subscription automatically.</param>
public sealed record EventHandlerExecutionFeature(
    EventSubscriptionId SubscriptionId,
    string TopicName,
    EventSubscriptionScope Scope,
    string? ServiceKey,
    bool IsAutoDiscovered)
{
    /// <summary>Gets the receiving host run identifier when each instance owns an independent delivery.</summary>
    public Guid? ReceiverInstanceId { get; init; }
}
