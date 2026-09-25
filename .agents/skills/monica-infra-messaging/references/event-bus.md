# Publish and receive events

`monica.AddEventBus()` registers the local bus, scoped publishing gateway, handler discovery, subscription registry, and receive dispatcher. Use `ILocalEventBus` when publisher and handlers run in one process; implement `ILocalEventHandler<TEvent>` in the host's discovery scope, or use `IEventBus.SubscribeAsync<TEvent,...>` for an explicit subscription. `DisableAutoDiscovery` disables the scan. A handler receives a cancellation token and should pass it to its own I/O.

For example, a discovered handler for `OrderApproved` uses the exact event type named by the publisher:

```csharp
public sealed record OrderApproved(Guid OrderId);

public sealed class OrderApprovedHandler(ILogger<OrderApprovedHandler> logger)
    : ILocalEventHandler<OrderApproved>
{
    public Task HandleEventAsync(OrderApproved eventData, CancellationToken cancellationToken)
    {
        logger.LogInformation("Approved order {OrderId}", eventData.OrderId);
        return Task.CompletedTask;
    }
}
```

```csharp
builder.AddMonica(monica => monica.AddEventBus());

await localEventBus.PublishAsync(
    new OrderApproved(orderId), cancellationToken: cancellationToken);
```

`IEventBus.PublishAsync<TEvent>` and `BulkPublishAsync<TEvent>` match the **exact** event type and topic. A handler registered for a base event does not receive a derived type. The optional `topicName` overrides event-name metadata. If a local event has no receiver, check the discovered handler type, its implemented interface, the topic override, and startup subscription registration before changing the transport.

For cross-process delivery, inject `IDistributedEventBus` after selecting a provider. `UseDistributedEventBus<TProvider>()` requires a `DistributedEventBusBase` that also supplies `IEventTransport`; `UseNoOpDistributedEventBus()` satisfies composition but sends nothing externally. For native Kafka, register `monica.AddEventBusKafka().UseKafkaProvider(cluster)` with a configured `KafkaClusterConfig`; this selects the transport and starts its subscription worker. `AddConfiguredCluster(cluster)` only exposes a cluster to the Kafka console, and `UseDaprKafkaIntegration(...)` describes an existing Dapr-backed Kafka component rather than selecting an EventBus provider. `AddKeyedEventBus(key, useDistributed: true)` needs the default distributed provider, while `AddKeyedLocalEventBus(key)` creates an isolated keyed local bus.

Implement `IDistributedEventHandler<TEvent>` for distributed receives and make sure its assembly is discovered. If nothing arrives, inspect the publishing service's provider selection, the receiving service's worker and subscription startup, service key, exact topic/type, and handler discovery. A successful ordinary publish follows its selected provider's behavior; it is not an end-to-end acknowledgment from every handler.

For a database write that must publish only after commit, mark the event `[Outbox]`, enable outbox on the primary UnitOfWork context, and publish through the scoped gateway inside that operation. A successful outbox `PublishAsync` means the prepared payload was staged, not committed or transported. Worker delivery can retry, so consumers need idempotency; `[Inbox]` supplies durable handler deduplication when configured. Follow the persistence skill's [transactional-events reference](../../monica-infra-persistence/references/transactional-events.md) for migration and transaction ownership.

Source and checks: `Monica.EventBus/Abstractions/IEventBus.cs`, `Abstractions/Handlers/ILocalEventHandler.cs` and `IDistributedEventHandler.cs`, `Monica.EventBus/Modules/ModuleEventBus.cs`, `Monica.EventBus.Kafka/Modules/ModuleEventBusKafka.cs`, `tests/Test.Monica.EventBus/Services/EventBusAutoDiscoveryLifecycleTests.cs`, `tests/Test.Monica.EventBus/Modules/ModuleEventBusCompositionTests.cs`, and `OutboxGatewayTests.cs`.
