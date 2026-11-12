# Repository snapshots

Repository snapshots separate a source dataset's transactional revision from its derived read models. Register the dataset once and register each projection separately through `AddSnapshots<TContext>` on Repository. Each dataset names its complete entity dependencies; each projection names its algorithm and format versions. Snapshot access belongs inside an application-owned repository's named materialized read method. Tracked `GetAsync`/`FindAsync` retain their mutation semantics.

This capability requires a relational `RepositoryDbContext`, Monica-owned UnitOfWork transactions for affected writes, a prepared `MonicaSnapshotVersions` table, and explicitly initialized dataset rows. It does not register Outbox messages, Redis, cache notifications or background polling. Required business Outbox events retain their own contracts.

## Version and write ownership

The authority is `(SourceId, DatasetKey, Epoch, Revision)`, stored beside business data. One physical transaction increments each affected dataset once after all saves and before commit, regardless of entity count, flush count, or number of projections. Failed version maintenance fails the transaction. Publication switches for entity events never suppress version capture.

Every writer, including synchronization receivers and hosts without cached readers, must register dataset dependencies. Controlled set-based/native writes declare affected entity types before execution through the context's snapshot-write entry. Ordinary raw SQL, external writers and triggers are not automatically analyzed; cover their complete effects before enabling snapshot readers. Writes outside the managed transaction fail rather than commit data without a revision. Initialization is an explicit deployment operation, never an automatic reader repair.

The infrastructure table uses Monica's native table layout. Exactly one context owns migrations for a shared physical table; other mappings set `ExcludeFromMigrations`. Restoring or cloning data requires a distinct source identity or epoch before reopening cached reads. Do not reset revisions at host startup.

## Register and consume

The following application-owned types illustrate one dataset and one projection. `InventoryDbContext` is an already configured relational `RepositoryDbContext<InventoryDbContext>`; `Product` and `ProductPrice` are mapped entities. Definitions are parameterless, stateless configuration/loader objects, not scoped services.

```csharp
using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Models;

public sealed class ProductMaterial : RepositoryDatasetDefinition<InventoryDbContext>
{
    public override void Configure(RepositoryDatasetBuilder<InventoryDbContext> dataset)
        => dataset.Dataset("inventory.products").DependsOn<Product>().DependsOn<ProductPrice>();
}

public sealed record ProductView(long Id, string Name);
public sealed record ProductCatalog(ImmutableArray<ProductView> Products);

public sealed class ProductCatalogDefinition : RepositorySnapshotDefinition<InventoryDbContext, ProductCatalog>
{
    public override void Configure(RepositorySnapshotBuilder<InventoryDbContext> snapshot)
        => snapshot.Dataset<ProductMaterial>().Projection("catalog", algorithmVersion: 1, formatVersion: 1);

    public override async Task<ProductCatalog> LoadAsync(InventoryDbContext context, CancellationToken token)
        => new((await context.Set<Product>().AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new ProductView(x.Id, x.Name)).ToListAsync(token)).ToImmutableArray());
}
```

Inside the existing `builder.AddMonica(monica => { ... })` composition, after registering that context:

```csharp
monica.AddRepository().AddSnapshots<InventoryDbContext>(options =>
{
    options.SourceId = "production/site-a/inventory"; // Supplied by stable deployment input in production.
    options.AddDataset<ProductMaterial>();
    options.AddProjection<ProductCatalogDefinition>();
});
```

A repository injects `IRepositorySnapshot<ProductCatalog>` and calls `ReadFreshAsync(token)` once in its named materialized read method. Reuse the returned immutable value throughout the calculation. For parameterized projections, derive from `RepositorySnapshotDefinition<TContext,TInput,TSnapshot>` and consume `IRepositorySnapshot<TSnapshot,TInput>`; an immutable record such as `MapInput(DateOnly BusinessDate)` is supported. Override `Normalize` for deterministic validation/canonicalization. The normalized input is serialized, round-tripped, and used for both identity and loading. Mutable properties, collections, custom converters, hidden ignored properties and cyclic input graphs are rejected. Canonical input is limited to 8192 characters.

Generate migrations through the owning application's EF tooling. A separate technical owner can map just the table with `AddSnapshotVersionTable<TContext>()`; business registrations then set `ExcludeFromMigrations = true`. After schema migration, an explicit deployment command resolves `RepositorySnapshotAdministration<TContext>` and calls `InitializeAsync(token)`. This adds only missing rows, never resets revisions or repairs malformed existing rows. Stop or gate all affected writers while initially installing the protocol; enable readers only after every writer maintains versions.

Within a named repository method, wrap set-based changes using the strongly typed context:

```csharp
await context.ExecuteSnapshotWriteAsync([typeof(Product)], ct => context.Set<Product>()
    .Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, name), ct), token);
```

