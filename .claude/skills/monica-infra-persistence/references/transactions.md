# UnitOfWork transactions

Monica's automatic UnitOfWork owns saving and commit for an operation. Register a `RepositoryDbContext<TContext>` in `DbContextProviderType.UnitOfWork` mode; [repositories](repositories.md) covers the registration, which also requires `ModuleUnitOfWork`. Resolve the handler, repositories, and participating contexts from the operation's DI scope. A database transaction requires a relational provider.

## Ordinary writes

An ordinary mediated write needs no transaction attribute. `IRepository<TEntity>.Add` and `Remove` stage changes; the automatic UnitOfWork saves them when the handler succeeds:

```csharp
using Monica.Core.Mediator;
using Monica.Core.Results;
using Monica.Repository.Persistence.Abstractions;

public sealed record SaveOrder(Order Order) : IRequest<Res>;

public sealed class SaveOrderHandler(IRepository<Order> orders)
    : IRequestHandler<SaveOrder, Res>
{
    public Task<Res> Handle(SaveOrder request, CancellationToken cancellationToken)
    {
        orders.Add(request.Order);
        return Task.FromResult(Res.Ok());
    }
}
```

`Order` is the application's entity; register the handler through the host's normal DI/discovery path. The boundary begins a transaction, runs the handler, drains same-scope domain events, flushes participants, and commits. An exception, failed `Res`/`Res<T>`, or execution outcome marked for rollback rolls it back.

The execution adapters choose these defaults:

| Entry point | Default |
| --- | --- |
| Mediator handler | `Automatic`; a registered `IReadOnlyRequestConvention` selects `None` for reads, including published Query requests with the application convention, independently of HTTP method |
| MVC action | `None` for GET, HEAD, and OPTIONS; `Automatic` for other HTTP methods |
| EventBus handler | `Automatic` |
| JobScheduler job | `None` |
| Seeder or hosted-service work item | `Automatic` |
| Hosted-service start/stop lifecycle | `None` |

## Domain defaults in a complete application

When `AddApplicationModel` is present, select each persisted domain's primary context through the UnitOfWork options:

```csharp
monica.AddUnitOfWork(options =>
{
    options.ForDomain(ProductDomain.Ordering, typeof(OrderingDbContext));
    options.ForDomain(ProductDomain.Inventory, typeof(InventoryDbContext));
});
```

Each context must be registered with Repository and belong to the declared domain core assembly. The framework freezes this mapping and selects the current operation's domain from the application manifest or its component assembly. It does not use the host's AppId or a global `BelongToDomain`. Domains without a mapping have no implicit persistence participant. Standalone hosts without the application model retain the single registered write-context behavior.

Explicit `ExecutionTransaction.DbContextTypes` and execution-scope options take precedence. History and intentional same-connection multi-context operations use that explicit selection. Nested ordinary calls join the existing operation and do not switch its transaction. Access to another database does not make it an atomic participant; writes outside the selected transaction remain rejected by the existing persistence guard. Use independent operation scopes and explicit coordination when separate databases must commit independently.

## One declaration for an override

Use `ExecutionTransactionAttribute` from `Monica.Core.Execution` on the **concrete execution method** when its default needs to change. Request types, handler classes, and controller classes do not carry transaction metadata. The execution descriptor resolves the method declaration once; all adapters use that policy.

```csharp
using Monica.Core.Execution;

[ExecutionTransaction(ExecutionTransactionMode.None)]
public Task<Res> Handle(RefreshOrderCache request, CancellationToken cancellationToken)
{
    return cache.RefreshAsync(request.OrderId, cancellationToken);
}
```

Here `RefreshOrderCache` and `cache` are application-owned request and cache abstractions; the command updates only the cache. `None` skips Monica's automatic outer UnitOfWork, including its domain-event drain, flush, and commit. It is useful for:

- A handler that only reads, updates a cache, or calls an external service and has no local database changes to commit.
- An orchestrator such as an import where each item must commit independently and partial success is acceptable. Mark the outer method `None`, create a fresh DI scope per item, and send each ordinary write through that scope's mediator. Earlier committed items remain committed when a later item fails.

`None` does not suspend an enclosing transaction or prevent writes. A nested operation in the same scope can still use its caller's transaction. A staged repository change in a standalone `None` operation is not automatically saved; keep ordinary database commands on `Automatic`. Disabling the boundary for performance also removes its consistency and save behavior; measure any benefit against the operation's requirements.

To opt a default-`None` method into automatic saving, apply `[ExecutionTransaction(ExecutionTransactionMode.Automatic)]` to that method. This still requires UnitOfWork registration and participating contexts.

## Select database participants

When one UnitOfWork context is registered, it is selected automatically. With multiple write contexts, the same method declaration must select participants:

```csharp
[ExecutionTransaction(
    ExecutionTransactionMode.Automatic,
    DbContextTypes = new[] { typeof(OrdersDbContext), typeof(OrderProjectionDbContext) })]
public Task<Res> Handle(ApproveOrder request, CancellationToken cancellationToken)
{
    // Stage the application's order and projection changes here.
    return Task.FromResult(Res.Ok());
}
```

The application owns these context and request types. Types must be distinct registered `DbContext` types. The first context owns the physical transaction and any Inbox/Outbox rows; additional participants must share the **same `DbConnection` instance** and relational provider. Independent databases cannot form this local transaction. An empty selection uses the registered-context default. `None` rejects an explicit nonempty context selection.

## Explicit callers and scope lifetime

For work outside an execution adapter, resolve `IUnitOfWorkManager` and the repository from the **same scope** and use the same transaction engine explicitly:

```csharp
await unitOfWork.RunAsync(() =>
{
    repository.Add(order);
    return Task.CompletedTask;
}, cancellationToken: cancellationToken);
```

Supply `UnitOfWorkScopeOptions(DbContextTypes: [typeof(OrdersDbContext)])` when an explicit caller needs context selection. `UnitOfWorkScopeOptions` also supplies isolation and command-timeout settings. A pipeline caller's explicit execution feature takes precedence over the method's context selection.

A nested `RunAsync` joins the active transaction; even a caught nested failure leaves the outer operation rollback-only. The manager becomes terminal after completion or failure, so retry or verify committed state in a fresh DI scope. `Current.FlushAsync()` and direct `SaveChangesAsync()` flush changes inside the active transaction; neither commits it. A failed save faults the context. `SaveChanges(false)` and concurrent/recursive saves are unsupported. For durable delivery within this boundary, read [transactional events](transactional-events.md).

Policy declaration and resolution: `Monica.Core/Execution/ExecutionTransactionAttribute.cs` and `ExecutionDescriptor.cs`. Transaction semantics: `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs` and `Services/Behaviors/UnitOfWorkExecutionBehavior.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs`, `UnitOfWorkExecutionBehaviorTests.cs`, and `SharedTransactionTests.cs` exercise saving, rollback, and shared-connection boundaries.
