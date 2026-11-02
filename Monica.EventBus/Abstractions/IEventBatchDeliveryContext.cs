using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>Scoped metadata aligned with the payloads passed to a distributed batch handler.</summary>
/// <remarks>
/// Empty outside batch execution. Inbox filtering removes metadata with its payload; the ordinary
/// IEventDeliveryContext.Current remains null because a batch has no representative message identity.
/// Do not retain this scoped context beyond the handler's execution.
/// </remarks>
public interface IEventBatchDeliveryContext
{
    /// <summary>Gets the current ordered, filtered delivery metadata.</summary>
    IReadOnlyList<EventDeliveryMetadata> Current { get; }
}
