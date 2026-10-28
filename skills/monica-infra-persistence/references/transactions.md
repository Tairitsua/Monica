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

Each context must be registered with Repository and belong to the declared domain core assembly. The framework freezes this mapping and selects the concrete executing component's role/domain ownership, with verified request-operation metadata as a fallback. Conflicting ownership fails. It does not infer ownership from an event's namespace, publisher, topic, host AppId or a global `BelongToDomain`. Domains without a mapping have no implicit persistence participant. Standalone hosts without the application model retain the single registered write-context behavior.

Explicit execution-scope options take precedence over `ExecutionTransaction` method options, which take precedence over domain defaults. History and intentional same-connection multi-context operations can use explicit selection. Nested calls resolve their own participants even when the outer transaction is active. Compatible contexts join that transaction without replacing its original Inbox/Outbox owner. A different connection or provider fails before the target's business body; the shared operation becomes rollback-only.

## Shared and independent API calls

Generated APIs default to `ApiTransaction.Shared`. For an independently committed step, make the boundary explicit at the call site:

```csharp
using Monica.Invocation.Models;

var result = await inventoryApi
    .WithTransaction(ApiTransaction.Independent)
    .ReserveStock(request, cancellationToken);
```

`inventoryApi`, `ReserveStock` and `request` are application-owned generated contracts. `WithTransaction` returns an immutable typed view borrowing the client's dependencies. Creating a view opens no transaction, does not change the injected client, and does not change other calls. Its owning dependency scope must outlive its invocations. The selection applies to calls made on that view; normal descendant calls use Shared within the selected operation.

| Invocation | Shared (default) | Independent |
| --- | --- | --- |
| No active transaction | Execute an ordinary root at the selected target | Execute an independent root at the selected target |
| Active transaction; compatible local target | Join the same physical transaction; outermost owner commits | Resolve a fresh scope, handlers and contexts; commit independently |
| Active transaction; target uses another connection | Reject before target business execution | Execute and commit independently |
| Active transaction; target is remote | Reject before sending HTTP and mark the caller rollback-only | Receiver owns its operation and commit |
| Child fails and caller handles the result/exception | Shared transaction remains rollback-only | Caller decides whether to continue; child rolls back |
| Child succeeds and caller later rolls back | Roll back both | Previously committed child changes remain |

An independent query or `None` entry gets a fresh scope without being forced to open a write transaction. `None` does not suspend the caller's transaction and cannot substitute for Independent. For an orchestration with no outer database writes, `None` plus independently transactional child calls avoids holding a parent transaction across those calls. Independent does not supply retries, compensation or durable orchestration. A remote timeout can leave its commit outcome unknown; retain business idempotency and recovery. On the same database, an independent child must not wait for locks held by its awaiting parent.

Role and authority routing still select the destination. `RunOnRole` can remain local when the implementation and required active role are present; it does not always imply HTTP. A shared call with an active transaction that resolves remotely fails even when both hosts use the same database or logical AppId. No transaction is transmitted, no role/authority constraint is bypassed, and Shared never falls back to Independent. Without an active caller transaction, a normal root remote call remains valid.

## Prefer one database unless separation is needed

For the unified domain/role application model, start with one relational database unless data isolation, capacity or independent operations require separate databases. Keep domain-owned contexts, tables/schemas and migrations, and access other domains through their published APIs. Sharing a database does not authorize direct access to a peer domain's repositories or tables.

Atomic participation requires one scoped `DbConnection` instance and one provider, not merely identical connection strings. For example, a host using the SQLite provider can supply a shared scoped connection:

```csharp
builder.Services.AddScoped(_ => new Microsoft.Data.Sqlite.SqliteConnection(connectionString));
builder.AddMonica(monica =>
{
    monica.AddRepository()
        .AddRepositoryDbContext<OrdersDbContext>((services, options) =>
            options.UseSqlite(services.GetRequiredService<Microsoft.Data.Sqlite.SqliteConnection>()))
        .AddRepositoryDbContext<InventoryDbContext>((services, options) =>
            options.UseSqlite(services.GetRequiredService<Microsoft.Data.Sqlite.SqliteConnection>()));
    monica.AddUnitOfWork(options =>
    {
        options.ForDomain(ProductDomain.Ordering, typeof(OrdersDbContext));
        options.ForDomain(ProductDomain.Inventory, typeof(InventoryDbContext));
    });
});
```

This composition fragment assumes the application's domain model and normal host lifecycle are already registered; the example types belong to that application. Import EF Core and DI extensions. Use the equivalent connection overload for the selected relational provider. The DI scope owns the connection; the contexts borrow it. Never use a singleton connection: Independent needs separate context and connection instances. Separate database connections require an explicit Independent call boundary when invoked from an active transaction. No distributed commit is provided.

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
- An orchestrator such as an import where each item must commit independently and partial success is acceptable. Mark the outer method `None` and call each generated command through `WithTransaction(ApiTransaction.Independent)`. Earlier committed items remain committed when a later item fails.

`None` does not suspend an enclosing transaction or prevent writes. A nested operation in the same scope can still use its caller's transaction. A staged repository change in a standalone `None` operation is not automatically saved; keep ordinary database commands on `Automatic`. Disabling the boundary for performance also removes its consistency and save behavior; measure any benefit against the operation's requirements.

To opt a default-`None` method into automatic saving, apply `[ExecutionTransaction(ExecutionTransactionMode.Automatic)]` to that method. This still requires UnitOfWork registration and participating contexts.

Set `IsolationLevel = System.Data.IsolationLevel.Serializable` on the same attribute when the operation requires that isolation. The default `Unspecified` uses the provider default. `None` rejects an isolation override. A nested requirement must match the active physical transaction's actual isolation; it cannot upgrade or replace it. Put the required isolation on the root or choose an independent operation. Explicit execution features override method options.

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

A nested `RunAsync` joins the active transaction and can select additional compatible participants; it cannot replace isolation or command timeout. Even a caught nested failure leaves the outer operation rollback-only. The manager becomes terminal after completion or failure, so retry or verify committed state in a fresh DI scope. `Current.FlushAsync()` and direct `SaveChangesAsync()` flush changes inside the active transaction; neither commits it. A failed save faults the context. `SaveChanges(false)` and concurrent/recursive saves are unsupported. For durable delivery within this boundary, read [transactional events](transactional-events.md).

Policy declaration and resolution: `Monica.Core/Execution/ExecutionTransactionAttribute.cs` and `ExecutionDescriptor.cs`. Transaction semantics: `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs` and `Services/Behaviors/UnitOfWorkExecutionBehavior.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs`, `ExecutionTransactionPolicyTests.cs`, and `SharedTransactionTests.cs` exercise saving, isolation, rollback, and shared connections. `tests/Test.Monica.Application/TransactionBoundaryTests.cs` verifies generated calls, fresh scopes, identity, independent commit, cancellation restoration, and atomic Inbox/business/Outbox effects. `tests/Test.Monica.ApplicationInvocation/ApplicationInvocationTests.cs` verifies role and authority routing before remote dispatch.
