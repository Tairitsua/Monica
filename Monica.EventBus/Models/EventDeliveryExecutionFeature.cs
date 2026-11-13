namespace Monica.EventBus.Models;

/// <summary>Preserves the incoming identity when an application reruns an event operation in a new scope.</summary>
/// <remarks>
/// Supply the original transport metadata to the new execution pipeline. Inbox uses this identity
/// in preference to the invocation scope's delivery context. This feature does not initialize
/// <see cref="Abstractions.IEventDeliveryContext"/>, acknowledge delivery, or implement retries.
/// Only retry operations known to have rolled back, and resolve all collaborators from the new scope.
/// </remarks>
/// <param name="Metadata">The unchanged metadata captured from the incoming delivery.</param>
public sealed record EventDeliveryExecutionFeature(EventDeliveryMetadata Metadata);