This requires an active managed transaction even when no snapshot registration is present. It marks declared dependencies conservatively, including zero-row updates. A thrown write marks the operation rollback-only. Declare cascade-affected types too. Tracked changes are captured automatically at save; repository callers never manually increment versions. Native writes do not refresh EF's tracked instances. For calculations after writes, pass the updated business data explicitly; when complete transaction-visible data is required, flush through the operation and use a purpose-specific repository read. Do not make snapshot reads silently switch to SQL or merge tracked changes.

`context.HasUncommittedSnapshotChanges(datasetKey)` inspects both staged and already-flushed changes in the active operation; it does not publish them or change snapshot read semantics. Test fixture seeding of a registered dataset must also run through `application.ExecuteAsync`; the ordinary test `SeedAsync` helper does not start this transaction.

## Read and projection ownership

`IRepositorySnapshot<T>` and `IRepositorySnapshot<T,TInput>` are scoped accessors over a host-owned cache. `ReadFreshAsync` validates the primary database on every explicit call. A calculation obtains a snapshot once and reuses it. `ReadBoundedAsync` requires an explicit positive maximum validation age, measured from the start of authority validation and checked when returning the result. Slow validation can itself exceed the budget and cause a failure, including for a newly built result; storage expiration and failed validation never extend that age. Neither read mode substitutes for write-side concurrency control.

Projection cache identity includes the complete source version, projection name, algorithm/format versions and canonical input. Increment code versions when query, transformation or format behavior changes. Include business time, coordinate system, mutable rule versions and every other result-affecting input. Date-only inputs are valid only when results are constant throughout that business day. Input normalization and loading must use the same immutable values.

Loaders use independent short-lived contexts and produce deeply immutable models, never tracked entities, IQueryable or deferred enumerables. Version reads before and after construction detect races. A same-operation read after relevant staged or flushed changes is rejected; use the operation's tracked state or an explicit current-state repository method. Multiple projections needed as one coherent result must share the same source version and be revalidated as a group.

Identical builds are merged per host and full cache identity. One canceled waiter does not cancel work needed by others. Host shutdown owns cancellation and disposal. Loaders must honor cancellation. Entry count, queued work, parallel builds, construction timeout and retry budgets are explicit resource limits; entry-count limits do not estimate object bytes. Measure retained object size and peak old/new construction memory before enabling large datasets.

## Defaults and diagnosis

| Option | Default and scope |
| --- | --- |
| `SourceId` | Required, nonblank, at most 200 characters; dataset/projection names have the same length limit |
| `ExcludeFromMigrations` | `false`; exactly one physical-table owner retains this value |
| `MaximumCachedEntries` | 128 projection/input variants per context/source; least recently used entries are evicted |
| `MaximumConcurrentBuilds` | 4 active loaders per context/source |
| `MaximumPendingBuilds` | 64 total active/queued identities; excess requests fail |
| `BuildTimeout` | 30 seconds, including queue time; cooperative cancellation, maximum one day |
| `MaximumBuildAttempts` | 3 attempts when concurrent source changes invalidate construction |

All numeric capacities must be positive. Source/dependency registration and names are frozen at composition. Result types are unique across the host, projection names are unique within a source/dataset, and incompatible registrations fail instead of overwriting accessors. A nonrelational or unmanaged context and unmapped dependencies fail startup validation. Arbitrary provider transaction/isolation behavior still requires application acceptance testing.

`SnapshotRead<T>` reports the full source version, projection identity, UTC validation time, monotonic validation age and degraded-read flag. An eligible bounded read may return its previous value after a construction/storage failure, but never beyond the requested age, on caller cancellation, or after observed invalid/missing authority. Fresh reads fail if verification or rebuilding fails. A successful SQL version check does not refresh an old version's validation timestamp.

`RepositorySnapshotAdministration<TContext>.GetStatus()` reports this host's retained identities, validation ages and pending capacity without database I/O. Subscribe to meter `Monica.Repository.Snapshots` for `monica.repository.snapshots.events`, `monica.repository.snapshots.build.duration` and `monica.repository.snapshots.validation.duration`; durations use milliseconds. Events distinguish validated/bounded hits, joined builds, retries, failures, evictions, invalid authority and capacity rejection. Logs record failed builds and bounded degraded reads. No source connection strings or business input values are metric tags. Configure exporters in the host.

## Acceptance

Verify real host composition without Outbox or Redis; batch/multiple-flush coalescing; rollback and missing versions; child dependencies; publication suppression; declared bulk writes; independent hosts; projection/input isolation; bounded validation age; race fencing; cancellation and resource limits. Use actual relational providers for transaction and SQL claims. Deployment and multi-process checks are separate evidence from in-process tests.
