# Transactional events and delivery

Register the primary repository context and its UnitOfWork [transaction boundary](transactions.md) before configuring durable events.

Choose the event's execution boundary first:

- For domain effects that must complete before the current operation commits, enqueue through `IDomainEventQueue` and register `Monica.Repository.UnitOfWork.Abstractions.IDomainEventHandler<TEvent>` in that operation's scope. Its `HandleAsync` runs in the publisher's scope and transaction; exceptions abort the operation. Keep these effects free of external side effects. This contract is separate from the EventBus handler bases.
- For in-process EventBus reactions, use the local bus and the application local handler template in `$monica-application-project-unit-development` (`references/14-event-handler-template.md`). Being local does not make a reaction a before-commit domain effect.
- For committed notifications delivered across processes, publish an `[Outbox]` EventBus event and consume it with the application distributed handler template in `$monica-application-project-unit-development` (`references/14-event-handler-template.md`); add `[Inbox]` where durable receive deduplication is required.

When creating or refactoring application events and consumers, load the ProjectUnit event contract in `$monica-application-project-unit-development` (`references/12-entity-request-event-template.md`) and naming and placement in `$monica-application-project-unit-development` (`references/01-project-unit-naming-and-boundaries.md`) guidance. Outbox and inbox change delivery and transaction behavior; the application unit conventions still apply.

For `[Outbox]` events, enable `AddOutbox<OrdersDbContext>()` on the **primary transaction context** and generate its EF migration. Inside the host's `builder.AddMonica(monica => { ... })` callback, keep the repository registration for the existing `OrdersDbContext` and add outbox to it:

```csharp
var repository = monica.AddRepository()
    .AddRepositoryDbContext<OrdersDbContext>((_, db) => db.UseSqlite(connectionString));
repository.AddOutbox<OrdersDbContext>();
```

