# Repository contexts and aggregate access

`ModuleRepository` supplies EF Core repository contexts and `IRepository<TEntity>`. The application owns its entities, EF model, database provider, connection settings, and migrations. A key-value cache or shared service registry belongs to the hosting skill's [state stores](../../monica-infra-hosting/references/state-stores.md).

## Register the application context

For the context and narrow repository class shapes, use the application repository template in `$monica-application-building-block-development` (`references/13-repository-and-persistence-template.md`). In this example, `OrdersDbContext` is that application-owned context and `connectionString` selects its database. Register it inside the host's `AddMonica` callback:

```csharp
using Microsoft.EntityFrameworkCore;
using Monica.Modules;

builder.AddMonica(monica =>
{
    monica.AddRepository()
        .AddRepositoryDbContext<OrdersDbContext>((_, db) => db.UseSqlite(connectionString));
});
```

`OrdersDbContext` derives from `RepositoryDbContext<OrdersDbContext>`. `AddRepositoryDbContext` defaults to `DbContextProviderType.UnitOfWork` and requires `ModuleUnitOfWork` automatically. Use this mode for ordinary write operations. The registered context is scoped, while its `IDbContextFactory<T>` creates independently owned scopes for long-lived workers; dispose factory-created contexts. Use `DbContextProviderType.Default` for a store that owns its saves or is selected explicitly in a separate operation. Apply the application's migrations before the first write.

The core `IRepository<TEntity>` stages `Add`/`AddRange` and `Remove`/`RemoveRange`; `IRepository<TEntity,TKey>` additionally loads a tracked aggregate by key. Range staging retains normal graph tracking, audit, deletion policies, and enabled entity projections; it does not select a native database bulk API. Domain-specific repositories add purposeful tracked queries. The repository interface deliberately has no `Save` method; [transactions](transactions.md) owns the commit. Registration supplies a local database boundary, not a distributed transaction or an EventBus transport. Add [transactional events](transactional-events.md) when outbox, inbox, or entity projections must join the commit.

Repository contexts use EF's standard options composition, including `ConfigureDbContext<TContext>` contributions. Contexts remain scoped and options are transient; options callbacks resolve scoped dependencies from the requesting scope. The owned factory creates a fresh scope and consumes the same options pipeline instead of constructing a separate options builder. Disposing a factory-created context also disposes that scope. Registration, configuration contribution, scoped connection identity, and factory disposal are covered by `tests/Test.Monica.Repository/Hosting/RepositoryOptionsCompositionTests.cs`.

## Configure a default detail graph

`EfRepository<TDbContext,TEntity>` exposes a no-tracking `Query` and a `Details` query that calls `ApplyDetails(Query)`. The default `ApplyDetails` returns its input unchanged. Override it on the concrete repository when an entity has a canonical eager-loading graph; `Order` and its `Lines` navigation here are application-owned types:

```csharp
public override IQueryable<Order> ApplyDetails(IQueryable<Order> query)
    => query.Include(order => order.Lines);
```

The repository implements `IRepositoryDetailsConfigurator<TEntity>`, so native CRUD adapters also apply this graph when their `WithDetail()` override returns `true`. Plain `Query` access and tracked `FindAsync`/`GetAsync` do not apply the graph. Keep any existing query filters and tracking behavior when extending the input; use `Details.AsTracking()` or apply the configurator to an explicitly tracked query for mutation loading. Enumerate these queries within the owning operation scope and keep them off business repository interfaces.

`IsShardingTable` defaults to `false`; a provider-specific concrete repository can override it for projected CRUD queries that require different sorting behavior. The native repository does not detect a sharding provider or change transaction ownership.

`tests/Test.Monica.Repository/Persistence/Services/EfRepositoryTests.cs` covers default details, overridden graphs, CRUD query integration, and tracked operation commits.

## Insert prepared rows through a native provider

`IBulkWriter` in `Monica.Repository.Bulk.Abstractions` creates explicit native insert operations. Rows need not implement `IEntity`. Keep the writer inside named infrastructure/repository operations; ordinary business consumers keep their narrow repository interface. The existing Repository package owns the abstraction and execution guard. Database-specific adapters and their dependencies belong to the consuming application's infrastructure or an independently selected extension; Monica does not add a provider project or select one automatically.

