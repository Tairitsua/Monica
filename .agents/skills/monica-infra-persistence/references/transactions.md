# UnitOfWork transactions

Register a `RepositoryDbContext<TContext>` in `DbContextProviderType.UnitOfWork` mode before using this boundary; [repositories](repositories.md) covers that registration. Resolve all participants from the operation's DI scope.

`IUnitOfWorkManager.RunAsync` is scoped. It begins a relational transaction for the selected context, runs the delegate, drains same-scope domain events, flushes every participant, then commits. An exception, failed `Res`/`Res<T>`, or execution outcome marked for rollback rolls the transaction back. A nested call joins the active transaction; even a caught nested failure leaves the outer operation rollback-only. The manager becomes terminal after completion or failure, so retry with a new DI scope. Read-only operations should bypass the automatic write behavior.

For an explicit boundary, resolve the manager and repository from the **same scope**:

```csharp
await unitOfWork.RunAsync(() =>
{
    repository.Add(order);
    return Task.CompletedTask;
}, cancellationToken: cancellationToken);
```

Read the result in a new DI scope to verify the committed state; do not reuse the write scope after the manager completes.

The execution pipeline also applies `UnitOfWorkExecutionBehavior` to operations with `ExecutionTransactionMode.Automatic`; an application may use that boundary rather than calling `RunAsync` itself. Mediated requests and MVC write actions select `Automatic` by default; `[ReadOnlyOperation]`, `[ExecutionTransaction]`, or a registered `IReadOnlyRequestConvention` (the Web API module marks GET-bound requests read-only) selects `None`.

If several UnitOfWork contexts are registered, select participants explicitly with `UnitOfWorkScopeOptions(DbContextTypes: [typeof(OrdersDbContext)])` or `[UnitOfWorkContext(...)]` on the execution entry. Multiple selected contexts must share the *same `DbConnection` instance* and relational provider. Independent databases cannot form this local transaction.

`Current.FlushAsync()` and direct `SaveChangesAsync()` flush changes inside the active transaction; neither commits it. A failed save faults the context. `SaveChanges(false)` and concurrent/recursive saves are unsupported; retry in a fresh scope. For durable delivery within this boundary, read [transactional events](transactional-events.md).

Transaction semantics: `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs`. `tests/Test.Monica.Repository/UnitOfWork/UnitOfWorkRepositoryCommitTests.cs` and `SharedTransactionTests.cs` exercise commit and shared-connection boundaries.
