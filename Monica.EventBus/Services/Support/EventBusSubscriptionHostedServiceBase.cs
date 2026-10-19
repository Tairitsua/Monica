using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Modules;

namespace Monica.EventBus.Services.Support;

/// <summary>
/// Abstract base class for subscription hosted services.
/// Listens to EventSubscriptionRegistry changes and manages external subscriptions (e.g., Dapr, RabbitMQ).
/// Each derived class handles a specific ServiceKey and implements the actual subscription management.
/// Providers can isolate physical subscriptions by stable workload owner while sharing the registry and lifecycle.
/// Now includes observable state management for monitoring.
/// Runs as a background service that doesn't block application startup.
/// </summary>
public abstract class EventBusSubscriptionHostedServiceBase(
    IEventSubscriptionRegistry subscriptionManager,
    IEventReceiveDispatcher dispatcher,
    ITopicSubscriptionStatusStore topicStatusStore,
    IObservableInstanceRegistry observableManager,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger logger,
    string? serviceKey)
    : MoBackgroundService(observableManager, hostedServiceOptions, serviceScopeFactory, logger), IObserver<EventSubscriptionChange>
{
    protected readonly IEventSubscriptionRegistry SubscriptionManager = subscriptionManager;
    protected readonly IEventReceiveDispatcher Dispatcher = dispatcher;

    /// <summary>
    /// Store receiving runtime health reports for the topics this service manages.
    /// </summary>
    protected readonly ITopicSubscriptionStatusStore TopicStatusStore = topicStatusStore;

    /// <inheritdoc />
    public override string? ServiceKey { get; } = serviceKey;

    /// <inheritdoc />
    public override string? ServiceGroupId => nameof(ModuleEventBus);

    /// <summary>
    /// The provider kind reported with topic status transitions. Derived services override
    /// this so monitoring can attribute topic health to the concrete provider.
    /// </summary>
    protected virtual EventBusProviderKind ProviderKind => EventBusProviderKind.Unknown;

    /// <summary>Whether each stable workload owner needs an independent external subscription.</summary>
    protected virtual bool GroupSubscriptionsByWorkloadOwner => false;

    /// <summary>Identifies one physical subscription within this bus instance.</summary>
    protected readonly record struct SubscriptionGroup(string TopicName, string? WorkloadOwner);

    /// <summary>
    /// Tracks subscription information per topic.
    /// </summary>
    protected class TopicSubscriptionInfo
    {
        public required Type EventType { get; init; }
        public required HashSet<EventSubscriptionId> SubscriptionIds { get; init; }
        public TopicSubscriptionRuntimeState RuntimeState { get; set; } = TopicSubscriptionRuntimeState.Subscribing;
    }

    /// <summary>
    /// Tracks active topic/owner groups and their associated subscriptions.
    /// </summary>
    private readonly ConcurrentDictionary<SubscriptionGroup, TopicSubscriptionInfo> _topicSubscriptions = new();
    private readonly object _topicLock = new();

    private IDisposable? _subscriptionManagerObserver;

    /// <summary>
    /// Executes the background work for the subscription service.
    /// Initializes subscriptions, subscribes to changes, and keeps running until cancellation.
    /// </summary>
    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        RecordState("Initializing subscription manager observer", HostedServiceState.Starting);

        // Subscribe to EventSubscriptionRegistry changes
        _subscriptionManagerObserver = SubscriptionManager.Subscribe(this);

        // Group existing active subscriptions by topic
        var existingSubscriptions = SubscriptionManager.GetAll().AsEnumerable()
            .Where(ShouldHandleSubscription)
            .Where(s => s.State == EventSubscriptionState.Active)
            .ToList();

        RecordState($"Creating subscriptions for {existingSubscriptions.Count} existing subscriptions across topics", HostedServiceState.Starting);

        // Add subscriptions using the new logic (which handles topic grouping)
        foreach (var subscription in existingSubscriptions)
        {
            await HandleSubscriptionAddedAsync(subscription, stoppingToken);
        }

        Logger.LogInformation(
            "{ServiceName} initialized for ServiceKey '{ServiceKey}' with {SubscriptionCount} subscriptions across {TopicCount} topics",
            ServiceName,
            ServiceKey ?? "default",
            existingSubscriptions.Count,
            _topicSubscriptions.Count);

        RecordState("Background service running, listening for subscription changes", HostedServiceState.Running);

        // Keep the service running until cancellation is requested
        // The observer pattern handles subscription changes asynchronously
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when the service is stopping
            Logger.LogDebug("{ServiceName} background task cancelled", ServiceName);
        }
    }

    /// <summary>
    /// Disposes the registry observer and all external subscriptions before the background operation stops.
    /// </summary>
    protected override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        RecordState("Disposing subscription manager observer", HostedServiceState.Stopping);

        // Unsubscribe from EventSubscriptionRegistry
        Interlocked.Exchange(ref _subscriptionManagerObserver, null)?.Dispose();

        RecordState($"Disposing {_topicSubscriptions.Count} tracked topics", HostedServiceState.Stopping);
        await DisposeExternalSubscriptionsAsync(cancellationToken);

        var removedTopics = _topicSubscriptions.Keys.Select(group => group.TopicName).Distinct().ToArray();
        _topicSubscriptions.Clear();
        foreach (var topicName in removedTopics)
        {
            ReportRemainingTopicState(topicName);
        }

        Logger.LogInformation(
            "{ServiceName} cleanup completed for ServiceKey '{ServiceKey}'",
            ServiceName,
            ServiceKey ?? "default");

        await base.OnStoppingAsync(cancellationToken).ConfigureAwait(false);
    }

    #region IObserver Implementation

    public void OnNext(EventSubscriptionChange change)
    {
        // Filter: only handle subscriptions matching our ServiceKey and criteria
        if (!ShouldHandleSubscription(change.Subscription))
        {
            return;
        }

        try
        {
            switch (change.ChangeType)
            {
                case EventSubscriptionChangeType.Added:
                case EventSubscriptionChangeType.Activated:
                    HandleSubscriptionAddedAsync(change.Subscription, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;

                case EventSubscriptionChangeType.Removed:
                case EventSubscriptionChangeType.Deactivated:
                    HandleSubscriptionRemovedAsync(change.Subscription.Id, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;
            }
        }
        catch (Exception ex)
        {
            RecordState($"Error handling subscription change {change.ChangeType} for {change.Subscription.Id}",
                HostedServiceState.Degraded, ex);

            Logger.LogError(ex,
                "Error handling subscription change {ChangeType} for {EventSubscriptionId}",
                change.ChangeType, change.Subscription.Id);
        }
    }

    private async Task HandleSubscriptionAddedAsync(IEventSubscription subscription, CancellationToken cancellationToken)
    {
        var topicName = subscription.TopicName;
        var group = GetSubscriptionGroup(subscription);
        var eventType = subscription.EventType;
        bool shouldCreateExternal = false;

        try
        {
            lock (_topicLock)
            {
                if (_topicSubscriptions.TryGetValue(group, out var topicInfo))
                {
                    // Topic already exists - validate same event type
                    if (topicInfo.EventType != eventType)
                    {
                        var errorMsg = $"Cannot add subscription {subscription.Id} for topic '{topicName}': " +
                                       $"Topic already has subscriptions with EventType '{topicInfo.EventType.Name}', " +
                                       $"but new subscription uses '{eventType.Name}'. " +
                                       $"Multiple event types per topic are not supported.";
                        RecordState(errorMsg, HostedServiceState.Faulted);
                        throw new InvalidOperationException(errorMsg);
                    }

                    // Add to existing topic's reference set
                    if (!topicInfo.SubscriptionIds.Add(subscription.Id))
                    {
                        Logger.LogWarning(
                            "EventSubscription {EventSubscriptionId} already registered for topic {Topic}",
                            subscription.Id, topicName);
                        return;
                    }

                    Logger.LogDebug(
                        "Added subscription {EventSubscriptionId} to existing topic {Topic} (total: {Count} subscriptions)",
                        subscription.Id, topicName, topicInfo.SubscriptionIds.Count);
                }
                else
                {
                    // New topic - create tracking info
                    var newTopicInfo = new TopicSubscriptionInfo
                    {
                        EventType = eventType,
                        SubscriptionIds = [subscription.Id]
                    };
                    _topicSubscriptions[group] = newTopicInfo;
                    shouldCreateExternal = true;

                    Logger.LogDebug(
                        "Created new topic {Topic} for subscription {EventSubscriptionId}",
                        topicName, subscription.Id);
                }
            }

            // Create external subscription if this is the first subscription for the topic
            if (shouldCreateExternal)
            {
                RecordState($"Creating external subscription for topic {topicName}", HostedServiceState.Running);

                ReportSubscriptionState(
                    group, TopicSubscriptionRuntimeState.Subscribing,
                    $"Creating external subscription for topic {topicName}");

                try
                {
                    await CreateExternalSubscriptionForTopicAsync(group, eventType, cancellationToken);
                }
                catch (Exception ex)
                {
                    ReportSubscriptionState(
                        group, TopicSubscriptionRuntimeState.Failed,
                        $"Failed to create external subscription for topic {topicName}", ex);
                    throw;
                }

                RecordState($"Successfully created external subscription for topic {topicName}", HostedServiceState.Running);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            RecordState($"Error adding subscription for topic {topicName}",
                HostedServiceState.Degraded, ex);
            throw;
        }
    }

    private async Task HandleSubscriptionRemovedAsync(EventSubscriptionId subscriptionId, CancellationToken cancellationToken)
    {
        // Find which topic this subscription belongs to
        SubscriptionGroup? groupToRemove = null;

        try
        {
            lock (_topicLock)
            {
                foreach (var (group, topicInfo) in _topicSubscriptions)
                {
                    if (topicInfo.SubscriptionIds.Remove(subscriptionId))
                    {
                        Logger.LogDebug(
                            "Removed subscription {EventSubscriptionId} from topic {Topic} (remaining: {Count} subscriptions)",
                            subscriptionId, group.TopicName, topicInfo.SubscriptionIds.Count);

                        // If this was the last subscription for the topic, mark for removal
                        if (topicInfo.SubscriptionIds.Count == 0)
                        {
                            groupToRemove = group;
                        }
                        break;
                    }
                }

                if (groupToRemove is { } removedGroup)
                {
                    _topicSubscriptions.TryRemove(removedGroup, out _);
                }
            }

            // Remove external subscription if this was the last subscription for the topic
            if (groupToRemove is { } groupToStop)
            {
                RecordState($"Removing external subscription for topic {groupToStop.TopicName}", HostedServiceState.Running);
                await RemoveExternalSubscriptionForTopicAsync(groupToStop, cancellationToken);
                ReportRemainingTopicState(groupToStop.TopicName);
                RecordState($"Successfully removed external subscription for topic {groupToStop.TopicName}", HostedServiceState.Running);
            }
        }
        catch (Exception ex)
        {
            RecordState($"Error removing subscription {subscriptionId}",
                HostedServiceState.Degraded, ex);
            throw;
        }
    }

    public void OnError(Exception error)
    {
        Logger.LogError(error, "Error in EventSubscriptionChange observable");
    }

    public void OnCompleted()
    {
        Logger.LogInformation("EventSubscriptionChange observable completed");
    }

    #endregion

    /// <summary>
    /// Determines if this hosted service should handle the given subscription.
    /// Override this method to add additional filtering criteria.
    /// </summary>
    protected virtual bool ShouldHandleSubscription(IEventSubscription subscription)
    {
        // Only handle subscriptions matching our ServiceKey and Distributed scope
        return subscription.ServiceKey == ServiceKey &&
               subscription.Scope == EventSubscriptionScope.Distributed;
    }

    private SubscriptionGroup GetSubscriptionGroup(IEventSubscription subscription)
        => new(subscription.TopicName, GroupSubscriptionsByWorkloadOwner ? subscription.WorkloadOwner : null);

    /// <summary>
    /// Reports owner-level evidence while retaining the least healthy active owner state in the topic overview.
    /// Reports from an already removed owner are ignored.
    /// </summary>
    protected void ReportSubscriptionState(
        SubscriptionGroup group,
        TopicSubscriptionRuntimeState state,
        string message,
        Exception? exception = null)
    {
        lock (_topicLock)
        {
            if (!_topicSubscriptions.TryGetValue(group, out var subscription))
            {
                return;
            }

            subscription.RuntimeState = state;
            ReportTopicState(group.TopicName,
                $"Owner '{group.WorkloadOwner ?? "<unowned>"}': {message}", exception);
        }
    }

    private void ReportRemainingTopicState(string topicName)
    {
        lock (_topicLock)
        {
            ReportTopicState(topicName, $"External subscription owners changed for topic {topicName}");
        }
    }

    private void ReportTopicState(string topicName, string message, Exception? exception = null)
    {
        var states = _topicSubscriptions
            .Where(entry => entry.Key.TopicName == topicName)
            .Select(entry => entry.Value.RuntimeState)
            .ToArray();
        var state = states.Length == 0
            ? TopicSubscriptionRuntimeState.Stopped
            : states.MaxBy(StateSeverity);
        TopicStatusStore.ReportState(ServiceKey, topicName, ProviderKind, state,
            $"{message}; {states.Length} active owner group(s).", exception);
    }

    private static int StateSeverity(TopicSubscriptionRuntimeState state) => state switch
    {
        TopicSubscriptionRuntimeState.Failed => 4,
        TopicSubscriptionRuntimeState.Recovering => 3,
        TopicSubscriptionRuntimeState.Subscribing => 2,
        TopicSubscriptionRuntimeState.Healthy => 1,
        _ => 0
    };

    protected virtual Task DisposeExternalSubscriptionsAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recreates all external subscriptions that are tracked in the in-memory registry.
    /// Use this when an external provider reconnects and previously created provider-side subscriptions may be lost.
    /// </summary>
    protected virtual async Task RecreateExternalSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var topics = _topicSubscriptions
            .Select(p => (Group: p.Key, p.Value.EventType))
            .ToList();

        if (topics.Count == 0)
        {
            return;
        }

        RecordState($"Recreating {topics.Count} external subscriptions", HostedServiceState.Running);

        await DisposeExternalSubscriptionsAsync(cancellationToken);

        foreach (var (group, eventType) in topics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReportSubscriptionState(
                group, TopicSubscriptionRuntimeState.Subscribing,
                $"Recreating external subscription for topic {group.TopicName}");

            await CreateExternalSubscriptionForTopicAsync(group, eventType, cancellationToken);
        }

        RecordState($"Successfully recreated {topics.Count} external subscriptions", HostedServiceState.Running);
    }

    /// <summary>
    /// Creates an external subscription for the given topic and event type.
    /// Called when the first subscription for a topic is added.
    /// </summary>
    /// <param name="group">The topic and logical consumer owner to subscribe.</param>
    /// <param name="eventType">The event type for message deserialization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task CreateExternalSubscriptionForTopicAsync(
        SubscriptionGroup group,
        Type eventType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes an external subscription for the given topic.
    /// Called when the last subscription for a topic is removed.
    /// </summary>
    /// <param name="group">The topic and logical consumer owner to remove.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task RemoveExternalSubscriptionForTopicAsync(
        SubscriptionGroup group,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles an external message received from the messaging system.
    /// Triggers all registered handlers for the prepared received message.
    /// </summary>
    /// <param name="message">The received message with its provider metadata.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="workloadOwner">The target owner for providers that group subscriptions by ownership.</param>
    protected async Task HandleExternalMessageAsync(
        EventMessage message,
        CancellationToken cancellationToken,
        string? workloadOwner = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topicName = message.Metadata.TopicName;
        var group = new SubscriptionGroup(topicName, GroupSubscriptionsByWorkloadOwner ? workloadOwner : null);
        // Get topic information
        if (!_topicSubscriptions.ContainsKey(group))
        {
            throw new InvalidOperationException(
                $"Received message for inactive topic '{topicName}' and owner '{workloadOwner ?? "<unowned>"}'.");
        }

        try
        {
            if (GroupSubscriptionsByWorkloadOwner)
            {
                await Dispatcher.DispatchAsync(message, workloadOwner, cancellationToken);
            }
            else
            {
                await Dispatcher.DispatchAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordState($"Error handling external message for topic {topicName}",
                HostedServiceState.Degraded, ex);

            Logger.LogError(ex,
                "Error handling external message for topic {Topic}",
                topicName);
            throw;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Interlocked.Exchange(ref _subscriptionManagerObserver, null)?.Dispose();
        base.Dispose();
    }
}
