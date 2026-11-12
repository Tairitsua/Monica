# UnitOfWork transactions

Monica's automatic UnitOfWork owns saving and commit for an operation. Register a `RepositoryDbContext<TContext>` in `DbContextProviderType.UnitOfWork` mode; [repositories](repositories.md) covers the registration, which also requires `ModuleUnitOfWork`. Resolve the handler, repositories, and participating contexts from the operation's DI scope. A database transaction requires a relational provider.

## Ordinary writes

An ordinary mediated write needs no transaction attribute. `IRepository<TEntity>.Add` and `Remove` stage changes; the automatic UnitOfWork saves them when the handler returns normally, regardless of its result status:

```csharp
using Monica.Core.Mediator;
using Monica.Core.Results;
using Monica.Repository.Persistence.Abstractions;

public sealed record SaveOrder(Order Order) : IRequest<Result>;

public sealed class SaveOrderHandler(IRepository<Order> orders)
    : IRequestHandler<SaveOrder, Result>
{
    public Task<Result> Handle(SaveOrder request, CancellationToken cancellationToken)
    {
        orders.Add(request.Order);
        return Task.FromResult(Result.Success());
    }
}
```

`Order` is the application's entity; register the handler through the host's normal DI/discovery path. The boundary begins a transaction, runs the handler, drains same-scope domain events, flushes participants, and commits after normal completion. A returned failure is still normal completion; it does not request rollback.

## Business outcomes and explicit abort

Business failure and transaction rollback are separate decisions. This contract applies equally to commands, queries, direct MVC actions, nested operations, and the outermost operation:

- A returned `Result`/`Result<T>` failure, custom envelope, or HTTP error status never automatically requests rollback. Even an outermost `BadRequest`, `Conflict`, or `InternalError` result leaves staged and flushed changes eligible for commit.
- An exception caught before it escapes the transaction-owning operation does not automatically request rollback. This includes a parent catching an exception from a nested Shared call, and a handler catching an exception and returning a business failure. No nested execution, API invocation, or SQL error callback secretly marks the shared transaction.
- An exception that escapes the outermost owning operation triggers automatic rollback and is rethrown unchanged. This includes exceptions from domain-event draining, final saving, commit, and cancellation checks. An Independent child has its own owning boundary: catching its exception outside that boundary cannot undo its already completed rollback.
- Developers must inspect child results and decide whether to continue, return a failure, recover partial work, or explicitly abort. Propagating a failed result alone does not request rollback. Command/Query intent selects transaction and read behavior, not a different failure policy.

Use `IUnitOfWorkManager.Current.Abort(...)` or `IExecutionTransaction.Abort(...)` when a business failure must invalidate the whole operation. It retains the first reason and immediately throws `TransactionRollbackOnlyException`; it never returns normally. The owning boundary performs the database rollback while unwinding. Catching this exception cannot clear the abort, and subsequent Shared execution, database access, publishing, flushing, or commit is rejected.

For example, in an already active operation with a required child command:

```csharp
var child = await inventoryApi.ReserveStock(request, cancellationToken);
if (child.IsFailure())
{
    unitOfWork.Current!.Abort(ExecutionTransactionFailure.FromResult("RequiredReservation", child));
}
return child;
```

`inventoryApi`, `request`, and the business choice that reservation is required belong to the application; `unitOfWork` is its scoped `IUnitOfWorkManager`. Import `Monica.Core.Execution` and `Monica.Core.Results`. An optional child may instead be handled and execution continued. Explicit abort is an exception, not a failed-result return; if the application needs to translate it into a response, do so outside the owning transaction boundary.

Continuing does not roll back a failed child's writes. Tracked changes, prior flushes, native writes, domain events, and Outbox messages remain part of the shared operation. Validate before mutation when possible; otherwise deliberately recover a valid state or abort. Monica does not invent nested savepoints or undo part of the change tracker. Catching a provider exception cannot repair an aborted physical database transaction or a context whose save is incomplete: a later save/commit can still throw and trigger rollback. A cancelled owner token is still checked before completion; catching an earlier cancellation does not cancel that check. Retry an unusable context or transaction in a fresh scope.

