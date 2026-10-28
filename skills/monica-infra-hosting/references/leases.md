# Shared coordinator leases

Use coordinator leases for a bounded set of roles, such as one scheduler owner or one physical Outbox table's maintenance. `Monica.Locker.Leases` owns the common protocol and `LeaseCoordinator`; `Monica.Locker.EfCore` supplies the relational implementation and `Monica.Locker.Redis` supplies the StackExchange.Redis implementation. Neither provider depends on Repository; Redis leases also have no StateStore dependency. These types are explicitly bound to a store; they do not require `AddLocker()` or choose a global database through DI.

Keep execution claims in their business records. Job execution tokens remain on executions, and Outbox delivery claims remain on messages. Moving those high-volume claims into this table adds another write and another transaction boundary. The existing [Locker handle API](lockers.md) retains provider-specific semantics; its handles are not automatically renewable or transaction-fenced. ServiceDiscovery uses this same protocol and runtime through an explicitly selected authority; sharing a protocol does not make independent backends one election.

## Contracts and ownership

`IExclusiveLeaseStore` exposes `TryAcquireAsync`, `TryRenewAsync`, `TryReleaseAsync`, and a read-only `GetSnapshotAsync`. A `LeaseResource` is an immutable `(Scope, Name)` within one configured backend/database/schema. Each successful acquisition creates a fresh opaque `TermId`, even for the same holder after expiration. Renewal accepts only the exact, unexpired identity and never reacquires; `null` means unavailable/lost, while transport and ambiguous commit failures propagate. Release compares the complete identity. Tokens are equality guards, not increasing fencing numbers for external systems.

Use `GetSnapshotAsync(resource, cancellationToken)` when the resource is known. It returns the authority's observation time and zero or one retained observation without enumerating unrelated roles. Redis uses one keyed Lua request, SQL an equality lookup on the composite primary key, and memory a dictionary lookup. A retained SQL/memory observation may be expired; compare its expiry with `ObservedAtUtc`. Missing Redis keys still return Redis observation time. The scope/prefix overload is an inventory operation: Redis completes a database-wide `SCAN`, even when few matching roles exist. Reserve it for low-frequency diagnostics. Custom store implementations must implement the exact overload without falling back to inventory enumeration.

`LeaseCoordinator.GetAsync` caches a usable `LeaseContext`. One coordinator belongs to one worker lifetime. Renewal runs independently of business callbacks, and a context's cancellation token is never reused for a later term. The deadline starts before the request, uses the returned validity duration, and subtracts a margin of up to one second. Monotonic elapsed time prevents backward wall-clock changes from extending validity; forward wall-clock jumps shorten it conservatively. A failed/stalled renewal retires the term. An unresolved provider call is observed and prevents more calls accumulating for that resource; a late result never revives a retired context.

| `LeaseCoordinatorOptions` | Default | Constraint |
| --- | --- | --- |
| `Duration` | 30 seconds | Positive, at most one day; renewal every third of this interval |
| `StandbyInterval` | 5 seconds | Positive minimum interval after an unsuccessful acquisition |
| `OperationTimeout` | 5 seconds | Positive and strictly below one third of `Duration`; also bounds runtime drain |

Use the context token in protected work, and drain that work before `DisposeAsync` releases the role. Synchronous `Dispose` stops supervision but leaves the record to expire. Cancellation does not undo a remote send or fence arbitrary external writes. For SQL writes, `RelationalLeaseStore<TContext>.EnsureHeldAsync` must run inside the caller's active transaction, before business-row locks, against the same authority and mapping. It holds the lease row until commit/rollback and never renews or commits. Keep the whole transaction bounded and keep external network calls outside it.

Asynchronous disposal shares one real-clock `OperationTimeout` budget across all resources, independently of an injected workload clock. Resources drain and release concurrently; a stalled or failing resource does not skip healthy roles. Uncertain grants expire naturally. Concurrent and repeated `DisposeAsync` calls await the same cleanup task and observe the same failure. When an owner has multiple coordinators, stop them all before awaiting any cleanup so one unavailable authority cannot keep another renewing.

```csharp
using Monica.Locker.EfCore;
using Monica.Locker.Leases;

// contextFactory targets the protected database; its model maps ConfigureMonicaLeases().
var store = new RelationalLeaseStore<MaintenanceDbContext>(contextFactory);
await using var coordinator = new LeaseCoordinator(store, instanceId, TimeProvider.System, logger);
var role = new LeaseResource("billing", "reconciliation");
var context = await coordinator.GetAsync(role, stoppingToken);
if (context is not null)
{
    using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, context.StoppingToken);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
    deadline.CancelAfter(TimeSpan.FromSeconds(5));
    await using var db = await contextFactory.CreateDbContextAsync(deadline.Token);
    db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));
    await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
    await store.EnsureHeldAsync(db, context.Identity, deadline.Token);
    // Perform and await the bounded database mutation here, using deadline.Token.
    await transaction.CommitAsync(deadline.Token);
}
```

