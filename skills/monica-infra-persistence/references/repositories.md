# Repository contexts and aggregate access

`ModuleRepository` supplies EF Core repository contexts and `IRepository<TEntity>`. The application owns its entities, EF model, database provider, connection settings, and migrations. A key-value cache or shared service registry belongs to the hosting skill's [state stores](../../monica-infra-hosting/references/state-stores.md).

## Register the application context

For the context and narrow repository class shapes, use the application repository template in `$monica-application-project-unit-development` (`references/13-repository-and-persistence-template.md`). In this example, `OrdersDbContext` is that application-owned context and `connectionString` selects its database. Register it inside the host's `AddMonica` callback:

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

The core `IRepository<TEntity>` stages `Add`/`Remove`; `IRepository<TEntity,TKey>` additionally loads a tracked aggregate by key. Domain-specific repositories add purposeful tracked queries. The repository interface deliberately has no `Save` method; [transactions](transactions.md) owns the commit. Registration supplies a local database boundary, not a distributed transaction or an EventBus transport. Add [transactional events](transactional-events.md) when outbox, inbox, or entity projections must join the commit.

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

## Validate native provider access

Provider APIs that bypass EF command interceptors, such as binary COPY, must call
`RepositoryDbContext<TContext>.ValidateDatabaseAccess()` immediately before database I/O. This reuses
the checks for failed saves, terminal or rollback-only operations, selected transaction participants,
and physical adapters sharing their transaction owner's connection and transaction. Validation
does not access the database, start or commit a transaction, or apply save policies. The technical
adapter still supplies provider prerequisites and calls `IUnitOfWorkManager.Current?.MarkRollbackOnly()`
when execution fails, so catching a provider error cannot allow the operation to commit.

## Inspect repository registrations

To inspect these registrations through `AddRepositoryUI()`, use [Repository diagnostics](repository-diagnostics.md) for the four task routes, exact context identity, provider activity scope, and migration command outcomes.

Repository model and save policy: `Monica.Repository/Persistence/Services/RepositoryDbContext.cs`. Registration: `Monica.Repository/Modules/ModuleRepository.cs` and `ModuleUnitOfWork.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs` exercises repository commit behavior.