Migration: replace `MarkRollbackOnly(...)` with `Abort(...)` and remove code that expected marking to return normally. Audit every failed return and caught exception after writes, including ignored child results, and explicitly abort where rollback is intended. `ExecutionOutcomePolicy` is removed; `IExecutionOutcome.IsFailure` reports diagnostics only and replaces `ShouldRollback`. Rebuild custom transaction owners and consumers together.

The execution adapters choose these defaults:

| Entry point | Default |
| --- | --- |
| Mediator handler | `IQueryRequest` defaults to `None`; commands and unclassified internal requests default to `Automatic`, independently of HTTP method, endpoint publication or naming |
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

Explicit execution-scope options take precedence over `ExecutionTransaction` method options, which take precedence over domain defaults. History and intentional same-connection multi-context operations can use explicit selection. Nested calls resolve their own participants even when the outer transaction is active. Compatible contexts join that transaction without replacing its original Inbox/Outbox owner. A different connection or provider fails before the target's business body. The caller can handle that exception; if it escapes the owning operation, the owner rolls back.

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
| No active unit of work | Resolve a fresh operation scope at the selected local target; the target's policy decides whether to open a transaction | Execute an independent root at the selected target |
| Active logical unit of work, no database enlisted yet | Reuse its scope; a compatible child may enlist the first database | Resolve a fresh scope and complete separately |
| Active transaction; compatible local target | Join the same physical transaction; outermost owner commits | Resolve a fresh scope, handlers and contexts; commit independently |
| Active transaction; target uses another connection | Reject before target business execution | Execute and commit independently |
| Active transaction; target is remote | Throw before sending HTTP; caller owns handling or abort | Receiver owns its operation and commit |
| Child returns failure | Caller decides whether to continue or explicitly abort; changes remain staged/flushed | Child commits on normal completion; caller decides how to handle its result |
| Child throws/cancels | Caller may catch it; automatic rollback occurs only if it escapes the owner | Child rolls back if the exception escapes its own owner |
| Child explicitly aborts | Immediate exception; shared operation cannot commit even if caught | Child rolls back; parent may handle its failure and continue |
| Child succeeds and caller later rolls back | Roll back both | Previously committed child changes remain |

A job or other `None` orchestration has a dependency scope without an automatic outer unit of work. Its default Shared API calls each own a fresh scope, so sequential commands can commit separately without reusing a completed manager. A Query or `None` target still opens no automatic write transaction. For example, inside such an orchestration:

```csharp
var first = await inventoryApi.ReserveStock(firstRequest, cancellationToken);
var second = await inventoryApi.ReserveStock(secondRequest, cancellationToken);
```

The requests and API are application-owned contracts. Handle each returned outcome according to the business workflow; a later failure cannot undo an earlier committed step. If both steps must succeed together, declare an Automatic outer operation and keep the default Shared calls. Without an active unit of work, nested Local APIs also get separate scoped services: pass data explicitly and use the application context contributor for trusted tenant/caller context instead of relying on mutable scoped state to cross API boundaries.

`None` does not suspend an already active unit of work and cannot substitute for Independent. Select Independent when a child must complete separately even inside an active parent. Independent does not supply retries, compensation or durable orchestration. A remote timeout can leave its commit outcome unknown; retain business idempotency and recovery. On the same database, an independent child must not wait for locks held by its awaiting parent.

Scope selection uses the logical unit of work, not merely the presence of an ambient execution scope or physical database transaction. `IExecutionTransaction.IsOperationActive` remains true before database enlistment and while rollback-only; `IsActive` reports an active physical transaction. A completed manager rejects the logical-state query instead of silently starting another operation. Custom transaction owners must implement both facts. Local invokers choose the scope before resolving handlers and dependencies; fresh scopes never reset an old manager, event queue, or snapshot transaction.

`tests/Test.Monica.Application/Invocation/LocalApiOperationScopeTests.cs` covers both Local invokers through a real job execution boundary, separate commits and disposal, transaction-free queries, exception/cancellation propagation, logical participation before enlistment, and rejection of a completed caller scope.

Role and authority routing still select the destination. `RunOnRole` can remain local when the implementation and required active role are present; it does not always imply HTTP. A shared call with an active transaction that resolves remotely fails even when both hosts use the same database or logical AppId. No transaction is transmitted, no role/authority constraint is bypassed, and Shared never falls back to Independent. Without an active caller transaction, a normal root remote call remains valid.

