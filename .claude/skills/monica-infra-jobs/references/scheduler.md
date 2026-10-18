# JobScheduler

`AddJobScheduler()` discovers concrete `RecurringJob` and `TriggeredJob<TArgs>` types in the host's type-discovery scope. Composition requires both an `IJobSchedulerStore` and `UseSchedulerScope(scopeKey)`; the module brings HostedService and HealthCheck dependencies automatically. The scope key names one logical scheduler installation and must be shared by its replicas; use different keys for unrelated environments. In standalone composition, `ModuleJobSchedulerOption.ProjectName` (or the resolved Monica project name) identifies the owner of discovered jobs; keep it stable across replicas and deployments.

When application composition supplies an `ITypeActivationPolicy`, discovery admits only selected job types and freezes each definition's logical workload owner. A missing workload owner retains the project-owner fallback. Roles can contribute jobs to the same host without starting another scheduler or maintaining another job list. The scheduling plane publishes one complete snapshot per effective owner, typed trigger admission uses that definition's owner, and the worker claims only selected job keys for each owned owner. Recurring materialization and worker claim budgets remain shared across all owners in the process; owner rotation prevents a busy owner from monopolizing later polls. Unselected job executors never enter the scheduler catalog, trigger admission, or worker claims. Lease recovery and history maintenance remain scope-wide infrastructure work.

Discovered recurring declarations become persisted schedule cursors, and workers claim executions under leases. Worker identity fences leases. Definitions, cursors, and gates are keyed by `(SchedulerScopeKey, OwnerKey, JobKey)`; `JobKey` remains the job type's CLR full name. Replicas publishing the same owner must contribute the same complete selected declaration set. The current protocol has no publisher retirement handshake: changing one owner's job set or contracts requires an explicit rollout strategy that prevents incompatible snapshot publishers from overlapping. In a unified application, workload ownership defaults to the stable role ID, independently of AppId. `ApplicationRole.WorkloadOwner` can preserve an existing scheduler owner such as the old executable project name; moving code or deployment does not rename queued work or sticky policies. The model exposes selected workload owners even when their final job was removed, allowing an empty snapshot to retire those definitions without touching inactive roles. The runtime overview exposes `Configuration.OwnerKeys` for all locally published owners; `OwnerKey` remains the standalone fallback. The runtime UI displays the full list. Register one store:

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

Jobs default to no automatic application UnitOfWork. Use the persistence [transaction contract](../../monica-infra-persistence/references/transactions.md) when job work needs automatic saving, context selection, or independently committed child operations.

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

If a job is missing, inspect type-discovery scope, role activation, the job's definition validation, store selection, scope key, and effective owner. If it is queued but idle, inspect worker health, leases, and cancellation state with the [operational surface](scheduler-operations.md). Scheduler state and its `monica.job-scheduler` readiness check are registered by the module.

Scheduler composition and defaults: `Monica.JobScheduler/Modules/ModuleJobScheduler.cs`; EF store registration: `Monica.JobScheduler.EfCore/Modules/ModuleJobSchedulerEfCore.cs`; job contracts and admission: `Monica.JobScheduler/Annotations/JobConfigAttribute.cs`, `Monica.JobScheduler/Abstractions/TriggeredJob.cs`, and `Monica.JobScheduler/Services/TriggeredJobManager.cs`. Tests: `tests/Test.Monica.JobScheduler/Modules/ModuleJobSchedulerCompositionTests.cs`, `Modules/ModuleJobSchedulerActivationTests.cs`, `Services/JobSchedulingHostedServiceTests.cs`, and `Services/JobExecutorExecutionPipelineTests.cs` under the same test project.
