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
    /// <inheritdoc />
    public Task DispatchBatchAsync(IReadOnlyList<EventMessage> messages, EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
        => DispatchBatchCoreAsync(messages, subscriptionId, allowHeld: false, cancellationToken);

    /// <inheritdoc />
    public Task DispatchTestBatchAsync(IReadOnlyList<EventMessage> messages, EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
        => DispatchBatchCoreAsync(messages, subscriptionId, allowHeld: true, cancellationToken);

    private async Task DispatchBatchCoreAsync(IReadOnlyList<EventMessage> messages, EventSubscriptionId id,
        bool allowHeld, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0) throw new ArgumentException("A receive batch cannot be empty.", nameof(messages));
        var subscription = registry.GetById(id)
            ?? throw new InvalidOperationException($"Event subscription '{id}' no longer exists.");
        if (!subscription.IsBatch || subscription.Scope != EventSubscriptionScope.Distributed
            || (subscription.State != EventSubscriptionState.Active
                && !(allowHeld && subscription.State == EventSubscriptionState.Held)))
            throw new InvalidOperationException($"Event subscription '{id}' is not eligible for batch delivery.");
        var inputs = messages.ToArray();
        foreach (var message in inputs)
            if (message.Scope != subscription.Scope || message.Metadata.TopicName != subscription.TopicName
                || message.Metadata.ServiceKey != subscription.ServiceKey)
                throw new ArgumentException("A receive batch must match the selected subscription's scope, topic and bus.", nameof(messages));
        var payloads = inputs.Select(message => Deserialize(message, subscription.EventType)).ToArray();
        await EventHandlerDispatcher.DispatchBatchAsync(subscription, invoker, payloads,
            inputs.Select(message => message.Metadata).ToArray(), cancellationToken, allowHeld).ConfigureAwait(false);
    }

    public Task DispatchAsync(EventMessage message, CancellationToken cancellationToken)
        => DispatchCoreAsync(message, filterWorkloadOwner: false, workloadOwner: null, cancellationToken);

    /// <inheritdoc />
    public Task DispatchAsync(EventMessage message, string? workloadOwner, CancellationToken cancellationToken)
        => DispatchCoreAsync(message, filterWorkloadOwner: true, workloadOwner, cancellationToken);

    /// <inheritdoc />
    public Task DispatchTestAsync(EventMessage message,
        IReadOnlyCollection<EventSubscriptionId> subscriptionIds, CancellationToken cancellationToken = default)
        => DispatchIdsAsync(message, subscriptionIds, allowHeld: true, cancellationToken);

    /// <inheritdoc />
    public Task DispatchSelectedAsync(EventMessage message,
        IReadOnlyCollection<EventSubscriptionId> subscriptionIds, CancellationToken cancellationToken = default)
        => DispatchIdsAsync(message, subscriptionIds, allowHeld: false, cancellationToken);

    private async Task DispatchIdsAsync(EventMessage message,
        IReadOnlyCollection<EventSubscriptionId> subscriptionIds, bool allowHeld, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(subscriptionIds);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = subscriptionIds.ToArray();
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Select a nonempty set of distinct local subscriptions.", nameof(subscriptionIds));
        if (string.IsNullOrWhiteSpace(message.Metadata.MessageId) || string.IsNullOrWhiteSpace(message.Metadata.Source))
            throw new ArgumentException("Selected delivery requires a stable message ID and producer source.", nameof(message));
        var subscriptions = ids.Select(id => registry.GetById(id)
            ?? throw new InvalidOperationException($"Event subscription '{id}' no longer exists.")).ToArray();
        foreach (var subscription in subscriptions)
        {
            if ((subscription.State != EventSubscriptionState.Active
                    && !(allowHeld && subscription.State == EventSubscriptionState.Held))
                || subscription.Scope != message.Scope
                || subscription.TopicName != message.Metadata.TopicName
                || subscription.ServiceKey != message.Metadata.ServiceKey)
                throw new InvalidOperationException($"Event subscription '{subscription.Id}' is not eligible for this selected delivery.");
        }
        await DispatchSelectedAsync(message, subscriptions, cancellationToken, allowHeld).ConfigureAwait(false);
    }

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
        await DispatchSelectedAsync(message, subscriptions, cancellationToken, allowHeld: false).ConfigureAwait(false);
    }

    private async Task DispatchSelectedAsync(EventMessage message, IEventSubscription[] subscriptions,
        CancellationToken cancellationToken, bool allowHeld)
    {
        var metadata = message.Metadata;
        var eventTypes = subscriptions.Select(subscription => subscription.EventType).Distinct().ToArray();
        if (eventTypes.Length == 0)
        {
            throw new InvalidOperationException(
                $"No eligible {message.Scope} event subscription exists for '{metadata.TopicName}'.");
        }
        if (eventTypes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Topic '{metadata.TopicName}' resolves to multiple event contract types.");
        }
        var eventType = eventTypes[0];
        var eventData = Deserialize(message, eventType);
        await EventHandlerDispatcher.DispatchSubscriptionsAsync(
            subscriptions, invoker, logger, eventType, eventData, metadata, cancellationToken, allowHeld);
    }

    private object Deserialize(EventMessage message, Type eventType)
    {
        var metadata = message.Metadata;
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
        return eventData;
    }
}
