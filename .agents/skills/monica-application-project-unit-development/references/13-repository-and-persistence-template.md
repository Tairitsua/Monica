# Repository and DbContext Template

For concurrent aggregate modification or Actor migration, apply the [aggregate concurrency contract](19-aggregate-concurrency.md) alongside the rules below.

Use `$DomainNamespace$` for the domain project namespace and `$RepositoryNamespace$` for its infrastructure side. Keep interfaces in domain `Interfaces/` and implementations/mapping in domain `Repository/`; no new shared project or domain `Persistence/` folder is required.

## Rules

- Business repository interfaces inherit the minimal native `IRepository<TEntity, TKey>` contract: stage `Add`/`Remove` and load tracked aggregates with `FindAsync`/`GetAsync`. Add purposeful business methods instead of mirroring LINQ.
- Name the pair `IRepository{Aggregate}` and exact `Repository{Aggregate}`. Runtime/source classification requires the implementation to implement `I{ImplementationClassName}`; a differently named interface can compile while leaving the repository absent from the catalog.
- Put materialized read projections and tracked mutation loaders on that owning repository interface and implementation. Do not introduce an undefined parallel query role or expose `IQueryable`, `DbContext`, `IEfEntityStore`, or save methods through the business interface.
- A mutation loader returns tracked aggregates from the current operation's identity map. Mutate those instances; do not attach detached copies or call a generic `Update`.
- The operation owns commit and rollback. A repository stages changes; it does not save independently.
- Use native EF LINQ inside the persistence implementation and materialize reads before the scope ends. `IEfEntityStore` and any project-wide EF adapter contract belong to the technical view of that implementation.
- Only a persistence-facing CRUD adapter in an approved/adopted pattern may consume the concrete repository or its wide EF contract. New concrete endpoints within that family are ordinary implementation. Handlers, jobs, and domain services use the narrow business interface; the exception does not authorize a new pattern or general concrete injection.
- Inject the scoped context directly into `EfRepository`. Context, repository, and caller share the operation scope and tracked entity identity. Keep the established registration/discovery mechanism.
- A justified project-common base may specialize `EfRepository` under the [project-level extension rules](01-project-unit-naming-and-boundaries.md#project-level-extensions); the concrete repository stays with its domain and retains the `Repository*` name.
- Use `$monica-infra-persistence` for save policies, operation scopes, transactions, and read-only request behavior.

## Aggregate Loading and Read Projection

`Order`, its `Lines` navigation, and `OrderSummary` are application-owned example types. The read projection is a support type; it does not acquire ProjectUnit identity merely by appearing in a repository contract.

```csharp
using Microsoft.EntityFrameworkCore;
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services;

public sealed record OrderSummary(long Id, string Number);

public interface IRepositoryOrder : IRepository<Order, long>
{
    Task<Order?> FindForApprovalAsync(string number, CancellationToken cancellationToken = default);
    Task<OrderSummary?> FindSummaryAsync(long id, CancellationToken cancellationToken = default);
}

[ProjectUnitMetadata("$RepositoryTitle$", Description = "$RepositoryDescription$")]
[ProjectUnitRequirement("$ExistingRequirementId$")]
public sealed class RepositoryOrder(OrderingDbContext context)
    : EfRepository<OrderingDbContext, Order, long>(context), IRepositoryOrder
{
    public Task<Order?> FindForApprovalAsync(string number, CancellationToken cancellationToken = default)
        => DbContext.Orders.AsTracking().Include(order => order.Lines)
            .SingleOrDefaultAsync(order => order.Number == number, cancellationToken);

    public Task<OrderSummary?> FindSummaryAsync(long id, CancellationToken cancellationToken = default)
        => DbContext.Orders.AsNoTracking().Where(order => order.Id == id)
            .Select(order => new OrderSummary(order.Id, order.Number))
            .SingleOrDefaultAsync(cancellationToken);
}
```

Replace the metadata placeholders with the concrete unit's responsibility and an existing actual requirement reference; do not allocate an example ID. The handler calls `FindForApprovalAsync` and then `order.Approve()`, or consumes `FindSummaryAsync` as a completed read. It never enumerates an EF query or saves the repository.

## DbContext and Composition

```csharp
public sealed class OrderingDbContext(
    DbContextOptions<OrderingDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<OrderingDbContext>(options, serviceProvider)
{
    public DbSet<Order> Orders => Set<Order>();
}

builder.AddMonica(monica =>
{
    monica.AddRepository()
        .AddRepositoryDbContext<OrderingDbContext>((_, options) => options.UseSqlite(connectionString))
        .AddOutbox<OrderingDbContext>();
});
```

The example shows application-owned repository and context placement. Use the application's established DI convention to resolve the business interface and concrete adapter against the same scoped context; verify this through the real host. Do not build another service provider or registration framework for a project base.

For `AddRepositoryDbContext`, multi-context participant selection, save policies, outbox, and commit behavior, read `$monica-infra-persistence`. For domain events versus distributed delivery, read `$monica-infra-messaging`. Keep migrations with the application that owns the data. Use `$monica-application-unit-testing` for independent arrange/act/assert scopes and assertions of tracked writes, persisted outcomes, and rollback.
