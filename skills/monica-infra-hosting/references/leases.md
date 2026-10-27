# Shared coordinator leases

Use coordinator leases for a bounded set of roles, such as one scheduler owner or one physical Outbox table's maintenance. `Monica.Locker.Leases` owns the common protocol and `LeaseCoordinator`; `Monica.Locker.EfCore` supplies the relational implementation. The provider depends on EF Core and Locker, not Repository. These types are explicitly bound to a store; they do not require `AddLocker()` or choose a global database through DI.

Keep execution claims in their business records. Job execution tokens remain on executions, and Outbox delivery claims remain on messages. Moving those high-volume claims into this table adds another write and another transaction boundary. The existing [Locker handle API](lockers.md) retains provider-specific semantics; its handles are not automatically renewable or transaction-fenced. ServiceDiscovery registry election still uses its existing state-store implementation and does not join this SQL authority.

## Contracts and ownership

`IExclusiveLeaseStore` exposes `TryAcquireAsync`, `TryRenewAsync`, `TryReleaseAsync`, and a read-only `GetSnapshotAsync`. A `LeaseResource` is an immutable `(Scope, Name)` within one configured backend/database/schema. Each successful acquisition creates a fresh opaque `TermId`, even for the same holder after expiration. Renewal accepts only the exact, unexpired identity and never reacquires; `null` means unavailable/lost, while transport and ambiguous commit failures propagate. Release compares the complete identity. Tokens are equality guards, not increasing fencing numbers for external systems.

`LeaseCoordinator.GetAsync` caches a usable `LeaseContext`. One coordinator belongs to one worker lifetime. Renewal runs independently of business callbacks, and a context's cancellation token is never reused for a later term. The deadline starts before the request, uses the returned validity duration, and subtracts a margin of up to one second. Monotonic elapsed time prevents backward wall-clock changes from extending validity; forward wall-clock jumps shorten it conservatively. A failed/stalled renewal retires the term. An unresolved provider call is observed and prevents more calls accumulating for that resource; a late result never revives a retired context.

| `LeaseCoordinatorOptions` | Default | Constraint |
| --- | --- | --- |
| `Duration` | 30 seconds | Positive, at most one day; renewal every third of this interval |
| `StandbyInterval` | 5 seconds | Positive minimum interval after an unsuccessful acquisition |
| `OperationTimeout` | 5 seconds | Positive and strictly below one third of `Duration`; also bounds runtime drain |

Use the context token in protected work, and drain that work before `DisposeAsync` releases the role. Synchronous `Dispose` stops supervision but leaves the record to expire. Cancellation does not undo a remote send or fence arbitrary external writes. For SQL writes, `RelationalLeaseStore<TContext>.EnsureHeldAsync` must run inside the caller's active transaction, before business-row locks, against the same authority and mapping. It holds the lease row until commit/rollback and never renews or commits. Keep the whole transaction bounded and keep external network calls outside it.

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

## Relational table and deployment

Call `modelBuilder.ConfigureMonicaLeases(options)` in each participating context. The fixed table name is `MonicaLeases`, with a composite primary key `(Scope, Name)`, holder and term strings, and acquisition/confirmation/expiration UTC ticks. Renewal changes only non-indexed columns. There is no expiration index or per-message record. Snapshots expose holder and timestamps, omit term tokens, and are diagnostic observations rather than authority to mutate state. No lease UI is supplied.

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

Behavior evidence: `tests/Test.Monica.Locker/Leases/LeaseCoordinatorTests.cs`, native race/expiry tests in `ExternalSharedLeaseTests.cs`, and native delivery/maintenance integration in `ExternalOutboxLeaseTests.cs` under `tests/Test.Monica.JobScheduler/Stores/External`. The opt-in performance workloads use disposable databases configured through the [test-suite instructions](../../../tests/README.md); they never read application connection settings.