## Query contracts and failure propagation

Application requests declare `IQuery<TData>`/`IQuery` or `ICommand<TData>`/`ICommand` from `Monica.Application.Abstractions`. These retain the standard result envelope. A custom `IRequest<TResult>` uses the corresponding `IQueryRequest` or `ICommandRequest` marker from `Monica.Core.Mediator`. A request cannot declare both. Generated endpoints require exactly one intent matching their existing Query/Command name prefix; HTTP verb and name alone no longer establish intent. Rebuild protocols and all consumers together. The old name-based application convention and `IReadOnlyRequestConvention` are removed.

Ordinary queries need no `ExecutionTransaction(None)` attribute. They do not start a root transaction, but a local Shared query joins an active caller transaction and can read writes already flushed on that connection. Do not detach queries automatically: an independent scope cannot observe the caller's uncommitted writes. A query needing consistent multi-statement reads may explicitly select `Automatic`, participants and a provider-supported isolation level. Its root owns transaction completion but does not drain write-side domain events or automatically flush contexts. Query intent forbids business writes regardless of transaction mode; this release does not scan change trackers, parse SQL or enforce a database-level read-only session. Review and test the contract, including direct SQL and external side effects.

Queries use the same [business-outcome and explicit-abort contract](#business-outcomes-and-explicit-abort) as commands. Neither a returned failure nor a handled exception invalidates Shared work. Query read-only intent still suppresses its own write-side drain/flush; it cannot clear an explicit abort. `UnitOfWorkScopeOptions.IsReadOnly` selects the same read-only execution behavior for explicit callers.

Mediator/execution and generated Local APIs use the same ownership rule. Shared remote calls remain unsupported while a physical transaction is active; this routing rejection occurs before HTTP. Independent remains the explicit choice for separate visibility or remote/database boundaries and does not force a Query to open a transaction.

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
public Task<Result> Handle(RefreshOrderCache request, CancellationToken cancellationToken)
{
    return cache.RefreshAsync(request.OrderId, cancellationToken);
}
```

Here `RefreshOrderCache` and `cache` are application-owned request and cache abstractions; the command updates only the cache. `None` skips Monica's automatic outer UnitOfWork, including its domain-event drain, flush, and commit. It is useful for:

- A non-Query handler that updates a cache or calls an external service and has no local database changes to commit. Typed Queries already default to None.
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
public Task<Result> Handle(ApproveOrder request, CancellationToken cancellationToken)
{
    // Stage the application's order and projection changes here.
    return Task.FromResult(Result.Success());
}
```

The application owns these context and request types. Types must be distinct registered `DbContext` types. The first context owns the physical transaction and any Inbox/Outbox rows; additional participants must share the **same `DbConnection` instance** and relational provider. Independent databases cannot form this local transaction. An empty selection uses the registered-context default. `None` rejects an explicit nonempty context selection.

## First-failure diagnostics

`IUnitOfWork.FirstFailure` and `IExecutionTransaction.FirstFailure` remain null for returned failures and handled exceptions. They retain the first explicit abort, or the exception that escapes the owning operation. `ExecutionTransactionFailure` snapshots the source, reason, optional result status, operation name/key, invocation ID, timestamp, and current trace/span. Callers may supply context type or command ID for an explicit provider-related abort; SQL callbacks no longer populate rollback state. No request, result payload, SQL text, or parameters are captured.

`ExecutionTransactionFailure.FromResult("RequiredStep", result)` snapshots an explicitly rejected business outcome without retaining its payload. `FromException("RequiredStep", exception)` preserves a caught exception when the developer decides to abort. Neither factory aborts by itself. `Abort(failure)` records that decision and throws immediately; subsequent aborts cannot overwrite the snapshot. `Abort()` supplies a generic explicit reason. Never copy headers, credentials, request payloads or SQL into a reason.

`TransactionRollbackOnlyException` carries the transaction ID, rejected action, and the same first cause. Its `InnerException` is the exception supplied to the explicit abort; result-based aborts retain their status/message without inventing an inner exception. An uncaught original exception is recorded by the owner and rethrown unchanged. The operation boundary's current metadata describes where rollback was decided; the original exception stack still identifies where it was thrown.

The first record emits one structured Debug log under `UnitOfWorkManager`. The existing [exception response diagnostics](../../monica-infra-hosting/references/exception-diagnostics.md) preserve original exceptions by identity. Completed scopes reject reuse; resolve a fresh scope for retries. `tests/Test.Monica.Application/TransactionFailureDiagnosticsTests.cs` verifies immediate abort, first-cause preservation, rejection of further work after a caught abort, rollback of early flushes, Independent isolation, and unchanged propagation of escaping exceptions.

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

A nested `RunAsync` joins the active transaction and can select additional compatible participants; it cannot replace isolation or command timeout. Returned failures and caught nested exceptions follow the same explicit-abort policy as outermost results. The manager becomes terminal after completion or failure, so retry or verify committed state in a fresh DI scope. `Current.FlushAsync()` and direct `SaveChangesAsync()` flush changes inside the active transaction; neither commits it. A failed save faults the context. `SaveChanges(false)` and concurrent/recursive saves are unsupported. For durable delivery within this boundary, read [transactional events](transactional-events.md).

## Save ownership and database commands

UnitOfWork is the sole transaction owner for transactional Outbox capture. `RepositoryDbContext` does not start another transaction or create custom savepoints because Outbox is configured. During a save in a verified managed physical transaction, it temporarily disables EF automatic savepoints and restores the previous setting afterward, including on failure. The validation includes the selected logical participant, provider, connection and physical transaction; delegated sharding contexts must share that ownership. A failed save faults the context because business/Outbox capture may be incomplete. Catching that exception does not mark the transaction, but the next save (including final flushing) rejects the faulted context with the original exception as its inner cause. An uncaught rejection then rolls back the owner. Commit also checks for explicit abort after final flushing and snapshot finalization.

Persistence policies, EF save callbacks and entity-event projections must throw when they cannot continue. Their exceptions propagate unchanged. A callback can explicitly call `Abort(...)`, which throws immediately; later managed work remains rejected even if application code catches that abort. Saves also check for an abort after each EF flush, so a callback cannot swallow an explicit abort and let event capture or the second Outbox flush continue. Failed-context reuse is rejected independently of transaction rollback state; a caught save error never repairs incomplete capture.

An empty save still executes persistence policies and EF save callbacks, but does not issue transaction or write commands when those callbacks leave it empty. UnitOfWork still owns its surrounding begin/commit. Do not filter participants using only `ChangeTracker.HasChanges()`: prepared Outbox messages have their own queue, and a logical sharding context delegates saving to physical contexts. The primary context is flushed again when a later participant stages projection messages on it.

Ordinary saves outside a managed UnitOfWork retain EF's transaction and automatic-savepoint behavior. A caller-owned transaction is never committed by the context. Durable publishing and enabled entity-event projections require a managed UnitOfWork before writing; a manually started EF transaction alone does not enable them. Business writes and generated-key event capture can require two flushes, both inside the managed transaction. Retry failures using a fresh scope and transaction.

`tests/Test.Monica.Repository/Persistence/RepositorySaveTransactionTests.cs` verifies synchronous/asynchronous empty saves, callback-created writes, immediate explicit abort from save callbacks, original callback exceptions, message-only saves, generated keys, caller-owned transactions, savepoint counts and whole-operation rollback. `ContextAdapterOwnershipTests.cs` and `SharedTransactionTests.cs` cover physical ownership and the primary context's final Outbox flush.

Policy declaration and resolution: `Monica.Core/Execution/ExecutionTransactionAttribute.cs` and `ExecutionDescriptor.cs`. Transaction semantics: `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs` and `Services/Behaviors/UnitOfWorkExecutionBehavior.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs`, `ExecutionTransactionPolicyTests.cs`, and `SharedTransactionTests.cs` exercise saving, isolation, rollback, and shared connections. `tests/Test.Monica.Application/TransactionBoundaryTests.cs` verifies generated calls, fresh scopes, identity, independent commit, cancellation restoration, and atomic Inbox/business/Outbox effects. `tests/Test.Monica.ApplicationInvocation/ApplicationInvocationTests.cs` verifies role and authority routing before remote dispatch.
