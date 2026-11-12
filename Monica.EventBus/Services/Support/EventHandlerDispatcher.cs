using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services.Support;

internal static class EventHandlerDispatcher
{
    internal static async Task DispatchAsync(
        IEventSubscriptionRegistry registry,
        IEventHandlerInvoker invoker,
        ILogger logger,
        Type eventType,
        object eventData,
        EventSubscriptionScope scope,
        string? serviceKey,
        string topicName,
        EventDeliveryMetadata? metadata,
        bool requireSubscription,
        CancellationToken cancellationToken)
    {
        var subscriptions = registry.GetAll()
            .Where(subscription => subscription.EventType == eventType &&
                                   subscription.TopicName == topicName &&
                                   subscription.State == EventSubscriptionState.Active &&
                                   subscription.ServiceKey == serviceKey &&
                                   subscription.Scope == scope)
            .ToArray();
        if (subscriptions.Length == 0 && requireSubscription)
        {
            throw new InvalidOperationException(
                $"No active {scope} event subscription exists for '{topicName}' on bus '{serviceKey ?? "default"}'.");
        }

        await DispatchSubscriptionsAsync(subscriptions, invoker, logger, eventType, eventData, metadata, cancellationToken);
    }

    internal static async Task DispatchSubscriptionsAsync(
        IReadOnlyList<IEventSubscription> subscriptions,
        IEventHandlerInvoker invoker,
        ILogger logger,
        Type eventType,
        object eventData,
        EventDeliveryMetadata? metadata,
        CancellationToken cancellationToken,
        bool allowHeld = false)
    {
        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subscription.State != EventSubscriptionState.Active
                && !(allowHeld && subscription.State == EventSubscriptionState.Held))
            {
                throw new InvalidOperationException(
                    $"Event subscription '{subscription.Id}' stopped before delivery completed.");
            }
            try
            {
                await using var handlerScope = await subscription.HandlerFactory
                    .CreateExecutionScopeAsync().ConfigureAwait(false);
                handlerScope.ServiceProvider.GetService<EventDeliveryContext>()?.Set(subscription.IsBatch ? null : metadata);
                if (subscription.IsBatch)
                {
                    if (metadata is null)
                        throw new InvalidOperationException("Batch handlers require delivery metadata, including for a singleton delivery.");
                    handlerScope.ServiceProvider.GetRequiredService<EventBatchDeliveryContext>().Feature = new([metadata]);
                }
                await invoker.InvokeAsync(
                    handlerScope.EventHandler, subscription.IsBatch ? new[] { eventData } : eventData, eventType, subscription,
                    handlerScope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error invoking handler {HandlerType} for event {EventType}",
                    subscription.HandlerType?.Name ?? "Unknown", eventType.Name);
                throw;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async Task DispatchBatchAsync(IEventSubscription subscription, IEventHandlerInvoker invoker,
        IReadOnlyList<object> payloads, IReadOnlyList<EventDeliveryMetadata> metadata, CancellationToken cancellationToken,
        bool allowHeld)
    {
        if (subscription.State != EventSubscriptionState.Active
            && !(allowHeld && subscription.State == EventSubscriptionState.Held))
            throw new InvalidOperationException($"Batch subscription '{subscription.Id}' stopped before delivery.");
        await using var scope = await subscription.HandlerFactory.CreateExecutionScopeAsync().ConfigureAwait(false);
        scope.ServiceProvider.GetRequiredService<EventDeliveryContext>().Set(null);
        scope.ServiceProvider.GetRequiredService<EventBatchDeliveryContext>().Feature = new(metadata);
        await invoker.InvokeAsync(scope.EventHandler, payloads, subscription.EventType, subscription,
            scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (subscription.State != EventSubscriptionState.Active
            && !(allowHeld && subscription.State == EventSubscriptionState.Held))
            throw new InvalidOperationException($"Batch subscription '{subscription.Id}' stopped before acknowledgment.");
    }
}
