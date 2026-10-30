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
| Mediator handler | `Automatic`; a registered `IReadOnlyRequestConvention` selects `None` for reads, including GET-bound requests with the Web API convention |
| MVC action | `None` for GET, HEAD, and OPTIONS; `Automatic` for other HTTP methods |
| EventBus handler | `Automatic` |
| JobScheduler job | `None` |
| Seeder or hosted-service work item | `Automatic` |
| Hosted-service start/stop lifecycle | `None` |

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

## Save ownership and database commands

UnitOfWork is the sole transaction owner for transactional Outbox capture. `RepositoryDbContext` does not start another transaction or create custom savepoints because Outbox is configured. During a save in a verified managed physical transaction, it temporarily disables EF automatic savepoints and restores the previous setting afterward, including on failure. The validation includes the selected logical participant, provider, connection and physical transaction; delegated sharding contexts must share that ownership. A failed save faults the context and makes the whole operation rollback-only. Commit checks that state again after final flushing, so a failure marked by a save callback cannot be committed even when the callback returns normally.

Persistence policies and EF save callbacks must throw when they cannot continue. The original exception is propagated unchanged. If an EF callback only calls `MarkRollbackOnly()` and returns normally, `SaveChanges`/`SaveChangesAsync` throws `InvalidOperationException` as soon as that EF flush returns, including empty saves and the second Outbox flush. Rejection after the business flush prevents subsequent entity-event capture and Outbox flushing. The operation owner still rolls back when the caller catches the failure.

An empty save still executes persistence policies and EF save callbacks, but does not issue transaction or write commands when those callbacks leave it empty. UnitOfWork still owns its surrounding begin/commit. Do not filter participants using only `ChangeTracker.HasChanges()`: prepared Outbox messages have their own queue, and a logical sharding context delegates saving to physical contexts. The primary context is flushed again when a later participant stages projection messages on it.

Ordinary saves outside a managed UnitOfWork retain EF's transaction and automatic-savepoint behavior. A caller-owned transaction is never committed by the context. Durable publishing and enabled entity-event projections require a managed UnitOfWork before writing; a manually started EF transaction alone does not enable them. Business writes and generated-key event capture can require two flushes, both inside the managed transaction. Retry failures using a fresh scope and transaction.

`tests/Test.Monica.Repository/Persistence/RepositorySaveTransactionTests.cs` verifies synchronous/asynchronous empty saves, callback-created writes, immediate rollback-only rejection, message-only saves, generated keys, caller-owned transactions, savepoint counts and whole-operation rollback. `ContextAdapterOwnershipTests.cs` and `SharedTransactionTests.cs` cover physical ownership and the primary context's final Outbox flush.

Policy declaration and resolution: `Monica.Core/Execution/ExecutionTransactionAttribute.cs` and `ExecutionDescriptor.cs`. Transaction semantics: `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs` and `Services/Behaviors/UnitOfWorkExecutionBehavior.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs`, `UnitOfWorkExecutionBehaviorTests.cs`, and `SharedTransactionTests.cs` exercise saving, rollback, and shared-connection boundaries.