The host must first register its context, then select exactly one singleton `IBulkInsertProvider` implementation for it. In the host's `AddMonica` callback, `OrdersDbContext` and `ApplicationBulkInsertProvider` below are application-owned types:

```csharp
monica.AddRepository()
    .AddRepositoryDbContext<OrdersDbContext>((_, db) => ConfigureDatabase(db))
    .UseBulkInsert<OrdersDbContext, ApplicationBulkInsertProvider>();
```

Inside a named operation with an active managed UnitOfWork, resolve `IBulkWriter` and pass the actual participating physical context and prepared rows:

```csharp
await bulkWriter.Insert(context, rows)
    .WithAuditProperties()
    .ExecuteAsync(cancellationToken);
```

Omit `WithAuditProperties()` when rows already contain their final fields. It returns a new immutable operation configuration and applies the context scope's existing `IAuditPropertySetter.SetCreationProperties` conventions before provider validation and I/O: missing creation time and creator identity are filled, supplied values follow the setter's preservation rules, and the Clock deployment timezone is honored. It does not enable entity events, audit logs, synchronization fields, optimistic concurrency, or soft deletion. The writer never attaches input rows or traverses navigation graphs. `Execute` is the synchronous counterpart.

Every nonempty insert requires a live managed transaction. The writer borrows the supplied context and transaction, reports its row type to snapshot version tracking, and never saves or commits for the caller. Provider selection uses the context's owning scope and registered generic context type, including physical adapters. A logical sharding coordinator is rejected: select a physical route whose connection and transaction match its owner, then execute sequentially. Additional effects from triggers require the named operation's complete snapshot dependency declaration; the writer cannot infer them.

Empty input is a no-op after cancellation validation. Duplicate client-assigned primary keys, pending mutations of the same mapped type, and conflicts with tracked keys are rejected. The provider rejects unsupported database/mapping shapes before I/O. No failure falls back to EF staging. Audit or provider execution failures, including cancellation after native work begins, leave the operation rollback-only even if caught; retry the whole operation in a fresh scope when permitted.

Generated values are not hydrated into caller-owned rows. Allocate client keys before routing and follow the provider's rules for generated/default columns and value converters. Input collections and rows remain caller-owned and must not be mutated concurrently. The provider extension point must be safe across independent contexts, validate without I/O, and use the supplied connection and transaction without replacing, committing, saving, or attaching them.

`tests/Test.Monica.Repository/Bulk/` verifies operation isolation, audit inputs, tracked/native/Outbox rollback, cancellation, and transactional snapshot revisions through a raw SQLite boundary double. It does not prove a production database's native bulk behavior; each consuming adapter must supply its own real-provider acceptance tests. `Persistence/RepositoryRangeTests.cs` covers ordinary aggregate range semantics.

## Validate other native provider access

Provider APIs that bypass EF command interceptors, such as binary COPY, must call
`RepositoryDbContext<TContext>.ValidateDatabaseAccess()` immediately before database I/O. This reuses
the checks for failed saves, terminal or rollback-only operations, selected transaction participants,
and physical adapters sharing their transaction owner's connection and transaction. Validation
does not access the database, start or commit a transaction, or apply save policies. The technical
adapter still supplies provider prerequisites and calls `IUnitOfWorkManager.Current?.MarkRollbackOnly()`
when execution fails, so catching a provider error cannot allow the operation to commit.
The shared `IBulkWriter` already owns these responsibilities for its insert operations.

## Inspect repository registrations

To inspect these registrations through `AddRepositoryUI()`, use [Repository diagnostics](repository-diagnostics.md) for the four task routes, exact context identity, provider activity scope, and migration command outcomes.

Repository model and save policy: `Monica.Repository/Persistence/Services/RepositoryDbContext.cs`. Registration: `Monica.Repository/Modules/ModuleRepository.cs` and `ModuleUnitOfWork.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs` exercises repository commit behavior.
