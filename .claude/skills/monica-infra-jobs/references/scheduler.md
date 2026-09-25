# JobScheduler

`AddJobScheduler()` discovers concrete `RecurringJob` and `TriggeredJob<TArgs>` types in the host's type-discovery scope. Composition requires both an `IJobSchedulerStore` and `UseSchedulerScope(scopeKey)`; the module brings HostedService and HealthCheck dependencies automatically. The scope key names one logical scheduler installation and must be shared by its replicas; use different keys for unrelated environments. `ModuleJobSchedulerOption.ProjectName` (or the resolved Monica project name) identifies the owner of discovered jobs; keep it stable across replicas and deployments. Discovered recurring declarations become persisted schedule cursors, and workers claim executions under leases. Worker identity fences leases. Definitions, cursors, and gates are keyed by `(SchedulerScopeKey, OwnerKey, JobKey)`, and the current protocol has no publisher retirement handshake: changing one owner's job set or contracts requires a drain or blue-green rollout so old and new publishers overlap safely. Register one store:

```csharp
builder.AddMonica(monica =>
{
    monica.AddJobScheduler()
        .UseInMemoryStore()
        .UseSchedulerScope("orders-dev");
});
```

`UseInMemoryStore()` loses state on restart and cannot coordinate multiple hosts. For a local durable example, use `UseEfCoreStore((_, db) => db.UseSqlite(connectionString))` from `Monica.JobScheduler.EfCore`, then apply `JobSchedulerDbContext` migrations before running the host. Production multi-replica deployments should use a shared PostgreSQL-compatible database with serializable transactions. The database owns the definition, policy, queue, lease, and history consistency boundary; all replicas for one scope must point to it. The EF Core registration supplies its own repository context in `DbContextProviderType.Default` mode; no application UnitOfWork context is needed for the scheduler store. A custom `UseStore<TStore>()` implementation must satisfy the full `IJobSchedulerStore` contract. `examples/JobSchedulerMinimal/Program.cs` is a runnable in-memory composition example.

```csharp
[JobConfig(CronSchedule = "0 */5 * * * *", MaxConcurrency = 1)]
public sealed class RefreshOrdersJob(ILogger<RefreshOrdersJob> logger) : RecurringJob(logger)
{
    public override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Refreshing orders");
        return Task.CompletedTask;
    }
}
```

The six-field cron has second precision and uses `ModuleJobSchedulerOption.CronTimeZone` (local time zone by default); occurrences are persisted in UTC. Job instances are transient and resolved in a fresh DI scope per execution. Honor cancellation for timeout, operator cancellation, and shutdown. `[JobConfig]` can set name, cron, concurrency, retry count, and execution timeout. Without overrides, concurrency is one, retry count is zero, and per-attempt timeout is one hour. A failed attempt is durably requeued up to `RetryCount`. At capacity, triggered work waits durably, while a scheduled recurring occurrence is recorded as skipped to avoid an unbounded backlog.

Triggered jobs derive from `TriggeredJob<TArgs>` with JSON-serializable reference-type args and override `ExecuteAsync(TArgs, CancellationToken)`. Put this job in the host's type-discovery scope:

```csharp
using Microsoft.Extensions.Logging;
using Monica.JobScheduler.Abstractions;

public sealed class RefreshOrderArgs
{
    public long OrderId { get; init; }
}

public sealed class RefreshOrderJob(ILogger<RefreshOrderJob> logger)
    : TriggeredJob<RefreshOrderArgs>(logger)
{
    public override Task ExecuteAsync(RefreshOrderArgs args, CancellationToken cancellationToken)
    {
        logger.LogInformation("Refreshing order {OrderId}", args.OrderId);
        return Task.CompletedTask;
    }
}
```

Replace the log with the application's operation and pass the supplied cancellation token to its I/O. Inject `ITriggeredJobManager` in application code and call `EnqueueAsync(new RefreshOrderArgs { OrderId = 42 }, cancellationToken: cancellationToken)`. The returned instance ID confirms admission, not successful execution; inspect it with `JobSchedulerFacade.GetExecutionAsync(instanceId)` to observe completion and attempts. The manager resolves the job by the exact `TArgs` type. `CancelExecutionAsync(instanceId)` cancels queued work or requests cooperative cancellation of running work. Use [scheduler operations](scheduler-operations.md) for delayed enqueueing or for triggering a persisted definition by owner and job key from an operator UI or API.

If a job is missing, inspect type-discovery scope, the job's definition validation, store selection, scope key, and host owner. If it is queued but idle, inspect worker health, leases, and cancellation state with the [operational surface](scheduler-operations.md). Scheduler state and its `monica.job-scheduler` readiness check are registered by the module.

Scheduler composition and defaults: `Monica.JobScheduler/Modules/ModuleJobScheduler.cs`; EF store registration: `Monica.JobScheduler.EfCore/Modules/ModuleJobSchedulerEfCore.cs`; job contracts and admission: `Monica.JobScheduler/Annotations/JobConfigAttribute.cs`, `Monica.JobScheduler/Abstractions/TriggeredJob.cs`, and `Monica.JobScheduler/Services/TriggeredJobManager.cs`. Tests: `tests/Test.Monica.JobScheduler/Modules/ModuleJobSchedulerCompositionTests.cs` and `tests/Test.Monica.JobScheduler/Services/JobExecutorExecutionPipelineTests.cs`.
