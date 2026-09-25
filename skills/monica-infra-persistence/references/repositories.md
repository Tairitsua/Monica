# Repository contexts and aggregate access

`ModuleRepository` supplies EF Core repository contexts and `IRepository<TEntity>`. The application owns its entities, EF model, database provider, connection settings, and migrations. A key-value cache or shared service registry belongs to the hosting skill's [state stores](../../monica-infra-hosting/references/state-stores.md).

## Register the application context

For the context and narrow repository class shapes, use the [application repository template](../../monica-application-project-unit-development/references/13-repository-and-persistence-template.md). In this example, `OrdersDbContext` is that application-owned context and `connectionString` selects its database. Register it inside the host's `AddMonica` callback:

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

Repository model and save policy: `Monica.Repository/Persistence/Services/RepositoryDbContext.cs`. Registration: `Monica.Repository/Modules/ModuleRepository.cs` and `ModuleUnitOfWork.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs` exercises repository commit behavior.
