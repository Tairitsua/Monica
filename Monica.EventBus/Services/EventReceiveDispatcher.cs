using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;

namespace Monica.EventBus.Services;

/// <summary>Default receive dispatcher backed by the host subscription registry.</summary>
public sealed class EventReceiveDispatcher(
    IEventSubscriptionRegistry registry,
    IEventHandlerInvoker invoker,
    IJsonSerializerOptionsProvider jsonOptions,
    ILogger<EventReceiveDispatcher> logger) : IEventReceiveDispatcher
{
    public Task DispatchAsync(EventMessage message, CancellationToken cancellationToken)
        => DispatchCoreAsync(message, filterWorkloadOwner: false, workloadOwner: null, cancellationToken);

    /// <inheritdoc />
    public Task DispatchAsync(EventMessage message, string? workloadOwner, CancellationToken cancellationToken)
        => DispatchCoreAsync(message, filterWorkloadOwner: true, workloadOwner, cancellationToken);

    private async Task DispatchCoreAsync(
        EventMessage message, bool filterWorkloadOwner, string? workloadOwner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = message.Metadata;
        var subscriptions = registry.GetAll()
            .Where(subscription => subscription.TopicName == metadata.TopicName &&
                                   subscription.ServiceKey == metadata.ServiceKey &&
                                   subscription.Scope == message.Scope &&
                                   subscription.State == EventSubscriptionState.Active &&
                                   (!filterWorkloadOwner || subscription.WorkloadOwner == workloadOwner))
            .ToArray();
        var eventTypes = subscriptions.Select(subscription => subscription.EventType).Distinct().ToArray();
        if (eventTypes.Length == 0)
        {
            throw new InvalidOperationException(
                $"No active {message.Scope} event subscription exists for '{metadata.TopicName}'" +
                (filterWorkloadOwner ? $" and workload owner '{workloadOwner ?? "<unowned>"}'." : "."));
        }
        if (eventTypes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Topic '{metadata.TopicName}' resolves to multiple event contract types.");
        }
        var eventType = eventTypes[0];
        var declaredName = EventNameAttribute.GetNameOrDefault(eventType);
        if (!string.Equals(declaredName, metadata.EventName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Received event name '{metadata.EventName}' does not match contract '{declaredName}'.");
        }
        object eventData;
        try
        {
            eventData = JsonSerializer.Deserialize(message.Body.Span, eventType, jsonOptions.SerializerOptions)
                ?? throw new JsonException($"Event '{metadata.EventName}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new EventMessageDeserializationException(
                $"Event '{metadata.EventName}' could not be deserialized.", ex);
        }
        await EventHandlerDispatcher.DispatchSubscriptionsAsync(
            subscriptions, invoker, logger, eventType, eventData, metadata, cancellationToken);
    }
}
