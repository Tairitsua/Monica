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

## Manual delivery and held subscriptions

`Application:AutonomousWork=false` retains eligible distributed subscriptions as `Held`. Ineligible owners remain visible as `Disabled`; passive local subscriptions remain active. Both discovery and explicit subscription registration use the frozen activation policy. Neither direct subscription activation nor the registry activation methods can attach a held or disabled subscription to a transport. Kafka and Dapr subscription workers do not start in Manual mode.

For a bounded local test, select exact registry IDs and call `IEventReceiveDispatcher.DispatchTestAsync(message, selectedIds, cancellationToken)`. Create a fresh message with `IEventMessageFactory.Prepare` for each new delivery; reuse the original message only for intentional replay. The complete nonempty, distinct selection must exist, be Active or Held, and match the message's topic, key, scope and event type before any handler runs. This dispatch uses the ordinary scoped invoker, transaction and Inbox pipeline. It neither publishes its input to a broker nor activates subscriptions or advances offsets. Handler business writes and outgoing events remain real; Inbox deduplication still applies to replayed message IDs.

Native resource registrations can supply `EventSubscriptionDescriptor.RegistrationOwner` for shared implementations. Automatic discovery preserves the finalized handler service-registration owner without constructing the handler; conflicting owners, including mixed owned and unowned registrations, fail composition. The policy validates this against concrete handler ownership and uses the same owner precedence for activation and durable workload identity. Explicit `WorkloadOwner` and `EventSubscriptionOwnerAttribute` retain their migration-identity precedence; neither grants activation permission.

## Native Kafka setup and consumer ownership

Select the native provider in the host registration. This example deliberately reads a new development namespace from its earliest retained record; production cutover provisions exact offsets instead.

```csharp
using Confluent.Kafka;
using Monica.Core.Modularity.Extensions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Kafka.Models;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<OrderApprovedReceiver>();
builder.AddMonica(monica =>
{
    monica.AddEventBusKafka(options =>
    {
        options.ConsumerGroupId = "orders.site-a-dev";
        options.AutoOffsetReset = AutoOffsetReset.Earliest;
    }).UseKafkaProvider(new KafkaClusterConfig
    {
        ClusterId = "orders",
        BootstrapServers = "localhost:9092"
    });
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();

[EventName("orders.approved.v1")]
public sealed record DistributedOrderApproved(Guid OrderId);

[EventSubscriptionOwner("orders.notifications")]
public sealed class OrderApprovedReceiver(ILogger<OrderApprovedReceiver> logger)
    : IDistributedEventHandler<DistributedOrderApproved>
{
    public Task HandleEventAsync(DistributedOrderApproved message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Approved order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
```

`EventSubscriptionDescriptor.WorkloadOwner` identifies the logical receiver, independently of host AppId, process instance, keyed bus, and Inbox consumer identity. For a typed distributed subscription, the registry resolves the owner in this order: an explicit descriptor value, an inherited `[EventSubscriptionOwner("...")]` on the handler, then `ITypeActivationPolicy.GetWorkloadOwner(handlerType)`. A missing owner remains null. This applies both to discovered handlers and `SubscribeAsync<TEvent,THandler>`. Local subscriptions do not infer an owner. An owner annotation supplies identity; it does not register or activate a handler. Automatic subscriptions still require container registration and pass the external-role activation policy. Explicit subscription callers own their admission and lifetime.

The native worker creates one consumer per `(topic, workload owner)` within each bus instance. A delivery invokes only that owner's matching active handlers, including when several owners share a topic in a combined host. Replicas of the same owner and bus compete within the same Kafka group; different owners each receive their own copy. Several handlers within one owner share its delivery and acknowledgment. Keep the same stable owner and group prefix when moving roles between combined and split hosts. Preserve existing `[Inbox("...")]` values: they remain handler deduplication identities.

`ConsumerGroupId` is a prefix, `monica-eventbus` by default. The exact group construction is:

| Receiver | Effective group |
| --- | --- |
| Unowned, default bus | `<trimmed-prefix>.default` |
| Owned, default bus | `<trimmed-prefix>.owner.<encoded-owner>` |
| Either receiver on a keyed bus | Append `.bus.<encoded-service-key>` to the corresponding group above |

