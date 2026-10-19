using System.Collections.Concurrent;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Dapr.Abstractions;
using Monica.Modules;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;

namespace Monica.Dapr.Services;

/// <summary>
/// Dapr-specific implementation of subscription hosted service.
/// Manages Dapr streaming subscriptions for distributed events.
/// Now includes observable state management for monitoring.
/// Runs as a background service that doesn't block application startup.
/// </summary>
internal class DaprEventBusSubscriptionHostedService(
    DaprPublishSubscribeClient daprClient,
    IEventSubscriptionRegistry subscriptionManager,
    ITopicSubscriptionStatusStore topicStatusStore,
    IHostApplicationLifetime applicationLifetime,
    IEventReceiveDispatcher dispatcher,
    IObservableInstanceRegistry observableManager,
    IDaprSidecarHealthCoordinator healthCoordinator,
    IOptions<ModuleDaprEventBusOption> options,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<DaprTopicSubscription> topicSubscriptionLogger,
    ILogger<DaprEventBusSubscriptionHostedService> logger,
    string? serviceKey = null)
    : EventBusSubscriptionHostedServiceBase(
        subscriptionManager,
        dispatcher,
        topicStatusStore,
        observableManager,
        hostedServiceOptions,
        serviceScopeFactory,
        logger,
        serviceKey)
{
    private readonly ModuleDaprEventBusOption _options = options.Value;
    private readonly DaprDeadLetterTopicPolicy _deadLetterTopicPolicy =
        DaprDeadLetterTopicPolicy.Create(options.Value);
    private readonly DaprSubscriptionRecoveryPolicy _recoveryPolicy =
        DaprSubscriptionRecoveryPolicy.Create(options.Value);

    /// <summary>
    /// Gets the name of this service for identification and monitoring
    /// </summary>
    public override string ServiceName => $"DaprEventBus{(ServiceKey != null ? $"_{ServiceKey}" : "")}";
    public override string? ServiceGroupId => nameof(ModuleEventBus);

    /// <inheritdoc />
    protected override EventBusProviderKind ProviderKind => EventBusProviderKind.Dapr;

    private readonly ConcurrentDictionary<string, DaprTopicSubscription> _daprSubscriptionsByTopic = new();
    private readonly ConcurrentDictionary<string, byte> _recoveringTopics = new();
    private readonly CancellationTokenSource _subscriptionStopping =
        CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
    private readonly object _subscriptionLifecycleLock = new();
    private int _wasHealthy;
    private int _stopping;
    private int _disposed;

    /// <summary>
    /// Override ExecuteBackgroundAsync to wait for Dapr sidecar health before creating subscriptions.
    /// This runs in the background without blocking application startup.
    /// </summary>
    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        RecordState("Waiting for Dapr sidecar to become healthy", HostedServiceState.Starting);

        Logger.LogInformation("Waiting for Dapr sidecar health check...");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for Dapr sidecar to be healthy before subscribing.
            var isHealthy = await healthCoordinator.WaitForHealthyAsync(
                _options.SidecarHealthWaitTimeout,
                stoppingToken);

            if (isHealthy)
            {
                break;
            }

            var message = "Dapr sidecar is not healthy. Subscription creation will be retried.";
            RecordState(message, HostedServiceState.Degraded);
            Logger.LogWarning(
                "Dapr sidecar is not healthy. Retrying subscription creation in {Delay}",
                _options.SidecarHealthWaitTimeout);

            if (_options.FailFastOnSidecarUnavailable)
            {
                applicationLifetime.StopApplication();
                return;
            }
        }

        stoppingToken.ThrowIfCancellationRequested();

        RecordState("Dapr sidecar is healthy, proceeding with subscription creation", HostedServiceState.Starting);

        Logger.LogInformation("Dapr sidecar is healthy, creating subscriptions");

        Volatile.Write(ref _wasHealthy, 1);

        // Now safe to create subscriptions - call base to initialize and keep running
        await base.ExecuteBackgroundAsync(stoppingToken);
    }

    protected override async Task OnHeartbeatAsync(CancellationToken cancellationToken)
    {
        await base.OnHeartbeatAsync(cancellationToken);

        if (!healthCoordinator.IsHealthy)
        {
            Volatile.Write(ref _wasHealthy, 0);

            // The sidecar being unhealthy means no topic is delivering messages, even when the
            // SDK streams have not faulted yet. Mark live topics as recovering so the gap is
            // visible immediately. Topics already recovering (for example from a real stream
            // fault) keep their existing failure counters, so this fires once per outage.
            foreach (var topicName in _daprSubscriptionsByTopic.Keys)
            {
                if (TopicStatusStore.Get(ServiceKey, topicName)?.State
                    is not (TopicSubscriptionRuntimeState.Subscribing or TopicSubscriptionRuntimeState.Healthy))
                {
                    continue;
                }

                TopicStatusStore.ReportState(
                    ServiceKey, topicName, ProviderKind, TopicSubscriptionRuntimeState.Recovering,
                    "The Dapr sidecar is unhealthy; topic subscriptions are not receiving messages.");
            }

            return;
        }

        if (Interlocked.Exchange(ref _wasHealthy, 1) == 1)
        {
            return;
        }

        foreach (var subscription in _daprSubscriptionsByTopic.Values)
        {
            subscription.RequestReconnect(
                new InvalidOperationException("The Dapr sidecar recovered after an unhealthy health-check state."));
        }
    }

    /// <summary>
    /// Creates a Dapr streaming subscription for the given topic.
    /// </summary>
    protected override Task CreateExternalSubscriptionForTopicAsync(
        SubscriptionGroup group,
        Type eventType,
        CancellationToken cancellationToken)
    {
        var topicName = group.TopicName;
        lock (_subscriptionLifecycleLock)
        {
            if (Volatile.Read(ref _stopping) == 1 || Volatile.Read(ref _disposed) == 1)
            {
                return Task.CompletedTask;
            }

            var subscription = new DaprTopicSubscription(
                daprClient,
                healthCoordinator,
                _options,
                _recoveryPolicy,
                topicName,
                _deadLetterTopicPolicy.Resolve(topicName),
                HandleMessageAsync,
                _subscriptionStopping.Token,
                OnSubscriptionReceiverCreated,
                OnSubscriptionStable,
                OnSubscriptionRecoveryScheduled,
                OnSubscriptionCleanupFailed,
                OnSubscriptionSupervisorFailed,
                topicSubscriptionLogger);

            if (!_daprSubscriptionsByTopic.TryAdd(topicName, subscription))
            {
                return subscription.DisposeAsync().AsTask();
            }

            _recoveringTopics[topicName] = 0;
            RecordState($"Starting Dapr subscription supervisor for topic {topicName}", HostedServiceState.Running);
            Logger.LogDebug(
                "Starting Dapr subscription supervisor for topic {Topic} with EventType {EventType} (ServiceKey: {ServiceKey})",
                topicName,
                eventType.Name,
                ServiceKey ?? "default");

            subscription.Start();
            return Task.CompletedTask;
        }

        // The common dispatcher is the sole payload decoder.
        async Task<TopicResponseAction> HandleMessageAsync(TopicMessage message, CancellationToken ct)
        {
            if (_options.EnableMessageDataDebugLogging)
            {
                var rawJson = System.Text.Encoding.UTF8.GetString(message.Data.Span);
                Logger.LogInformation(
                    "Received message on topic {Topic}: {RawJson}",
                    message.Topic, rawJson);
            }

            var metadata = CreateMetadata(message, eventType, ServiceKey);
            var handlingTask = HandleExternalMessageAsync(
                new EventMessage(metadata, EventSubscriptionScope.Distributed, message.Data), ct);

            try
            {
                // The Dapr SDK only cancels this token; it still awaits the callback indefinitely.
                // Bound our wait while allowing the handler task to retain and dispose its scope when it finishes.
                await handlingTask.WaitAsync(ct);

                TopicStatusStore.ReportMessageProcessed(ServiceKey, message.Topic);

                return TopicResponseAction.Success;
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                _ = ObserveLateMessageHandlingAsync(handlingTask, message.Topic);

                TopicStatusStore.ReportError(ServiceKey, message.Topic, $"Dapr message handling deadline elapsed for topic {message.Topic}", ex);
                RecordState(
                    $"Dapr message handling deadline elapsed for topic {message.Topic}",
                    HostedServiceState.Degraded,
                    ex);
                Logger.LogWarning(
                    ex,
                    "Dapr message handling deadline elapsed for topic {Topic}; requesting retry",
                    message.Topic);
                return TopicResponseAction.Retry;
            }
            catch (EventMessageDeserializationException ex)
            {
                TopicStatusStore.ReportError(ServiceKey, message.Topic,
                    $"Rejected malformed Dapr message for topic {message.Topic}", ex);
                RecordState($"Rejected malformed Dapr message for topic {message.Topic}",
                    HostedServiceState.Degraded, ex);
                Logger.LogWarning(ex, "Dropping malformed Dapr message for topic {Topic}", message.Topic);
                return TopicResponseAction.Drop;
            }
            catch (Exception ex)
            {
                TopicStatusStore.ReportError(ServiceKey, message.Topic, $"Error handling Dapr message for topic {message.Topic}", ex);
                RecordState($"Error handling Dapr message for topic {message.Topic}",
                    HostedServiceState.Degraded, ex);

                Logger.LogError(ex,
                    "Error handling Dapr message for topic {Topic}; requesting retry",
                    message.Topic);
                return TopicResponseAction.Retry;
            }
        }
    }

    internal static EventDeliveryMetadata CreateMetadata(
        TopicMessage message, Type eventType, string? serviceKey)
    {
        string? Extension(string name) => message.Extensions.TryGetValue(name, out var value)
            ? value.StringValue : null;
        return new EventDeliveryMetadata(
            string.IsNullOrWhiteSpace(message.Id) ? null : message.Id,
            string.IsNullOrWhiteSpace(message.Source) ? null : message.Source,
            EventNameAttribute.GetNameOrDefault(eventType),
            message.Topic,
            serviceKey,
            Extension("traceparent"),
            Extension("tracestate"));
    }

    private void OnSubscriptionReceiverCreated(string topicName)
    {
        // A receiver generation exists but must survive the stability window before the topic
        // counts as healthy again.
        TopicStatusStore.ReportState(
            ServiceKey, topicName, ProviderKind, TopicSubscriptionRuntimeState.Subscribing,
            $"Dapr streaming receiver created for topic {topicName}");

        Logger.LogInformation(
            "Dapr streaming receiver created for topic {Topic} (ServiceKey: {ServiceKey})",
            topicName,
            ServiceKey ?? "default");
    }

    private void OnSubscriptionStable(string topicName)
    {
        _recoveringTopics.TryRemove(topicName, out _);

        TopicStatusStore.ReportState(
            ServiceKey, topicName, ProviderKind, TopicSubscriptionRuntimeState.Healthy,
            $"Dapr subscription for topic {topicName} is stable");

        if (_recoveringTopics.IsEmpty)
        {
            RecordState("All Dapr topic subscriptions are stable", HostedServiceState.Running);
        }

        Logger.LogInformation(
            "Dapr subscription remained stable for topic {Topic} (ServiceKey: {ServiceKey})",
            topicName,
            ServiceKey ?? "default");
    }

    private void OnSubscriptionRecoveryScheduled(string topicName, Exception exception, TimeSpan delay)
    {
        _recoveringTopics[topicName] = 0;

        TopicStatusStore.ReportState(
            ServiceKey, topicName, ProviderKind, TopicSubscriptionRuntimeState.Recovering,
            $"Dapr subscription for topic {topicName} failed; recovery is scheduled in {delay}",
            exception);

        RecordState(
            $"Dapr subscription for topic {topicName} failed; recovery is scheduled in {delay}",
            HostedServiceState.Degraded,
            exception);

        Logger.LogError(
            exception,
            "Dapr subscription failed for topic {Topic}; reconnecting in {Delay} (ServiceKey: {ServiceKey})",
            topicName,
            delay,
            ServiceKey ?? "default");
    }

    private void OnSubscriptionCleanupFailed(string topicName, Exception exception)
    {
        TopicStatusStore.ReportError(
            ServiceKey, topicName,
            $"Failed to clean up a Dapr subscription generation for topic {topicName}",
            exception);

        RecordState(
            $"Failed to clean up a Dapr subscription generation for topic {topicName}",
            HostedServiceState.Degraded,
            exception);
        Logger.LogWarning(
            exception,
            "Failed to clean up a Dapr subscription generation for topic {Topic} (ServiceKey: {ServiceKey})",
            topicName,
            ServiceKey ?? "default");
    }

    private void OnSubscriptionSupervisorFailed(string topicName, Exception exception)
    {
        _recoveringTopics[topicName] = 0;

        TopicStatusStore.ReportState(
            ServiceKey, topicName, ProviderKind, TopicSubscriptionRuntimeState.Failed,
            $"Dapr subscription supervisor terminated unexpectedly for topic {topicName}; " +
            "the topic will not receive messages until the application restarts",
            exception);

        RecordState(
            $"Dapr subscription supervisor terminated unexpectedly for topic {topicName}",
            HostedServiceState.Faulted,
            exception);
        Logger.LogCritical(
            exception,
            "Dapr subscription supervisor terminated unexpectedly for topic {Topic} (ServiceKey: {ServiceKey})",
            topicName,
            ServiceKey ?? "default");
    }

    private async Task ObserveLateMessageHandlingAsync(Task handlingTask, string topicName)
    {
        try
        {
            await handlingTask;
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation after the callback returned Retry is expected.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Dapr message handling for topic {Topic} faulted after the callback returned Retry",
                topicName);
        }
    }

    /// <summary>
    /// Removes a Dapr subscription for the given topic.
    /// </summary>
    protected override async Task RemoveExternalSubscriptionForTopicAsync(
        SubscriptionGroup group,
        CancellationToken cancellationToken)
    {
        var topicName = group.TopicName;
        _recoveringTopics.TryRemove(topicName, out _);

        if (_daprSubscriptionsByTopic.TryRemove(topicName, out var daprSubscription))
        {
            try
            {
                RecordState($"Disposing Dapr subscription for topic {topicName}", HostedServiceState.Running);

                Logger.LogInformation(
                    "Disposing Dapr subscription for topic {Topic} (ServiceKey: {ServiceKey})",
                    topicName, ServiceKey ?? "default");

                await daprSubscription.DisposeAsync();

                RecordState($"Successfully disposed Dapr subscription for topic {topicName}", HostedServiceState.Running);
            }
            catch (Exception ex)
            {
                RecordState($"Error disposing Dapr subscription for topic {topicName}",
                    HostedServiceState.Degraded, ex);

                Logger.LogError(ex,
                    "Error disposing Dapr subscription for topic {Topic}",
                    topicName);
                throw;
            }
        }
        else
        {
            RecordState($"Attempted to remove non-existent Dapr subscription for topic {topicName}", HostedServiceState.Degraded);

            Logger.LogWarning(
                "Attempted to remove Dapr subscription for topic {Topic}, but it was not found",
                topicName);
        }
    }

    protected override async Task DisposeExternalSubscriptionsAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, DaprTopicSubscription>[] subscriptions;
        lock (_subscriptionLifecycleLock)
        {
            subscriptions = _daprSubscriptionsByTopic.ToArray();
            _daprSubscriptionsByTopic.Clear();
            _recoveringTopics.Clear();
        }

        foreach (var (topicName, subscription) in subscriptions)
        {
            try
            {
                Logger.LogInformation(
                    "Disposing Dapr subscription for topic {Topic} during shutdown (ServiceKey: {ServiceKey})",
                    topicName,
                    ServiceKey ?? "default");

                await subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Error disposing Dapr subscription for topic {Topic} during shutdown",
                    topicName);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        BeginSubscriptionShutdown();
        await base.OnStoppingAsync(cancellationToken).ConfigureAwait(false);
    }

    private void BeginSubscriptionShutdown()
    {
        lock (_subscriptionLifecycleLock)
        {
            Volatile.Write(ref _stopping, 1);
        }

        try
        {
            _subscriptionStopping.Cancel();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to signal Dapr subscription supervisor shutdown");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        DaprTopicSubscription[] subscriptions;
        lock (_subscriptionLifecycleLock)
        {
            Volatile.Write(ref _stopping, 1);
            subscriptions = _daprSubscriptionsByTopic.Values.ToArray();
            _daprSubscriptionsByTopic.Clear();
            _recoveringTopics.Clear();
        }

        try
        {
            _subscriptionStopping.Cancel();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to signal Dapr subscription supervisor shutdown");
        }

        try
        {
            Task.WhenAll(subscriptions.Select(subscription => subscription.DisposeAsync().AsTask()))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to dispose one or more Dapr topic subscription supervisors");
        }

        _subscriptionStopping.Dispose();
        base.Dispose();
    }
}
