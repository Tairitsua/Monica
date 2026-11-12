using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services;

internal sealed class EventBatchDeliveryContext : IEventBatchDeliveryContext
{
    internal EventBatchExecutionFeature? Feature { get; set; }

    public IReadOnlyList<EventDeliveryMetadata> Current => Feature?.Metadata ?? [];
}