Each owner and bus segment uses `Uri.EscapeDataString`, then replaces every remaining `.` with `%2E`; the prefix is trimmed but not encoded. For example, prefix `orders.site-a-prod`, owner `orders.notifications`, and bus key `audit` produce `orders.site-a-prod.owner.orders%2Enotifications.bus.audit`. The null-owner default differs from an owner literally named `default`. A null or whitespace bus key selects the default bus; supplied owner values must be nonempty. Unowned infrastructure subscriptions share the configured prefix's default group; choose that prefix according to their intended consumption boundary instead of assuming per-AppId broadcasting.

Topic runtime health aggregates active owner groups. A healthy owner cannot hide another owner's failure or recovery, and removing one owner does not mark another owner's live subscription stopped. Kafka assignment, revocation, and loss reports identify the effective group; the overview remains a topic-level aggregate.

## Kafka delivery, limits, and lifecycle

The native producer sends the captured UTF-8 business JSON as the Kafka value. It supplies required `monica-message-id`, `monica-source`, and `monica-event-name` headers, plus optional `traceparent` and `tracestate`. The receiver requires the three native headers and preserves their identity. A missing header, null value, unknown receiver, removed target, handler failure, or cancellation fails delivery without committing the record. Event name, exact event type, topic, owner, and bus must all match the subscription.

The producer enables idempotence and requests `Acks.All`; successful ordinary publication means Kafka acknowledged delivery, not that any business handler completed. Producer idempotence does not deduplicate a later Outbox replay or a new producer session. Cancellation may end the caller's wait after the native send was queued, while that send still completes. Outbox retries therefore retain the original message ID/source, and durable consumers still need Inbox or another verified idempotency boundary.

Automatic consumer offset commits and offset storage are disabled. The worker awaits all targeted handler operations, including their transaction completion, checks cancellation, and only then commits the record's next offset. A processing or transient commit failure rewinds the original record before retrying. Lost partition ownership is handled without seeking a partition that moved elsewhere; the worker recreates an unsafe consumer and resumes from committed offsets. A crash after business commit but before Kafka acknowledgment can redeliver the same message. Inbox must cover the intended transactional effects; unprotected external side effects can repeat.

The current message factory sets the transport key to the event contract's CLR name; a prepared message may supply its own `TransportKey`, and the native provider falls back to the declared event name when absent. Ordering is per Kafka partition. Do not infer an aggregate-ID partitioning policy from the business DTO.

These defaults belong to `ModuleEventBusKafkaOption`:

| Option | Default and effect |
| --- | --- |
| `ClientId` | `monica-eventbus-kafka`; a cluster's explicit client ID takes precedence |
| `AutoOffsetReset` | `Error`; an absent or unusable committed partition offset needs explicit provisioning or a deliberate `Earliest`/`Latest` policy for a new environment |
| `ProducerMessageMaxBytes` | `1_000_000` bytes per producer protocol request; align broker/topic limits and allow protocol overhead |
| `ProducerDeliveryTimeout` | 2 minutes including native retries; keep it below the Outbox delivery lease |
| `ConsumerFetchMessageMaxBytes` | 1 MiB initial per-partition fetch size; librdkafka can increase it to read an existing larger batch |
| `ConsumerFetchMaxBytes` | 50 MiB fetch-response target; the first batch can exceed it to preserve progress |
| `ConsumerMaxPollInterval` | 5 minutes; allow the complete handler transaction time and at least the session timeout |
| `ConsumerSessionTimeout` | 45 seconds; the broker's group limits must admit it |
| `ConsumerErrorBackoff` | 5 seconds between consumer retry attempts |
| `AdminRequestTimeout` | 10 seconds; also supplies native socket timeout, normalized to at least one second |
| `ConnectionSetupTimeout` | 5 seconds, normalized to at least one second and capped by the effective admin request timeout |
| `ProducerFlushTimeout` | 10 seconds maximum producer flush wait during disposal |

Producer delivery and consumer session/poll timeouts must be between one second and `Int32.MaxValue` milliseconds; message/fetch sizes must be positive. Valid existing committed offsets take precedence over `AutoOffsetReset`. Increasing client sizes does not increase broker/topic limits. Supply broker addresses, security protocol, SASL credentials/mechanism, and CA path through the selected `KafkaClusterConfig` as required by the deployment.