`AddOutbox` requires the EventBus and HostedService modules automatically; the host does not need to register those modules solely to satisfy this dependency. It does **not** choose an EventBus provider. For distributed `[Outbox]` publication, configure `monica.AddEventBus()` with a real distributed transport provider for this host (see the messaging skill's [EventBus reference](../../monica-infra-messaging/references/event-bus.md)); a no-op provider alone cannot publish a durable distributed event. Resolve `IDistributedEventBus`, the repository, and `IUnitOfWorkManager` from the same DI scope, then publish the `[Outbox]` event through that scoped gateway inside the [write operation](transactions.md). Successful `PublishAsync` means the prepared message was staged. The worker sends it only after commit; sends may retry, so consumers still need idempotency. `RepositoryOutboxOptions` controls polling, lease, batch, retry delays, retention, and whether this host runs a worker.

Register `AddOutbox<TDbContext>` once per context; repeated declarations fail before startup instead of choosing the last owner or options. Outbox activation is evaluated separately from each `TDbContext`'s declared role or domain `BackgroundRole`. A role's registration callback cannot claim a different context owner; its captured owner remains diagnostic provenance only. A combined host can dispatch one context while holding another. `Application:AutonomousWork=false` holds every background outbox scan, claim, delivery and cleanup; `EnableWorker=false` also disables that context's worker contribution. Transactional staging and passive dispatcher services remain registered. Manual API calls can commit pending outbox rows into shared storage, where the environment's workers may deliver them. No generic manual Outbox trigger is introduced.

Entity event projections are optional: `AddEntityEventProjections<TContext>` stages projection events during save and also requires outbox on the primary context. Projection callbacks should be side-effect-free. When several contexts share one physical database, exactly one (the transaction owner) keeps default migration ownership of the shared tables; every other context sets `ExcludeFromMigrations = true` on its outbox and inbox options so its migrations and snapshots never touch them. Because `ExcludeFromMigrations` also removes a table from `EnsureCreated`, apply it only in the composition used to generate migrations, never in runtime hosts.

## Outbox delivery and maintenance

The worker runs delivery, retention cleanup, and backlog sampling independently for each registered store, using separate scopes. A slow cleanup, status query, or another store's transport does not serialize delivery. Host shutdown cancels and awaits these loops. Delivery starts immediately; maintenance waits for its first interval. Failed operations retry on their own interval.

| Option | Default | Behavior |
| --- | --- | --- |
| `BatchSize` | 100 | Maximum messages selected per delivery pass; full successful batches continue immediately |
| `PollInterval` | 5 seconds | Delivery delay after an incomplete batch or scan failure |
| `CleanupBatchSize` | 1,000 | Maximum expired, acknowledged rows deleted by one SQL statement |
| `CleanupInterval` | 1 minute | Delay before the first cleanup and after each cleanup pass |
| `CleanupTimeBudget` | 2 seconds | Elapsed-time budget checked between independently committed cleanup batches |
| `StatusInterval` | 1 minute | Delay before the first backlog query and after each status sample |
| `DeliveredRetention` | 7 days | Time acknowledged rows remain available; pending rows never expire |

Cleanup selects keys inside the `DELETE` statement, ordered by `DeliveredAtUtc`, using the leading column of the existing `(DeliveredAtUtc, NextAttemptAtUtc, LeaseUntilUtc, Sequence)` index. It does not load message payloads or transfer a key list to the application. A pass fixes its cutoff once, deletes successive batches until a partial batch or the time budget, and retains completed batches if a later command fails or cancellation occurs. The budget does not cancel an in-flight command: use the provider's command timeout to bound individual SQL operations. This query change requires no new columns or indexes on an already migrated Outbox table; check the actual provider execution plan if cleanup remains expensive.

Size retention from diagnostic needs and event volume: roughly daily delivered volume multiplied by retention days remains in the table even when cleanup keeps up. For a host that needs only one day of delivered-message history, configure the existing registration explicitly:

```csharp
repository.AddOutbox<OrdersDbContext>(options =>
{
    options.DeliveredRetention = TimeSpan.FromDays(1);
    options.CleanupBatchSize = 1000;
    options.CleanupInterval = TimeSpan.FromMinutes(1);
    options.CleanupTimeBudget = TimeSpan.FromSeconds(2);
});
```

Reducing retention makes previously retained acknowledgments eligible for deletion. Adjust cleanup batch size and budget to the database's write capacity; changing delivery `BatchSize` or `PollInterval` no longer tunes retention throughput. `tests/Test.Monica.Repository/Outbox/OutboxCleanupTests.cs` protects retention boundaries, bounded server-side batches, the SQLite index access path, and partial progress. `OutboxWorkerTests.cs` covers independent maintenance cadence, continuous delivery, failure recovery, and shutdown.

## Inbox and timestamp behavior

For an EventBus handler with durable deduplication, enable `AddInbox<OrdersDbContext>()`, migrate `MonicaInbox`, and mark the handler class or method `[Inbox("stable.consumer.name")]`. The receipt, business writes, and outgoing events share the primary context's transaction. Keep the consumer name stable across deployments. Missing message identity or missing active inbox transaction fails; duplicate delivery for the same consumer/source/message ID is suppressed. Concurrent contenders may surface a database conflict; let the transport redeliver into a fresh scope.

Durable EventBus delivery can retry across process restarts without a separate job. Choose JobScheduler only when the work needs the independent execution lifecycle of a job in `$monica-application-project-unit-development` (`references/15-job-template.md`); its [store and admission semantics](../../monica-infra-jobs/references/scheduler.md) own durability of admitted job executions.

Outbox and inbox timestamps represent UTC instants. Their existing `timestamp` columns store UTC clock values without a time-zone marker; the repository converts these properties to `DateTimeKind.Unspecified` for database parameters and restores `DateTimeKind.Utc` on materialization, preserving ticks in both directions. The same mapping applies to delivery leases, retry comparisons, and bulk updates. It requires no column-type migration and does not change business `DateTime` mappings. Do not convert these values to local time or enable a global legacy timestamp switch to resolve PostgreSQL/GaussDB UTC parameter errors.

Before-commit effects: `Monica.Repository/UnitOfWork/Abstractions/IDomainEventQueue.cs`, `Services/DomainEventQueue.cs`, and `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkExecutionBehaviorTests.cs` establish same-scope draining and rollback. Outbox module dependencies: `Monica.Repository/Modules/ModuleRepository.cs`. Scoped staging and transport checks: `Monica.Repository/Outbox/Services/TransactionalEventSink.cs` and `Monica.EventBus/Services/ScopedEventBusGateway.cs`. Provider registration: `Monica.EventBus/Modules/ModuleEventBus.cs`. Inbox registration and execution: `Monica.Repository/Inbox/Services/InboxRegistration.cs` and `InboxExecutionBehavior.cs`. Test composition: `tests/Test.Monica.Repository/Hosting/RepositoryScenarioFactory.cs`; its base test factory uses the recording transport in `Monica.Testing/Hosting/DefaultSeams.cs`. `tests/Test.Monica.Repository/UnitOfWork/InboxExecutionTests.cs` and `EntityEventPublishingTests.cs` exercise inbox and entity-event behavior. For transport delivery beyond the repository transaction, read the messaging skill's [EventBus reference](../../monica-infra-messaging/references/event-bus.md).
