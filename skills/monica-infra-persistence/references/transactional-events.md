# Transactional events and delivery

Outbox, Inbox and their lease table use the [common infrastructure table layout](infrastructure-table-layout.md). Configure the same policy for all contexts mapping a shared physical store, with one migration owner and explicit exclusions elsewhere. Layout selection alone does not consolidate existing stores. Use `SharedStoreKey` below to consolidate automatic dispatchers and qualify delivery destinations.

Outbox physical-table discovery and native retry-clock reads execute on the context-owned connection, preserving its active transaction and command timeout. These store operations do not require scalar query-root support from a business model's custom query compiler. Verify worker startup and failed-delivery retry on the real sharding/provider composition as well as ordinary entity queries.

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

Entity event projections are optional: `AddEntityEventProjections<TContext>` stages projection events during save and also requires outbox on the primary context. Projection callbacks should be side-effect-free. When several contexts map the same physical Outbox/Inbox tables, choose exactly one migration owner; every other context sets `ExcludeFromMigrations = true` on its outbox and inbox options. Keep this ownership consistent in runtime and migration composition. The owner need not be the primary context of every runtime transaction; that primary context must still map and enable its selected store. Exclusion does not rewrite old migrations or migrate existing data. `EnsureCreated` on an excluded context cannot create the shared tables; install the owner's migrations first, including in integration fixtures. See [database layout and migrations](../../monica-application/references/05-database-migrations.md) before changing existing ownership or history.

## Shared physical stores

Assign one stable `SharedStoreKey` to every context writing one physical Outbox. This is a host composition identity, independent of CLR context names, process IDs and migration ownership:

```csharp
repository.AddOutbox<OrdersDbContext>(options =>
{
    options.SharedStoreKey = "application-messages";
    options.ExcludeFromMigrations = true;
    options.LeaseTable.ExcludeFromMigrations = true;
});
repository.AddOutbox<BillingDbContext>(options =>
{
    options.SharedStoreKey = "application-messages";
    options.ExcludeFromMigrations = true;
    options.LeaseTable.ExcludeFromMigrations = true;
});
// A separately composed technical context owns the shared tables' migrations.
monica.AddHostedService(options => options.FailFastOnStartupError = true);
```

Automatic contributions with one key produce one delivery loop and one pair of maintenance loops per host. Startup compares provider, endpoint, database, resolved schema/table and delivery/maintenance policies. Conflicting mappings or policies fault the worker; `FailFastOnStartupError = true` also aborts host startup. PostgreSQL/Gauss resolve the actual table schema through the database search path. Use identical endpoint names across contributors; aliases are not inferred. Private SQLite memory connections cannot represent a shared store. The key never grants activation: held/disabled contexts retain staging and explicit dispatcher services without becoming automatic workers. A null key retains context-owned dispatch. Explicit calls to different context dispatchers have separate capacity limits; use one dispatcher for deliberate manual draining of a shared store.

Shared publication captures `DeliveryTarget` with the immutable payload. Distributed targets identify the default or explicitly string-keyed `IEventTransport` registration. Local targets identify the complete registered typed handler set, event contract, topic, bus key and logical owners; subscription runtime IDs and assembly versions are excluded. The producer must contain the complete intended local handler registration set. Shared local publication rejects an absent, ambiguous or delegate-based destination. A local event requiring handlers from different partial artifacts should use explicit distributed consumers with their own Inbox identities.

Before claiming, a shared dispatcher selects only targets available on its host. Unsupported transports, incomplete local sets, inactive handlers and unqualified legacy rows remain pending without incrementing attempts or taking a lease. Typed local handlers are selected again before sending, then dispatched by exact active subscription IDs; removal/deactivation fails delivery rather than acknowledging a subset. Changes to the intended handler set are durable routing changes: drain pending local messages under the old complete set before deployment, or explicitly reconcile them. There is no automatic handler-set rewrite or delegation across hosts.

Every context must map the same message columns and use the same store policy. Add the `DeliveryTarget` column and its eligibility index in the sole migration owner. Quiesce old workers before shared publication: an older binary does not understand qualification. Drain old rows first or explicitly migrate their destinations after verifying the complete local handler set; shared workers intentionally do not infer missing identities. Context-owned workers in this version claim only unqualified rows. Keep Outbox and Inbox as separate tables, and preserve `(Consumer, Source, MessageId)` semantics when consolidating Inbox receipts. Do not reuse one consumer identity for different domain responsibilities merely because their receipts previously lived in separate databases.

`SharedOutboxTests` exercises coalescing, conflicting targets/policies, partial local hosts, delegate rejection, unavailable keyed transports and legacy rows with real hosted SQLite stores. `ExternalSharedOutboxTests` verifies qualified native claims under `infra` on explicitly selected PostgreSQL/Vastbase test servers.

## Outbox delivery and maintenance