The provider owns its lazy producer and consumer tasks. Producer disposal blocks new queue admission, flushes for at most `ProducerFlushTimeout`, then disposes the native handle. Subscription removal and host shutdown cancel and await their consumers; handlers must pass cancellation to I/O so draining can finish. Consumers deliberately dispose without `Consumer.Close()` to avoid the native leave-group crash path; takeover can wait for the broker's session timeout instead of an immediate graceful leave. No unprocessed or canceled delivery is acknowledged to accelerate shutdown.

## Switching an existing Dapr Kafka deployment

Dapr publication writes a CloudEvent envelope; native Kafka publication uses raw business JSON with Monica headers. The native receiver does not convert or accept old Dapr envelopes. Use a controlled stop-write and drain window:

1. Inventory topics, logical receivers, old groups, new owner/bus groups, and pending Outbox records. Pause related business publication and let the old provider drain committed Outbox rows and Kafka consumption.
2. Verify drain results, then stop old consumers. Record each topic/partition's committed **next** offset and end offset for the relevant logical receiver. Retain pending rows and their stable identities if draining cannot complete; postpone cutover instead of discarding or rewriting them.
3. While the new groups are stopped, explicitly provision the recorded handoff offsets for every required owner/bus group. The default `Error` policy exposes an uninitialized partition; `Earliest` or `Latest` must not choose the production cutover position.
4. Start native consumers, confirm their actual groups, assignments, and offsets, then enable native publication and resume business writes. Compare Outbox backlog, group lag, Inbox receipts, and business results.

An old envelope left behind is rejected and retried without acknowledgment. Rollback also needs a controlled pause, format verification, and an explicit offset handoff; flipping the provider setting alone does not make the two wire formats interchangeable.

Source and checks: [EventBus API](../../../Monica.EventBus/Abstractions/IEventBus.cs), [subscription ownership](../../../Monica.EventBus/Services/EventSubscriptionRegistry.cs), [owner annotation](../../../Monica.EventBus/Annotations/EventSubscriptionOwnerAttribute.cs), [receive dispatcher](../../../Monica.EventBus/Services/EventReceiveDispatcher.cs), [Kafka registration and options](../../../Monica.EventBus.Kafka/Modules/ModuleEventBusKafka.cs), [Kafka client configuration](../../../Monica.EventBus.Kafka/Services/Support/KafkaClientConfigFactory.cs), [native producer](../../../Monica.EventBus.Kafka/Providers/ConfluentKafka/KafkaEventBusProvider.cs), [native consumers](../../../Monica.EventBus.Kafka/Providers/ConfluentKafka/KafkaEventBusSubscriptionHostedService.cs), and [Dapr producer](../../../Monica.Dapr/Services/DaprEventBusProvider.cs). Behavioral checks: [discovery lifecycle](../../../tests/Test.Monica.EventBus/Services/EventBusAutoDiscoveryLifecycleTests.cs), [owner routing and lifecycle](../../../tests/Test.Monica.EventBus/Services/EventReceiveDispatcherTests.cs), [module composition](../../../tests/Test.Monica.EventBus/Modules/ModuleEventBusCompositionTests.cs), [Outbox gateway](../../../tests/Test.Monica.EventBus/Modules/OutboxGatewayTests.cs), [Kafka configuration](../../../tests/Test.Monica.EventBus.Kafka/Services/Support/KafkaClientConfigFactoryTests.cs), [producer ownership](../../../tests/Test.Monica.EventBus.Kafka/Providers/ConfluentKafka/KafkaEventBusProviderTests.cs), and [consumer acknowledgment/lifecycle](../../../tests/Test.Monica.EventBus.Kafka/Providers/ConfluentKafka/KafkaEventBusSubscriptionHostedServiceTests.cs).

For registered-handler inspection, independent transport health, live change-feed ownership, and diagnostic publishing, use [EventBus diagnostics](event-bus-diagnostics.md).

For Kafka broker configuration, routed topic and consumer inspection, sampled evidence, and persisted-snapshot upgrades, use [Kafka console diagnostics](kafka-console.md).