Retain the coordinator around the worker loop rather than constructing one on every tick. The example illustrates a single fully awaited operation and its disposal boundary.

## Coordinator metrics

Pass the host's `System.Diagnostics.Metrics.IMeterFactory` to `LeaseCoordinator` to emit standard .NET metrics. The built-in JobScheduler, Outbox maintenance and ServiceDiscovery workers pass their host factory when it is registered. Generic Host and ASP.NET Core supply that factory; a bare `ServiceCollection` needs `services.AddMetrics()` if instrumentation is wanted. A manually constructed coordinator must receive the factory explicitly, for example with an injected `meterFactory`:

```csharp
await using var coordinator = new LeaseCoordinator(
    store, instanceId, TimeProvider.System, logger,
    meterFactory: meterFactory);
```

Omitting the factory disables instrumentation. Coordinators sharing one factory share the meter and instruments; the host owns their lifetime, and disposing a coordinator never disposes the factory or meter. No exporter, polling service or Locker module registration is required to emit measurements. Choose collection/export separately through [OpenTelemetry metrics](../../monica-infra-observability/references/opentelemetry-metrics.md).

The [coordinator metric contract](../../monica-infra-observability/references/metrics.md#coordinator-lease-metrics) defines operation outcomes, durations and one-time term retirement. These measurements cover role coordination, including maintenance and leadership. Per-message Outbox delivery claims and job execution claims retain their own business-record protocols and are not counted by these instruments. Metrics and snapshots cannot establish current ownership or replace transaction fencing.

## Provider selection and Redis

Use keyed singleton `IExclusiveLeaseStore` registrations when a host needs several authorities. `LeaseStoreSelection(serviceKey, scope)` selects the registration and a stable authority namespace. Consumers resolve the key once during composition, then use the bound store. Do not resolve providers per iteration or select a global lease backend that silently replaces scheduler transaction fencing.

```csharp
using Monica.Locker.Leases;
using Monica.Locker.Redis;

// Reuse an existing singleton IConnectionMultiplexer, for example the connection registered by
// UseRedisStateStoreProvider. The host owns the connection; the lease store never disposes it.
builder.Services.AddRedisLeaseStore("background", new RedisLeaseStoreOptions
{
    KeyPrefix = "production",
    Database = 0
});
// A keyed connection is selected with the optional connectionServiceKey argument.

builder.AddMonica(monica =>
{
    monica.AddServiceDiscovery()
        .AsRegistry()
        .UseDistributedStorage()
        .UseLeaderLeaseStore("background", "service-registry");
    monica.AddJobScheduler(options =>
        options.HistoryCleanupLeaseStore = new("background", "orders-database"));
    monica.AddRepository().AddOutbox<OrdersDbContext>(options =>
        options.MaintenanceLeaseStore = new("background", "orders-database"));
    // Configure the membership StateStore, scheduler store and repository DbContext in this same host.
});
```

`KeyPrefix` is required and encoded literally, including glob/hash-tag characters; `Database` defaults to zero (required by Redis Cluster). `AddRedisLeaseStore` rejects duplicate lease keys, uses the existing unkeyed multiplexer unless a connection key is supplied, and fails when that connection is missing. It neither creates a connection nor silently falls back to memory. An independent host can register its own multiplexer or reuse the connection factory documented in [state stores](state-stores.md).

Use a stable logical database/deployment identity as the consumer scope. Independent databases sharing Redis must have different scopes; aliases of the same physical Outbox table must share one scope. Never derive this namespace from a connection string, CLR context name, process ID or randomly generated host identity. Changing authority, scope or prefix requires stopping/draining old participants and switching every replica together: mixed selections can each elect a holder.

| Consumer | Default | External keyed selection |
| --- | --- | --- |
| Scheduler owner scheduling and execution recovery | Scheduler store, validated inside each protected transaction | Never replaced by history cleanup selection |
| Scheduler terminal-history cleanup | Scheduler store's maintenance role | `HistoryCleanupLeaseStore`; separate `jobscheduler:history:<scheduler scope>` role, advisory coordination |
| Outbox delivery | Per-message database claims | Unchanged |
| Outbox cleanup and statistics | One SQL role per physical table | `MaintenanceLeaseStore`; advisory Redis/other authority, no SQL lease-row check |
| ServiceDiscovery leadership | Local memory only with memory membership | `UseLeaderLeaseStore`; required with distributed/external membership |

Redis acquisition, renewal and conditional release each use one single-key Lua request. Acquisition creates a random term; renewal compares holder/term and positive TTL, and never recreates an expired key. Redis server time supplies confirmations and observation timestamps. Lost/ambiguous responses propagate; caller cancellation cannot undo a command already sent. Keep connection command timeouts bounded as well as `LeaseCoordinatorOptions.OperationTimeout`. Observation uses explicit paged `SCAN` on primaries, never a `KEYS` fallback; snapshots are low-cardinality diagnostics, not atomic cluster-wide membership or permission to write.

Redis asynchronous replication can lose an acknowledged grant on failover. Unique terms prevent stale renewal/release within the authority but cannot prevent every duplicate leader across failover. Cleanup predicates, terminal-state checks and idempotency make advisory maintenance safe to repeat. Never replace a database transaction guard with a Redis check followed by a database write. Leadership callbacks with irreversible external effects need resource-side fencing/idempotency or a stronger authority. See the [Redis lock contract](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/) and [replication limitations](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/).

`ILeaseAdministration` is an optional operator capability used by existing discovery management actions. `RevokeAsync` deliberately invalidates the current resource without its holder's token; the caller owns authorization, and old work only observes loss at renewal/deadline. It is never a worker shutdown or normal release API. Providers without this capability reject forced revocation explicitly.

## Relational table and deployment

Call `modelBuilder.ConfigureMonicaLeases(options)` in each participating context. The fixed table name is `MonicaLeases`, with a composite primary key `(Scope, Name)`, holder and term strings, and acquisition/confirmation/expiration UTC ticks. Renewal changes only non-indexed columns. There is no expiration index or per-message record. Snapshots expose holder and timestamps, omit term tokens, and are diagnostic observations rather than authority to mutate state. The optional [lease workbench](lease-workbench.md) exposes explicitly bound authorities without selecting or changing consumer stores.

PostgreSQL and GaussDB-compatible providers use native row locks and database time. Time is sampled **after** a row-lock wait so renewal cannot revive an expired grant. Active standbys make one indexed read without a transaction; independent roles lock different rows. Each operation owns and disposes a fresh context, so holding a grant holds no connection. Only known transaction conflicts and precisely recognized lease-key insert races are retried. SQLite uses serializable transactions and a process clock for local/fixture use; it does not establish the production multi-replica contract. Other providers fail explicitly. Verify native concurrency on the actual deployed server/provider combination.

Exactly one context per database/schema must own the table's migration. Set `LeaseTableOptions.ExcludeFromMigrations = true` in other participants, keeping `Schema` and the actual resolved table identical. JobScheduler exposes this through `ModuleJobSchedulerEfCoreOption.LeaseTable`; Outbox through `RepositoryOutboxOptions.LeaseTable`. Their models map the table automatically. A host can, for example, let JobScheduler own the table and configure Outbox:

```csharp
repository.AddOutbox<OrdersDbContext>(options =>
{
    options.LeaseTable.ExcludeFromMigrations = true;
    // Set the same explicit Schema on all participants when the provider default is unsuitable.
});
```

Scaffold and apply the owning EF migration before starting workers. No table is created implicitly at startup. Upgrading from `JobSchedulerCoordinators` requires a coordinated cutover: stop/drain old scheduler and Outbox workers, apply the common-table migration, then start the new workers. Mixed old/new scheduler binaries elect through different tables and must not run together. Grants are temporary and need no data copy; remove the old table through the reviewed migration. Business execution and Outbox data stay in their existing tables.

Redis contract tests live in `tests/Test.Monica.Locker.Redis`; discovery term/initialization and two-host Redis tests live in `tests/Test.Monica.ServiceDiscovery`. Native SQL/Redis comparisons and safe overlapping cleanup are in `ExternalLeaseProviderPerformanceTests.cs` and `ExternalOutboxLeaseTests.cs`. Behavior evidence: `tests/Test.Monica.Locker/Leases/LeaseCoordinatorTests.cs`, native race/expiry tests in `ExternalSharedLeaseTests.cs`, and native delivery/maintenance integration in `ExternalOutboxLeaseTests.cs` under `tests/Test.Monica.JobScheduler/Stores/External`. The opt-in performance workloads use disposable databases configured through the [test-suite instructions](../../../tests/README.md); they never read application connection settings.
