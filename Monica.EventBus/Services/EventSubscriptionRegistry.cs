using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Models.Internal;

namespace Monica.EventBus.Services;

/// <summary>
/// Manages subscription lifecycle and provides queryable access to all subscriptions.
/// </summary>
public class EventSubscriptionRegistry(
    ILogger<EventSubscriptionRegistry> logger,
    ITypeActivationPolicy? activation = null) : IEventSubscriptionRegistry
{
    private readonly ConcurrentQueue<EventSubscriptionChange> _notificationQueue = new();
    private readonly ConcurrentDictionary<EventSubscriptionId, EventSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<Guid, IObserver<EventSubscriptionChange>> _observers = new();
    private int _isDispatchingNotifications;

    #region EventSubscription CRUD

    public async Task<IEventSubscription> SubscribeAsync(
        EventSubscriptionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        if (descriptor.Scope == EventSubscriptionScope.Distributed && descriptor.WorkloadOwner is null
            && descriptor.HandlerFactory.GetHandlerType() is { } handlerType)
        {
            descriptor = descriptor with
            {
                WorkloadOwner = handlerType.GetCustomAttributes(inherit: true)
                    .OfType<EventSubscriptionOwnerAttribute>().FirstOrDefault()?.WorkloadOwner
                    ?? activation?.GetWorkloadOwner(handlerType)
            };
        }
        if (descriptor.WorkloadOwner is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.WorkloadOwner, nameof(descriptor));
        }

        var subscription = new EventSubscription(descriptor);
 
        if (!_subscriptions.TryAdd(subscription.Id, subscription))
        {
            throw new InvalidOperationException($"EventSubscription {subscription.Id} already exists");
        }

        logger.LogDebug(
            "EventSubscription {EventSubscriptionId} created for {EventType} on topic {Topic}",
            subscription.Id, descriptor.EventType.Name, descriptor.TopicName);

        try
        {
            // Creation and activation form one committed mutation. Notifications are queued while
            // the subscription gate is held and dispatched only after it has been released.
            await subscription.ActivateAsync(
                committed =>
                {
                    EnqueueNotification(EventSubscriptionChangeType.Added, committed);
                    EnqueueNotification(EventSubscriptionChangeType.Activated, committed);
                },
                cancellationToken).ConfigureAwait(false);
            DrainNotifications();
            return subscription;
        }
        catch (Exception activationError)
        {
            try
            {
                await RemoveSubscriptionAsync(
                    subscription.Id,
                    notifyObservers: false,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"EventSubscription {subscription.Id} activation failed and the subscription could not be rolled back.",
                    activationError,
                    rollbackError);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<IEventSubscription>> SubscribeBatchAsync(
        IEnumerable<EventSubscriptionDescriptor> descriptors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var subscriptions = new List<IEventSubscription>();

        try
        {
            foreach (var descriptor in descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subscription = await SubscribeAsync(descriptor, cancellationToken).ConfigureAwait(false);
                subscriptions.Add(subscription);
            }

            return subscriptions;
        }
        catch (Exception subscriptionError)
        {
            var rollbackErrors = await UnsubscribeCreatedSubscriptionsAsync(subscriptions).ConfigureAwait(false);
            if (rollbackErrors.Count != 0)
            {
                throw new AggregateException(
                    "Event subscription batch creation failed and one or more created subscriptions could not be rolled back.",
                    [subscriptionError, .. rollbackErrors]);
            }

            throw;
        }
    }

    public async Task UnsubscribeAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RemoveSubscriptionAsync(
            subscriptionId,
            notifyObservers: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeBatchAsync(
        IEnumerable<EventSubscriptionId> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptionIds);

        var failures = new List<Exception>();
        var cancellationObserved = cancellationToken.IsCancellationRequested;

        foreach (var id in subscriptionIds.ToArray())
        {
            try
            {
                var effectiveToken = cancellationObserved ? CancellationToken.None : cancellationToken;
                await UnsubscribeAsync(id, effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Batch cleanup is best effort. Continue without the cancelled token so the remaining
                // subscriptions are still released, then report cancellation after cleanup completes.
                cancellationObserved = true;
                try
                {
                    await UnsubscribeAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"Failed to unsubscribe EventSubscription {id} after cancellation was requested.",
                        exception));
                }
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Failed to unsubscribe EventSubscription {id}.",
                    exception));
            }
        }

        if (failures.Count != 0)
        {
            if (cancellationObserved)
            {
                failures.Insert(0, new OperationCanceledException(cancellationToken));
            }

            throw new AggregateException(
                "One or more event subscriptions could not be removed.",
                failures);
        }

        if (cancellationObserved)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public async Task UnsubscribeWhereAsync(
        Func<IEventSubscription, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        cancellationToken.ThrowIfCancellationRequested();

        var toRemove = _subscriptions.Values.Where(predicate).Select(s => s.Id).ToList();
        await UnsubscribeBatchAsync(toRemove, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Query Operations

    public IQueryable<IEventSubscription> GetAll()
    {
        return _subscriptions.Values.AsQueryable();
    }

    public IEventSubscription? GetById(EventSubscriptionId subscriptionId)
    {
        _subscriptions.TryGetValue(subscriptionId, out var subscription);
        return subscription;
    }

    public IReadOnlyList<IEventSubscription> GetByServiceKey(string? serviceKey)
    {
        return _subscriptions.Values.Where(s => s.ServiceKey == serviceKey).ToList();
    }

    public IReadOnlyList<IEventSubscription> GetByTopicName(string topicName)
    {
        return _subscriptions.Values.Where(s => s.TopicName == topicName).ToList();
    }

    public IReadOnlyList<IEventSubscription> GetByEventType(Type eventType)
    {
        return _subscriptions.Values
            .Where(s => s.EventType == eventType || s.EventType.IsAssignableFrom(eventType))
            .ToList();
    }

    public IReadOnlyList<IEventSubscription> GetByState(EventSubscriptionState state)
    {
        return _subscriptions.Values.Where(s => s.State == state).ToList();
    }

    public IReadOnlyList<IEventSubscription> GetByScope(EventSubscriptionScope scope)
    {
        return _subscriptions.Values.Where(s => s.Scope == scope).ToList();
    }

    #endregion

    #region Lifecycle Operations

    public async Task ActivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            logger.LogWarning("EventSubscription {EventSubscriptionId} not found for activation", subscriptionId);
            return;
        }  

        await subscription.ActivateAsync(
            committed => EnqueueNotification(EventSubscriptionChangeType.Activated, committed),
            cancellationToken).ConfigureAwait(false);
        DrainNotifications();
    }

    public async Task DeactivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            logger.LogWarning("EventSubscription {EventSubscriptionId} not found for deactivation", subscriptionId);
            return;
        }

        await subscription.DeactivateAsync(
            committed => EnqueueNotification(EventSubscriptionChangeType.Deactivated, committed),
            cancellationToken).ConfigureAwait(false);
        DrainNotifications();
    }

    public async Task ReactivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await ActivateAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Batch Operations

    public async Task UnsubscribeByEventTypeAsync(Type eventType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        cancellationToken.ThrowIfCancellationRequested();

        var subscriptions = GetByEventType(eventType);
        await UnsubscribeBatchAsync(subscriptions.Select(s => s.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeByHandlerTypeAsync(Type handlerType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        cancellationToken.ThrowIfCancellationRequested();

        var subscriptions = _subscriptions.Values
            .Where(s => s.HandlerType == handlerType)
            .Select(s => s.Id)
            .ToList();
        await UnsubscribeBatchAsync(subscriptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeByServiceKeyAsync(string? serviceKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var subscriptions = GetByServiceKey(serviceKey);
        await UnsubscribeBatchAsync(subscriptions.Select(s => s.Id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Exception>> UnsubscribeCreatedSubscriptionsAsync(
        IReadOnlyList<IEventSubscription> subscriptions)
    {
        var rollbackErrors = new List<Exception>();

        for (var index = subscriptions.Count - 1; index >= 0; index--)
        {
            var subscriptionId = subscriptions[index].Id;
            try
            {
                await UnsubscribeAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(new InvalidOperationException(
                    $"Failed to roll back EventSubscription {subscriptionId}.",
                    exception));
            }
        }

        return rollbackErrors;
    }

    private async Task RemoveSubscriptionAsync(
        EventSubscriptionId subscriptionId,
        bool notifyObservers,
        CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            logger.LogWarning("EventSubscription {EventSubscriptionId} not found for unsubscribe", subscriptionId);
            return;
        }

        logger.LogDebug("Unsubscribing {EventSubscriptionId}", subscriptionId);

        // Keep the entry queryable until its resources have been released. The subscription owns
        // the mutation gate, so activation, deactivation, and removal cannot overtake each other.
        await subscription.DisposeAsync(
            committed =>
            {
                if (!_subscriptions.TryRemove(
                        new KeyValuePair<EventSubscriptionId, EventSubscription>(subscriptionId, committed)))
                {
                    return;
                }

                if (notifyObservers)
                {
                    EnqueueNotification(EventSubscriptionChangeType.Removed, committed);
                }
            },
            cancellationToken).ConfigureAwait(false);
        DrainNotifications();
    }

    #endregion


    #region IObservable Implementation

    public IDisposable Subscribe(IObserver<EventSubscriptionChange> observer)
    {
        var observerId = Guid.NewGuid();
        _observers[observerId] = observer;
        return new Unsubscriber(_observers, observerId);
    }

    private void EnqueueNotification(
        EventSubscriptionChangeType changeType,
        IEventSubscription subscription)
    {
        _notificationQueue.Enqueue(new EventSubscriptionChange(
            changeType,
            subscription,
            DateTime.UtcNow));
    }

    private void DrainNotifications()
    {
        if (Interlocked.Exchange(ref _isDispatchingNotifications, 1) != 0)
        {
            return;
        }

        while (true)
        {
            while (_notificationQueue.TryDequeue(out var change))
            {
                NotifyObservers(change);
            }

            Volatile.Write(ref _isDispatchingNotifications, 0);
            if (_notificationQueue.IsEmpty
                || Interlocked.Exchange(ref _isDispatchingNotifications, 1) != 0)
            {
                return;
            }
        }
    }

    private void NotifyObservers(EventSubscriptionChange change)
    {
        foreach (var observer in _observers.Values)
        {
            try
            {
                observer.OnNext(change);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error notifying observer of subscription change");
            }
        }
    }

    private sealed class Unsubscriber(
        ConcurrentDictionary<Guid, IObserver<EventSubscriptionChange>> observers,
        Guid observerId)
        : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, IObserver<EventSubscriptionChange>> _observers = observers;
        private readonly Guid _observerId = observerId;

        public void Dispose()
        {
            _observers.TryRemove(_observerId, out _);
        }
    }

    #endregion
}
