# Publish and receive events

For application EventBus contracts and consumers, including outbox or inbox refactors, first follow the ProjectUnit [event contract](../../monica-application-project-unit-development/references/12-entity-request-event-template.md) and [handler](../../monica-application-project-unit-development/references/14-event-handler-template.md) templates. This reference owns transport capabilities and setup; the interface example below demonstrates the general EventBus API.

`monica.AddEventBus()` registers the local bus, scoped publishing gateway, handler discovery, subscription registry, and receive dispatcher. Use `ILocalEventBus` when publisher and handlers run in one process; implement `ILocalEventHandler<TEvent>` in the host's discovery scope, or use `IEventBus.SubscribeAsync<TEvent,...>` for an explicit subscription. `DisableAutoDiscovery` disables the scan. A handler receives a cancellation token and should pass it to its own I/O.

Automatic subscriptions include only concrete handler types registered in the host container. When automatic handlers are discovered, the container must expose `IServiceProviderIsService`, as Microsoft DI does, so discovery can inspect registrations without constructing handlers. Disabled or empty discovery does not require this inspector; a discovered automatic handler without it causes a clear startup error. Each delivery activates its handler in a fresh asynchronously disposed DI scope; constructor failures remain delivery failures rather than removing the subscription during discovery.

EventBus execution defaults to an automatic UnitOfWork when that behavior is registered. Put any `ExecutionTransactionAttribute` override or context selection on the concrete `HandleEventAsync` method, following the persistence [transaction contract](../../monica-infra-persistence/references/transactions.md).

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

## Processing retries and dead letters

`ModuleEventBusOption.MessageRetryCount` is the single processing retry budget for Dapr and native Kafka. It defaults to **1 retry after the first attempt** (at most two attempts). Set it to **0** to disable retries; negative values fail policy activation. All processing failures consume the same budget: `BusinessException`, ordinary exceptions, timeout, malformed payloads, and a handler's independent cancellation. There is no business-exception classification or unlimited processing mode.

```csharp
builder.AddMonica(monica =>
    monica.AddEventBus(options => options.MessageRetryCount = 1)
        .UseDaprProvider(options => options.DeadLetterTopicSuffix = ".dead-letter"));
```

Retries run immediately within one transport delivery, invoking the receive dispatcher again with the same message identity and original body. Each invocation deserializes again and activates fresh handler scopes and transactions. No message-ID cache or persistent retry counter is needed. A successful retry ends processing immediately. The budget applies to the entire delivery, so handlers that already succeeded may be invoked again; use Inbox or another idempotency boundary for effects that must not repeat.

| Receive outcome | Dapr streaming provider | Native Kafka provider |
| --- | --- | --- |
| Any attempt completes | `Success` | Commit offset |
| Processing retry budget exhausted | Log and return `Drop`; Dapr forwards to the configured dead-letter topic, or discards when none is configured | Log and commit offset, allowing the next record to proceed |
| Transport/host stops or subscription is removed | Stop attempts and return `Retry` when the stream can still acknowledge | Stop without committing or seeking |

Cancellation of the transport lifetime is shutdown, not a processing failure. An exception or canceled task produced by a handler while the transport remains active is an ordinary failed attempt. This preserves messages during normal host restarts without depending on the handler's exception type.

Dapr's `MessageHandlingTimeout` remains 30 seconds by default and now applies **per attempt**. Monica owns this deadline; the SDK callback token represents stream lifetime only. Each attempt receives a fresh linked cancellation token. On timeout Monica cancels the attempt and stops waiting. A handler that does not honor cancellation may continue, retaining its scope until it finishes and potentially overlapping a retry or terminal forwarding. Its late completion cannot change the delivery result. Handlers must pass cancellation to I/O and protect repeatable side effects. Native Kafka counts thrown timeout failures against the same budget; it does not impose a separate handler execution deadline.

Native Kafka currently has no dead-letter destination option. Its exhausted processing failures are discarded after their offset commit succeeds. A failed offset commit remains a transport failure; a failed rewind stops the consumer before it can acknowledge later records. Dapr and Kafka retain the final exception in topic diagnostics and log message identity and topic information; exhausted deliveries are not counted as successfully processed messages.

Dapr's `DeadLetterTopicSuffix` defaults to `null`. A configured suffix must be non-empty and contain no whitespace. Each source topic uses `<topic><suffix>`; a topic already ending in the suffix has no further dead-letter target. Provision broker topics and retention separately. `Drop` delegates terminal forwarding to Dapr; Monica does not republish the payload itself. See the [Dapr streaming response contract](https://github.com/dapr/dotnet-sdk/blob/386b0ed7487ea365341f0ac52c4ba4df3593b635/src/Dapr.Messaging/PublishSubscribe/TopicResponseAction.cs#L31) and [dead-letter configuration](https://docs.dapr.io/developing-applications/building-blocks/pubsub/pubsub-deadletter/).

The limit bounds processing attempts **per transport delivery**, not broker redelivery across crashes, failed acknowledgments, or connection recovery. Those events can deliver the same message again with a fresh budget; broker delivery remains at least once. Dapr resiliency, broker retry configuration, native Kafka reconnection and offset-commit recovery remain transport concerns. This policy does not claim a durable, cross-restart attempt limit.

The failing handler's transaction, including its Inbox receipt, rolls back; an earlier handler's separately committed work is not undone. If an invalid business document must be saved or an alarm raised, persist it as a handled outcome and return normally so its transaction and Inbox receipt commit. Local EventBus calls still propagate exceptions to their caller; publishing and Outbox send retries are unchanged.

Behavioral checks: [shared retry budget](../../../tests/Test.Monica.EventBus/Services/EventMessageRetryPolicyTests.cs), [Dapr responses, deadlines and dead letters](../../../tests/Test.Monica.Dapr/Services/DaprEventBusSubscriptionHostedServiceTests.cs), [Kafka retries, cancellation and acknowledgment](../../../tests/Test.Monica.EventBus.Kafka/Providers/ConfluentKafka/KafkaEventBusSubscriptionHostedServiceTests.cs), and [Inbox rollback across fresh retry scopes](../../../tests/Test.Monica.Repository/UnitOfWork/InboxExecutionTests.cs).

For registered-handler inspection, independent transport health, live change-feed ownership, and diagnostic publishing, use [EventBus diagnostics](event-bus-diagnostics.md).

For Kafka broker configuration, routed topic and consumer inspection, sampled evidence, and persisted-snapshot upgrades, use [Kafka console diagnostics](kafka-console.md).
