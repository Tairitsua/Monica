using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.JsonSerialization.Services.Support;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.Support;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.Framework.UI.UIEventBus.State;

/// <summary>
/// Event bus test service for managing test publishing, listener sessions, and message collection.
/// </summary>
/// <remarks>
/// The DI scope owns this service and its row-level listener handles. Row start, stop, and scope cleanup operations
/// serialize; failed removal retains the exact handle for retry. Scope retirement rejects new row starts and drains
/// already admitted starts before cleanup. Components borrow the service and must not dispose it when a dialog closes.
/// </remarks>
public sealed class EventBusTestService(
    IServiceScopeFactory serviceScopeFactory,
    IServiceProvider serviceProvider,
    IEventSubscriptionRegistry subscriptionRegistry,
    EventBusProviderDiscoveryService providerDiscoveryService,
    IJsonSerializerOptionsProvider jsonSerializerOptionsProvider,
    IOptions<ModuleEventBusUIOption> options,
    IStringLocalizer<EventBusResource> localizer,
    ILogger<EventBusTestService> logger) : IAsyncDisposable
{
    private const int DEFAULT_LEGACY_MESSAGE_COUNT = 100;

    private readonly ConcurrentDictionary<EventSubscriptionId, RuntimeListenerState> _runtimeListeners = new();
    private readonly SemaphoreSlim _runtimeListenerGate = new(1, 1);
    private int _runtimeListenersRetired;
    private readonly ConcurrentQueue<ReceivedTestMessage> _receivedMessages = new();
    private readonly object _legacySubscriptionLock = new();
    private readonly SemaphoreSlim _legacySubscriptionGate = new(1, 1);
    private readonly SemaphoreSlim _standaloneLeaseGate = new(1, 1);
    private readonly CancellationTokenSource _standaloneRetirement = new();
    private IEventSubscription? _activeSubscription;
    private EventBusStandaloneListenerLease? _standaloneLease;
    private string? _currentTopicName;
    private bool _standaloneRetired;

    /// <summary>
    /// Gets whether this scoped service owns a standalone test subscription, including one whose removal failed.
    /// </summary>
    public bool IsSubscribed
    {
        get
        {
            lock (_legacySubscriptionLock)
            {
                return _activeSubscription != null;
            }
        }
    }

    /// <summary>
    /// Gets the standalone subscription's topic, retained until its exact registry handle is successfully removed.
    /// </summary>
    public string? CurrentTopicName
    {
        get
        {
            lock (_legacySubscriptionLock)
            {
                return _currentTopicName;
            }
        }
    }

    /// <summary>Acquires exclusive standalone-listener control for one mounted component.</summary>
    /// <param name="cancellationToken">Cancels waiting or rejects acquisition after the component has retired.</param>
    /// <returns>A component-owned lease that adopts an exact listener retained after a previous owner's failed stop.</returns>
    /// <remarks>
    /// Waits until the previous lease releases ownership. Cancel and await a pending acquisition during component
    /// retirement; a returned lease must always be disposed. The service serializes listener operations separately,
    /// so acquisition cannot race a previous owner's admitted start/stop. Scoped-service disposal cancels waiters and
    /// rejects new leases. Legacy unleased start/stop calls cannot interrupt an active component lease.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Acquisition was cancelled by the caller or scoped-service retirement.</exception>
    /// <exception cref="ObjectDisposedException">The scoped service has already retired.</exception>
    public async Task<EventBusStandaloneListenerLease> AcquireStandaloneListenerLeaseAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource admission;
        lock (_legacySubscriptionLock)
        {
            ObjectDisposedException.ThrowIf(_standaloneRetired, this);
            admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _standaloneRetirement.Token);
        }

        using (admission)
        {
            await _standaloneLeaseGate.WaitAsync(admission.Token);
            try
            {
                await _legacySubscriptionGate.WaitAsync(admission.Token);
                try
                {
                    lock (_legacySubscriptionLock)
                    {
                        admission.Token.ThrowIfCancellationRequested();
                        ObjectDisposedException.ThrowIf(_standaloneRetired, this);
                        var lease = new EventBusStandaloneListenerLease(this);
                        _standaloneLease = lease;
                        return lease;
                    }
                }
                finally { _legacySubscriptionGate.Release(); }
            }
            catch
            {
                _standaloneLeaseGate.Release();
                throw;
            }
        }
    }

    internal EventBusStandaloneListenerSnapshot? GetStandaloneListenerSnapshot(EventBusStandaloneListenerLease lease)
    {
        lock (_legacySubscriptionLock)
        {
            return ReferenceEquals(_standaloneLease, lease) && !lease.IsRetired && !_standaloneRetired
                && _activeSubscription is { } subscription && _currentTopicName is { } topic
                    ? new EventBusStandaloneListenerSnapshot(subscription.Id, topic)
                    : null;
        }
    }

    internal async Task ReleaseStandaloneLeaseAsync(EventBusStandaloneListenerLease lease)
    {
        await _legacySubscriptionGate.WaitAsync();
        try
        {
            lock (_legacySubscriptionLock)
            {
                if (ReferenceEquals(_standaloneLease, lease)) _standaloneLease = null;
            }
        }
        finally
        {
            _legacySubscriptionGate.Release();
            _standaloneLeaseGate.Release();
        }
    }

    internal Task<Res<IEventSubscription>> SubscribeForLeaseAsync(EventBusStandaloneListenerLease lease, string topicName) =>
        SubscribeToTestTopicCoreAsync(topicName, lease);

    internal Task<Res> UnsubscribeForLeaseAsync(EventBusStandaloneListenerLease lease) =>
        UnsubscribeFromTestTopicCoreAsync(lease);

    /// <summary>
    /// Creates a formatted JSON example for the event type behind a subscription.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <returns>A formatted JSON sample or an error result.</returns>
    public Res<string> CreateRuntimeJsonSample(EventSubscriptionId subscriptionId)
    {
        try
        {
            var subscription = GetSourceSubscription(subscriptionId);
            if (subscription is null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            var sample = CreateSampleValue(subscription.EventType, [], depth: 0);
            var json = JsonSerializer.Serialize(
                sample,
                jsonSerializerOptionsProvider.SerializerOptions.Copy(o => o.WriteIndented = true));

            return Res.Ok<string>(json ?? "{}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create EventBus test JSON sample for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:CreateJsonSampleFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Validates that JSON can be deserialized into the selected subscription event type.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <param name="json">JSON payload to validate.</param>
    /// <returns>A success result when deserialization succeeds.</returns>
    public Res ValidateRuntimePayload(EventSubscriptionId subscriptionId, string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Res.Fail(localizer["Services:Common:JsonContentRequired"]);
            }

            var subscription = GetSourceSubscription(subscriptionId);
            if (subscription is null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            _ = DeserializePayload(subscription.EventType, json);
            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EventBus test JSON validation failed for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:JsonValidationFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Injects a JSON test payload immediately through the selected subscription's transport or local dispatcher.
    /// The selected contract, topic, scope, and service key are preserved, including for Outbox-marked events.
    /// This diagnostic operation does not stage an Outbox message or require a publishing transaction.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <param name="json">JSON payload to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success after local handlers complete or the distributed transport accepts the message; distributed
    /// success does not acknowledge consumer completion. Preparation and delivery failures return an error result.
    /// </returns>
    public async Task<Res> PublishRuntimePayloadAsync(
        EventSubscriptionId subscriptionId,
        string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Res.Fail(localizer["Services:Common:JsonContentRequired"]);
            }

            var subscription = GetSourceSubscription(subscriptionId);
            if (subscription is null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            var eventBusResult = GetEventBus(subscription);
            if (eventBusResult.IsFailed(out var providerError, out _))
            {
                return providerError;
            }

            var payload = DeserializePayload(subscription.EventType, json);
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var message = services.GetRequiredService<IEventMessageFactory>().Prepare(
                subscription.EventType, payload, subscription.Scope, subscription.TopicName, subscription.ServiceKey);

            if (subscription.Scope == EventSubscriptionScope.Local)
            {
                await services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, cancellationToken);
            }
            else
            {
                var transport = subscription.ServiceKey is null
                    ? services.GetRequiredService<IEventTransport>()
                    : services.GetRequiredKeyedService<IEventTransport>(subscription.ServiceKey);
                await transport.SendAsync(message, cancellationToken);
            }

            logger.LogInformation(
                "Published EventBus UI runtime test payload for {EventType} on {TopicName} ({Scope}, ServiceKey: {ServiceKey})",
                subscription.EventType.GetCleanFullName(),
                subscription.TopicName,
                subscription.Scope,
                subscription.ServiceKey ?? "default");

            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish EventBus UI runtime test payload for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:PublishRuntimePayloadFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>Prepares a fresh message identity for explicit delivery to one local Active or Held handler.</summary>
    /// <param name="subscriptionId">Exact registry target; disabled or unavailable targets are rejected.</param>
    /// <param name="json">Payload deserialized using the target's event contract and host serializer.</param>
    /// <returns>The prepared message, or a validation failure. No transport or handler is invoked.</returns>
    /// <remarks>Retain this exact message, including its body, only when an intentional replay is requested.</remarks>
    public Res<EventMessage> PrepareLocalTestMessage(EventSubscriptionId subscriptionId, string json)
    {
        try
        {
            var subscription = GetSourceSubscription(subscriptionId);
            if (!CanDeliverLocally(subscription))
                return Res.Fail(localizer["Services:Test:LocalTargetUnavailable"]);
            if (string.IsNullOrWhiteSpace(json))
                return Res.Fail(localizer["Services:Common:JsonContentRequired"]);

            var payload = DeserializePayload(subscription!.EventType, json);
            return Res.Ok(serviceProvider.GetRequiredService<IEventMessageFactory>().Prepare(
                subscription.EventType, payload, subscription.Scope, subscription.TopicName, subscription.ServiceKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to prepare local EventBus test message for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:JsonValidationFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>Delivers a prepared message only to the selected local registry target through the native receive pipeline.</summary>
    /// <param name="subscriptionId">Exact Active or Held target, revalidated immediately before dispatch.</param>
    /// <param name="message">Fresh prepared message, or the unchanged message for an intentional Inbox replay.</param>
    /// <param name="cancellationToken">Cancels this delivery; it does not activate or stop a subscription.</param>
    /// <returns>Success after the handler pipeline completes, or a preparation/delivery failure.</returns>
    /// <remarks>Invokes real handlers, transactions and Inbox behavior. Never publishes to a broker or enables automatic receipt.</remarks>
    public async Task<Res> DeliverLocalTestMessageAsync(
        EventSubscriptionId subscriptionId, EventMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!CanDeliverLocally(GetSourceSubscription(subscriptionId)))
                return Res.Fail(localizer["Services:Test:LocalTargetUnavailable"]);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventReceiveDispatcher>()
                .DispatchTestAsync(message, [subscriptionId], cancellationToken);
            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deliver local EventBus test message for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:LocalDeliveryFailed", ex.GetMessageRecursively()]);
        }
    }

    private static bool CanDeliverLocally(IEventSubscription? subscription) =>
        subscription is { Activation: not ActivationMode.Disabled,
            State: EventSubscriptionState.Active or EventSubscriptionState.Held };

    /// <summary>
    /// Starts a background listener for the selected subscription.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <returns>The existing or newly owned listener snapshot, or a failure when startup fails or the scope has retired.</returns>
    /// <remarks>
    /// The scoped service owns one listener per source subscription, using its exact event type, topic, service key,
    /// and bus scope. Closing a dialog does not stop it. Starts serialize with stops and scope cleanup, preventing
    /// duplicate registry handles. This operation has no cancellation argument; components must drain admitted work.
    /// </remarks>
    public async Task<Res<RuntimeTestListenerSnapshot>> StartRuntimeListenerAsync(EventSubscriptionId subscriptionId)
    {
        await _runtimeListenerGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _runtimeListenersRetired) != 0, this);
            var subscription = GetSourceSubscription(subscriptionId);
            if (subscription is null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            if (_runtimeListeners.TryGetValue(subscriptionId, out var existingState))
            {
                return existingState.CreateSnapshot();
            }

            if (subscription.Activation != ActivationMode.Automatic)
                return Res.Fail(localizer["Services:Monitor:ActivationHeld"]);

            var state = new RuntimeListenerState(
                subscriptionId,
                subscription.EventType,
                subscription.TopicName,
                GetMessageLimit(),
                jsonSerializerOptionsProvider.SerializerOptions.Copy(o => o.WriteIndented = true));

            var descriptor = new EventSubscriptionDescriptor
            {
                ServiceKey = subscription.ServiceKey,
                EventType = subscription.EventType,
                TopicName = subscription.TopicName,
                Scope = subscription.Scope,
                IsAutoDiscovered = false,
                Metadata = EventBusTestMetadataKeys.CreateListenerMetadata(subscriptionId),
                HandlerFactory = new EventBusRuntimeTestHandlerFactory(
                    serviceScopeFactory,
                    subscription.EventType,
                    subscription.Scope,
                    state.CaptureAsync)
            };

            var listenerSubscription = await subscriptionRegistry.SubscribeAsync(descriptor);
            state.AttachSubscription(listenerSubscription);
            // The gate owns admission through adoption, including a handle returned after scope retirement began.
            // Cleanup waits behind this operation and therefore cannot overlook the newly owned exact handle.
            _runtimeListeners[subscriptionId] = state;

            logger.LogInformation(
                "Started EventBus UI runtime listener {ListenerSubscriptionId} for source subscription {SourceSubscriptionId}",
                listenerSubscription.Id,
                subscriptionId);

            return state.CreateSnapshot();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start EventBus UI runtime listener for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:StartRuntimeListenerFailed", ex.GetMessageRecursively()]);
        }
        finally
        {
            _runtimeListenerGate.Release();
        }
    }

    /// <summary>
    /// Stops the background listener for the selected subscription.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <returns>Success when removal completes or no listener is owned; otherwise a retryable failure.</returns>
    /// <remarks>
    /// Serializes with starts and scope cleanup. Failed registry removal retains the same listener handle and its
    /// messages, and a later stop or repeated scope disposal may retry it. This operation remains available after
    /// scope retirement for exact-handle cleanup and has no cancellation argument; callers must drain admitted work.
    /// </remarks>
    public async Task<Res> StopRuntimeListenerAsync(EventSubscriptionId subscriptionId)
    {
        await _runtimeListenerGate.WaitAsync();
        try
        {
            return await StopRuntimeListenerCoreAsync(subscriptionId);
        }
        finally
        {
            _runtimeListenerGate.Release();
        }
    }

    private async Task<Res> StopRuntimeListenerCoreAsync(EventSubscriptionId subscriptionId)
    {
        try
        {
            if (!_runtimeListeners.TryGetValue(subscriptionId, out var state))
            {
                return Res.Ok();
            }

            if (state.ListenerSubscription is not null)
            {
                await subscriptionRegistry.UnsubscribeAsync(state.ListenerSubscription.Id);
            }

            _runtimeListeners.TryRemove(subscriptionId, out _);
            state.Clear();

            logger.LogInformation("Stopped EventBus UI runtime listener for source subscription {SubscriptionId}", subscriptionId);
            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop EventBus UI runtime listener for {SubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Test:StopRuntimeListenerFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Gets the current listener snapshot for the selected subscription.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    /// <returns>Current listener state.</returns>
    public RuntimeTestListenerSnapshot GetRuntimeListenerSnapshot(EventSubscriptionId subscriptionId)
    {
        return _runtimeListeners.TryGetValue(subscriptionId, out var state)
            ? state.CreateSnapshot()
            : new RuntimeTestListenerSnapshot();
    }

    /// <summary>
    /// Gets the ids of subscriptions that currently have active test listeners.
    /// </summary>
    /// <returns>Active source subscription ids.</returns>
    public IReadOnlySet<string> GetActiveRuntimeListenerIds()
    {
        return _runtimeListeners.Keys
            .Select(id => id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clears retained listener messages for the selected subscription.
    /// </summary>
    /// <param name="subscriptionId">Selected subscription id.</param>
    public void ClearRuntimeListenerMessages(EventSubscriptionId subscriptionId)
    {
        if (_runtimeListeners.TryGetValue(subscriptionId, out var state))
        {
            state.Clear();
        }
    }

    /// <summary>
    /// Publish standalone test-panel message.
    /// </summary>
    public async Task<Res> PublishTestMessageAsync(string message, string? topicName, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Res.Fail(localizer["Services:Common:MessageContentRequired"]);
            }

            if (string.IsNullOrWhiteSpace(topicName))
            {
                return Res.Fail(localizer["Services:Common:TopicNameRequired"]);
            }

            var testMessage = new TestEventMessage
            {
                Message = message,
                CreatedAt = DateTime.UtcNow,
                MessageId = Guid.NewGuid().ToString()
            };

            await serviceProvider.GetRequiredService<IDistributedEventBus>().PublishAsync(testMessage, topicName, cancellationToken);

            logger.LogInformation("已发布测试消息到主题 {TopicName}: {MessageId}", topicName, testMessage.MessageId);

            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发布测试消息失败");
            return Res.Fail(localizer["Services:Test:PublishMessageFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Subscribes the standalone test panel to a topic on the default distributed bus.
    /// </summary>
    /// <param name="topicName">The exact topic to subscribe to; an empty or whitespace topic is rejected.</param>
    /// <returns>The owned subscription, or a failure if another subscription is owned or this service has retired.</returns>
    /// <remarks>
    /// Standalone start and stop operations serialize. The scoped service owns the resulting handle until successful
    /// registry removal. This API does not accept cancellation; a retiring component must await its admitted operation.
    /// </remarks>
    public Task<Res<IEventSubscription>> SubscribeToTestTopicAsync(string topicName) =>
        SubscribeToTestTopicCoreAsync(topicName, null);

    private async Task<Res<IEventSubscription>> SubscribeToTestTopicCoreAsync(
        string topicName, EventBusStandaloneListenerLease? lease)
    {
        await _legacySubscriptionGate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(topicName))
            {
                return Res.Fail(localizer["Services:Common:TopicNameRequired"]);
            }

            lock (_legacySubscriptionLock)
            {
                ObjectDisposedException.ThrowIf(_standaloneRetired, this);
                EnsureStandaloneOwner(lease);
                if (_activeSubscription != null)
                {
                    return Res.Fail(localizer["Services:Test:ActiveSubscriptionExists"]);
                }
            }

            var subscription = await serviceProvider.GetRequiredService<IDistributedEventBus>().SubscribeAsync<TestEventMessage>((eventData, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var receivedMessage = new ReceivedTestMessage
                {
                    EventData = eventData,
                    ReceivedAt = DateTime.Now,
                    TopicName = topicName
                };

                _receivedMessages.Enqueue(receivedMessage);

                while (_receivedMessages.Count > DEFAULT_LEGACY_MESSAGE_COUNT)
                {
                    _receivedMessages.TryDequeue(out _);
                }

                logger.LogInformation("收到测试消息: {MessageId} from topic {TopicName}",
                    eventData.MessageId, topicName);

                return Task.CompletedTask;
            }, topicName);

            lock (_legacySubscriptionLock)
            {
                _activeSubscription = subscription;
                _currentTopicName = topicName;
            }

            logger.LogInformation("已订阅测试主题: {TopicName}", topicName);
            return Res.Ok(subscription);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "订阅测试主题失败");
            return Res.Fail(localizer["Services:Test:SubscribeFailed", ex.GetMessageRecursively()]);
        }
        finally
        {
            _legacySubscriptionGate.Release();
        }
    }

    /// <summary>
    /// Removes the exact standalone test subscription from the registry.
    /// </summary>
    /// <returns>Success when removal completes or no subscription is owned; otherwise a retryable failure.</returns>
    /// <remarks>
    /// Start and stop operations serialize. A failed removal retains the handle and topic, preventing a replacement
    /// start from silently abandoning it. Call this method again to retry. The operation has no cancellation parameter.
    /// </remarks>
    public Task<Res> UnsubscribeFromTestTopicAsync() => UnsubscribeFromTestTopicCoreAsync(null);

    private async Task<Res> UnsubscribeFromTestTopicCoreAsync(
        EventBusStandaloneListenerLease? lease, bool forceScopeCleanup = false)
    {
        await _legacySubscriptionGate.WaitAsync();
        try
        {
            IEventSubscription? subscription;

            lock (_legacySubscriptionLock)
            {
                if (!forceScopeCleanup) EnsureStandaloneOwner(lease);
                subscription = _activeSubscription;
            }

            if (subscription != null)
            {
                await subscriptionRegistry.UnsubscribeAsync(subscription.Id);
                lock (_legacySubscriptionLock)
                {
                    _activeSubscription = null;
                    _currentTopicName = null;
                }

                logger.LogInformation("已取消订阅测试主题");
            }

            return Res.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "取消订阅失败");
            return Res.Fail(localizer["Services:Test:UnsubscribeFailed", ex.GetMessageRecursively()]);
        }
        finally
        {
            _legacySubscriptionGate.Release();
        }
    }

    /// <summary>
    /// Get a list of received standalone test-panel messages.
    /// </summary>
    public List<ReceivedTestMessage> GetReceivedMessages()
    {
        return _receivedMessages.ToList();
    }

    /// <summary>
    /// Clear standalone test-panel received messages.
    /// </summary>
    public void ClearReceivedMessages()
    {
        _receivedMessages.Clear();
        logger.LogInformation("已清空接收到的测试消息");
    }

    /// <summary>
    /// Retires new listener starts and attempts to release all listener subscriptions owned by this scope.
    /// </summary>
    /// <remarks>
    /// Waits for admitted row and standalone start/stop operations through their respective owner gates. Failed
    /// removals retain exact ownership handles for a later stop or repeated disposal attempt. Queued or late row
    /// starts are rejected; an in-flight registry start is adopted before cleanup so its handle cannot be abandoned.
    /// Components separately drain their admitted commands before their borrowed service scope is released.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _runtimeListenersRetired, 1);
        bool firstRetirement;
        lock (_legacySubscriptionLock)
        {
            firstRetirement = !_standaloneRetired;
            _standaloneRetired = true;
        }

        if (firstRetirement) _standaloneRetirement.Cancel();

        await _runtimeListenerGate.WaitAsync();
        try
        {
            foreach (var sourceSubscriptionId in _runtimeListeners.Keys.ToList())
            {
                await StopRuntimeListenerCoreAsync(sourceSubscriptionId);
            }
        }
        finally
        {
            _runtimeListenerGate.Release();
        }

        await UnsubscribeFromTestTopicCoreAsync(null, forceScopeCleanup: true);
        if (firstRetirement) _standaloneRetirement.Dispose();
        _receivedMessages.Clear();
        logger.LogInformation("EventBusTestService 已释放");
    }

    private void EnsureStandaloneOwner(EventBusStandaloneListenerLease? lease)
    {
        if (lease is not null)
        {
            ObjectDisposedException.ThrowIf(lease.IsRetired || !ReferenceEquals(_standaloneLease, lease), lease);
        }
        else if (_standaloneLease is not null)
        {
            throw new InvalidOperationException("Standalone listener control is held by a component-owned lease.");
        }
    }

    private IEventSubscription? GetSourceSubscription(EventSubscriptionId subscriptionId)
    {
        var subscription = subscriptionRegistry.GetById(subscriptionId);
        return subscription is not null && !EventBusTestMetadataKeys.IsTestListenerSubscription(subscription)
            ? subscription
            : null;
    }

    private Res<IEventBus> GetEventBus(IEventSubscription subscription)
    {
        if (subscription.Scope == EventSubscriptionScope.Local)
        {
            if (providerDiscoveryService.GetLocalProvider(subscription.ServiceKey).IsFailed(out var error, out var localProvider))
            {
                return error;
            }

            return Res.Ok<IEventBus>(localProvider);
        }

        if (providerDiscoveryService.GetDistributedProvider(subscription.ServiceKey).IsFailed(out var distributedError, out var distributedProvider))
        {
            return distributedError;
        }

        return Res.Ok<IEventBus>(distributedProvider);
    }

    private object DeserializePayload(Type eventType, string json)
    {
        return JsonSerializer.Deserialize(json, eventType, jsonSerializerOptionsProvider.SerializerOptions)
               ?? throw new InvalidOperationException(localizer["Services:Test:JsonDeserializeNull"]);
    }

    private object? CreateSampleValue(Type type, HashSet<Type> visitedTypes, int depth)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType is not null)
        {
            return CreateSampleValue(underlyingType, visitedTypes, depth);
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(Guid))
        {
            return Guid.Empty;
        }

        if (type == typeof(DateTime))
        {
            return DateTime.UnixEpoch;
        }

        if (type == typeof(DateTimeOffset))
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (type == typeof(DateOnly))
        {
            return DateOnly.FromDateTime(DateTime.UnixEpoch);
        }

        if (type == typeof(TimeOnly))
        {
            return TimeOnly.MinValue;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.Zero;
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(type);
        }

        if (type.IsNumeric())
        {
            return Activator.CreateInstance(type);
        }

        if (type == typeof(byte[]))
        {
            return Array.Empty<byte>();
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            return elementType is null ? [] : new[] { CreateSampleValue(elementType, visitedTypes, depth + 1) };
        }

        if (TryGetDictionaryValueType(type, out var dictionaryValueType))
        {
            return new Dictionary<string, object?>
            {
                ["key"] = CreateSampleValue(dictionaryValueType, visitedTypes, depth + 1)
            };
        }

        if (TryGetEnumerableElementType(type, out var collectionElementType))
        {
            return new[] { CreateSampleValue(collectionElementType, visitedTypes, depth + 1) };
        }

        if (type.IsAbstract || type.IsInterface || depth > 4 || !visitedTypes.Add(type))
        {
            return null;
        }

        var sample = new Dictionary<string, object?>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }

            sample[GetJsonMemberName(property)] = CreateSampleValue(property.PropertyType, visitedTypes, depth + 1);
        }

        if (jsonSerializerOptionsProvider.SerializerOptions.IncludeFields)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                sample[GetJsonMemberName(field)] = CreateSampleValue(field.FieldType, visitedTypes, depth + 1);
            }
        }

        visitedTypes.Remove(type);
        return sample;
    }

    private string GetJsonMemberName(MemberInfo member)
    {
        var explicitName = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        return jsonSerializerOptionsProvider.SerializerOptions.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = typeof(object);
            return false;
        }

        elementType = type.GetInterfaces()
            .Append(type)
            .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .FirstOrDefault() ?? typeof(object);

        return elementType != typeof(object);
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionaryType = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                && candidate.GetGenericArguments()[0] == typeof(string));

        valueType = dictionaryType?.GetGenericArguments()[1] ?? typeof(object);
        return dictionaryType is not null;
    }

    private int GetMessageLimit()
    {
        return Math.Max(1, options.Value.TestListenerMessageLimit);
    }

    private sealed class RuntimeListenerState(
        EventSubscriptionId sourceSubscriptionId,
        Type eventType,
        string topicName,
        int messageLimit,
        JsonSerializerOptions serializerOptions)
    {
        private readonly Queue<RuntimeTestEventMessage> _messages = new();
        private readonly object _messagesLock = new();

        public EventSubscriptionId SourceSubscriptionId { get; } = sourceSubscriptionId;
        public Type EventType { get; } = eventType;
        public string TopicName { get; } = topicName;
        public int MessageLimit { get; } = messageLimit;
        public JsonSerializerOptions SerializerOptions { get; } = serializerOptions;
        public DateTime StartedAt { get; } = DateTime.Now;
        public IEventSubscription? ListenerSubscription { get; private set; }

        public void AttachSubscription(IEventSubscription listenerSubscription)
        {
            ListenerSubscription = listenerSubscription;
        }

        public Task CaptureAsync(object eventData)
        {
            var message = new RuntimeTestEventMessage
            {
                ReceivedAt = DateTime.Now,
                TopicName = TopicName,
                EventType = EventType.GetCleanFullName(),
                PayloadJson = JsonSerializer.Serialize(
                                  eventData,
                                  EventType,
                                  SerializerOptions) ?? "{}"
            };

            lock (_messagesLock)
            {
                _messages.Enqueue(message);
                while (_messages.Count > MessageLimit)
                {
                    _messages.Dequeue();
                }
            }

            return Task.CompletedTask;
        }

        public RuntimeTestListenerSnapshot CreateSnapshot()
        {
            lock (_messagesLock)
            {
                return new RuntimeTestListenerSnapshot
                {
                    IsListening = true,
                    TopicName = TopicName,
                    StartedAt = StartedAt,
                    MessageCount = _messages.Count,
                    Messages = _messages.ToList()
                };
            }
        }

        public void Clear()
        {
            lock (_messagesLock)
            {
                _messages.Clear();
            }
        }
    }
}

/// <summary>
/// Test message received by the standalone test panel.
/// </summary>
public class ReceivedTestMessage
{
    /// <summary>
    /// Event data.
    /// </summary>
    public required TestEventMessage EventData { get; init; }

    /// <summary>
    /// Receive time in local time.
    /// </summary>
    public DateTime ReceivedAt { get; init; }

    /// <summary>
    /// Topic name.
    /// </summary>
    public required string TopicName { get; init; }
}