The worker runs delivery, retention cleanup, and backlog sampling independently for each registered store, using separate scopes. Delivery runs on every active replica and retains conditional per-message claims; delivery remains at least once. Cleanup and backlog sampling share one renewable maintenance role per physical Outbox database/schema/table through [coordinator leases](../../monica-infra-hosting/references/leases.md). The physical name, not the CLR context name or process ID, defines the role. A standby polls the indexed role record and skips the Outbox maintenance scan. A slow cleanup, status query, or another store's transport does not serialize delivery in the worker; native database locks and capacity still apply. Host shutdown cancels and awaits the loops before releasing the role. Delivery starts immediately; maintenance waits for its first interval. Failed operations retry on their own interval.

PostgreSQL and Gauss-compatible providers claim available rows using one atomic `UPDATE ... RETURNING` with `FOR UPDATE SKIP LOCKED`. Competing replicas skip locked rows and receive only the payloads they claimed. Eligibility, expiry and failed-send retry deadlines use database UTC. Claim expiry rounds upward to the next whole second to accommodate existing table precision; this adds at most one second before a crashed claim becomes eligible again. Other providers retain conditional per-row updates, fetching payloads only after winning the claim. Validate the native path against the deployed server version; PostgreSQL compatibility alone is not a concurrency test. See [PostgreSQL locking documentation](https://www.postgresql.org/docs/15/sql-select.html) for the queue-oriented `SKIP LOCKED` contract.

Manual `DrainAsync` calls must run outside an ambient transaction so claims commit before sending. All passes through one dispatcher share `DeliveryConcurrency` slots, reserved before claiming rows. The dispatcher immediately starts claimed sends and replenishes slots as actual operations complete. It never preclaims a large backlog waiting behind a slow transport. A pass-local sequence cursor avoids immediately retrying failed rows in the same pass. The claim context is reused within a bounded pass; its connection is released after each statement. Every send owns an independent scope and DbContext. A timed-out or caller-cancelled send that ignores cancellation retains its slot, scope and cancellation sources until the real operation and scope disposal finish; its eventual exception is observed. Other free slots continue, while a pass with no capacity returns without claiming more rows. Worker polling retries admission later. A permanently stuck transport can exhaust this dispatcher's capacity and requires transport recovery or host restart; starting unlimited replacement sends would violate the bound.

The default concurrency of one retains serial sends across all passes of that dispatcher; higher values overlap local handlers and transport calls, including messages sharing a transport key. No cross-replica or per-key ordering is guaranteed. Size concurrency against handler safety, transport limits and database pool headroom. Transports must return asynchronous tasks promptly and cooperate with cancellation; a synchronous blocking preamble cannot be interrupted by an asynchronous deadline.

The send deadline begins before claiming and reserves up to one second (10% for short leases) for uncertainty. Claims whose reply arrives too late are not sent and expire normally. Cancellation, ambiguous database outcomes and already-started send timeouts leave claims to expire; each claimed row counts as an attempt. A late send completion never starts an acknowledgment after its deadline. Timeout logs are immediate; row-level error recording waits for the real send to finish. Completion and retry updates match both the row and its claim token, so a stale worker cannot overwrite a successor. A transport can ignore cancellation or acknowledge after a connection failure, so delivery remains at least once and Inbox/idempotency remains necessary.

| Option | Default | Behavior |
| --- | --- | --- |
| `SharedStoreKey` | null | Coalesces eligible contributions to one physical store and enables destination qualification when explicitly set |
| `BatchSize` | 100 | Maximum messages claimed per delivery pass, including failed attempts; full successful batches continue immediately |
| `DeliveryConcurrency` | 1 | Maximum simultaneous deliveries per dispatcher across passes, including unresolved timed-out sends; increase explicitly for independent handlers/transports |
| `DeliveryLease` | 5 minutes | Claim lifetime; the send budget includes the claim round trip and a conservative expiry margin |
| `PollInterval` | 5 seconds | Delivery delay after an incomplete batch or scan failure |
| `CleanupBatchSize` | 1,000 | Maximum expired, acknowledged rows deleted by one SQL statement |
| `CleanupInterval` | 1 minute | Delay before the first cleanup and after each cleanup pass |
| `CleanupTimeBudget` | 2 seconds | Elapsed-time budget checked between independently committed cleanup batches |
| `StatusInterval` | 1 minute | Delay before the first backlog query and after each status sample |
| `DeliveredRetention` | 7 days | Time acknowledged rows remain available; pending rows never expire |
| `MaintenanceLease.Duration` | 30 seconds | Independent renewal every 10 seconds by default |
| `MaintenanceLease.StandbyInterval` | 5 seconds | Minimum retry delay for a standby |
| `MaintenanceLease.OperationTimeout` | 5 seconds | Deadline for a lease call, one cleanup transaction, or one status sample; must be less than `Duration / 3` |

Cleanup selects keys inside the `DELETE` statement, ordered by `DeliveredAtUtc`, using the leading column of the `(DeliveredAtUtc, NextAttemptAtUtc, LeaseUntilUtc, Sequence)` index. It does not load message payloads or transfer a key list to the application. With the default SQL authority, each batch validates and locks the maintenance lease inside its deletion transaction. A pass fixes its cutoff once, stops after a partial batch or the time budget, and retains committed batches if a later command fails or cancellation occurs. Cancellation within a batch rolls it back. The pass budget is checked between commits; `MaintenanceLease.OperationTimeout` bounds individual transactions and status calls. Status is an observational read under the term token and does not hold the lease-row lock, so a takeover boundary may overlap a cancelling status query without affecting correctness. The shared `MonicaLeases` table also requires a migration. Set `LeaseTable.ExcludeFromMigrations = true` when another context owns that table and use the same schema mapping. Follow the shared lease cutover instructions before upgrading replicas.

`OutboxDispatcher<TDbContext>.CleanupAsync` participates in the same election. A manual call returns zero when another maintenance owner holds the role, and releases its own role after its fully awaited pass. Worker-owned coordination remains alive between passes; it does not acquire/release on every cleanup batch. Lease reads, renewal and transaction guards add fixed overhead; measure both cold election and retained-row maintenance cost at the deployed replica count.

To coordinate repeat-safe maintenance through Redis, register a keyed lease store and set `MaintenanceLeaseStore = new LeaseStoreSelection("background", "orders-database")`. The scope must identify the physical database; schema/table names still identify the role, so different contexts mapping one table contend. Cleanup and statistics share that external role, while delivery claims stay on Outbox messages. This explicit selection is advisory: cleanup uses one autocommitted bounded DELETE per batch without a SQL lease-row guard. Expired/revoked Redis grants can overlap an in-flight cleanup, but pending/recent messages remain protected by the same predicates. A failed command preserves earlier committed batches; cancellation cannot undo a batch the database already committed. No fallback occurs if Redis is unavailable. The existing common-table model mapping is retained, so selecting Redis does not silently drop a shared table from migrations. Follow the [authority cutover and connection contract](../../monica-infra-hosting/references/leases.md#provider-selection-and-redis) for every replica.

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

Each subscriber's receiving scope is independent of the publisher and other subscribers. The execution pipeline makes that scope current, so nested generated Shared calls participate in its transaction. Resolve defaults from the concrete consumer's domain/role, never from the event namespace or publisher. Compatible added contexts keep the original receipt/Outbox owner. Changing nested business writes to Independent breaks their atomicity with the receipt and must not be used as a generic fix for participant errors. External sends cannot be rolled back with database writes; stage committed delivery intent in Outbox.

Durable EventBus delivery can retry across process restarts without a separate job. Choose JobScheduler only when the work needs the independent execution lifecycle of a job in `$monica-application-project-unit-development` (`references/15-job-template.md`); its [store and admission semantics](../../monica-infra-jobs/references/scheduler.md) own durability of admitted job executions.

Outbox and inbox timestamps represent UTC instants. Their existing `timestamp` columns store UTC clock values without a time-zone marker; the repository converts these properties to `DateTimeKind.Unspecified` for database parameters and restores `DateTimeKind.Utc` on materialization, preserving ticks in both directions. The same mapping applies to delivery leases, retry comparisons, and bulk updates. It requires no column-type migration and does not change business `DateTime` mappings. Do not convert these values to local time or enable a global legacy timestamp switch to resolve PostgreSQL/GaussDB UTC parameter errors.

Before-commit effects: `Monica.Repository/UnitOfWork/Abstractions/IDomainEventQueue.cs`, `Services/DomainEventQueue.cs`, and `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkExecutionBehaviorTests.cs` establish same-scope draining and rollback. Outbox module dependencies: `Monica.Repository/Modules/ModuleRepository.cs`. Scoped staging and transport checks: `Monica.Repository/Outbox/Services/TransactionalEventSink.cs` and `Monica.EventBus/Services/ScopedEventBusGateway.cs`. Provider registration: `Monica.EventBus/Modules/ModuleEventBus.cs`. Inbox registration and execution: `Monica.Repository/Inbox/Services/InboxRegistration.cs` and `InboxExecutionBehavior.cs`. Test composition: `tests/Test.Monica.Repository/Hosting/RepositoryScenarioFactory.cs`; its base test factory uses the recording transport in `Monica.Testing/Hosting/DefaultSeams.cs`. `tests/Test.Monica.Repository/UnitOfWork/InboxExecutionTests.cs` and `EntityEventPublishingTests.cs` exercise inbox and entity-event behavior. For transport delivery beyond the repository transaction, read the messaging skill's [EventBus reference](../../monica-infra-messaging/references/event-bus.md).
