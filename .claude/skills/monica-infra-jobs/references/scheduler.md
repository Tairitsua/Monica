# JobScheduler

`AddJobScheduler()` discovers concrete `RecurringJob` and `TriggeredJob<TArgs>` types in the host's type-discovery scope. Automatic composition requires an `IJobSchedulerStore`; Manual composition selects its own fresh process-local memory store. The scope defaults to `Deployment:Scope`, with `UseSchedulerScope(scopeKey)` available for an explicit scheduler namespace. A scope is required. The module brings HostedService and HealthCheck dependencies automatically. The scope key names one logical scheduler installation and must be shared by automatic replicas; use different keys for unrelated environments. In standalone composition, `ModuleJobSchedulerOption.ProjectName` (or the resolved Monica project name) identifies the owner of discovered jobs; keep it stable across replicas and deployments.

Set `Application:AutonomousWork=false` for Manual execution; `RecurringJobDebugMode` has been removed. Eligible definitions and execution history use the same isolated memory store, even when a shared provider is configured. That provider is not resolved by this scheduler. The local catalog initializes once and recurring definitions are marked Manual in that local store. The native worker executes explicit run-now, typed/API triggers, delays and retries. There is no automatic recurring materialization, shared snapshot publication, shared claims, lease recovery, retirement or cleanup. Restart discards this local history. Runtime diagnostics show `AutonomousWork=false` and the actual InMemory provider; enqueue success still does not establish job completion.

The host's `ITypeActivationPolicy.GetActivation(..., Job)` admits Automatic and Manual definitions and excludes Disabled definitions, freezing each definition's logical workload owner. A missing workload owner retains the project-owner fallback. Roles can contribute jobs to the same host without starting another scheduler or maintaining another job list. In Automatic mode, the scheduling plane publishes one complete snapshot per effective owner, typed trigger admission uses that definition's owner, and the worker claims only selected job keys for each owner. Recurring materialization and worker claim budgets remain shared across all owners in the process; owner rotation prevents a busy owner from monopolizing later polls. Disabled executors never enter the scheduler catalog, trigger admission, or worker claims. Automatic lease recovery and history maintenance remain scope-wide infrastructure work.

Discovered recurring declarations become persisted schedule cursors, and workers claim executions under leases. Worker identity fences leases. Definitions, cursors, and gates are keyed by `(SchedulerScopeKey, OwnerKey, JobKey)`; `JobKey` remains the job type's CLR full name. Replicas publishing the same owner must contribute the same complete selected declaration set. Each owner has an accepted hash of its normalized complete declaration manifest. Identical publication updates only owner observation metadata. Changed manifests require an exact previous hash through `ModuleJobSchedulerOption.OwnerManifestReplacements[ownerKey]` (or `JobOwnerSnapshot.ExpectedPreviousManifestHash` for an explicit store caller); otherwise publication fails closed. Stop incompatible writers before authorizing replacement. Automatic workers include their local hash on every claim and receive no work when the accepted hash differs. A valid scheduling lease alone does not authorize a manifest replacement. In a unified application, workload ownership defaults to the stable role ID, independently of AppId. `ApplicationRole.WorkloadOwner` can preserve an existing scheduler owner such as the old executable project name; moving code or deployment does not rename queued work or sticky policies. The model exposes selected workload owners even when their final job was removed, allowing an empty snapshot to retire those definitions without touching inactive roles. The runtime overview exposes `Configuration.OwnerKeys` for all locally published owners; `OwnerKey` remains the standalone fallback. The runtime UI displays the full list. Register one store:

```csharp
builder.AddMonica(monica =>
{
    monica.AddJobScheduler()
        .UseInMemoryStore()
        .UseSchedulerScope("orders-dev");
});
```

`UseInMemoryStore()` loses state on restart and cannot coordinate multiple hosts. For a local durable example, use `UseEfCoreStore((_, db) => db.UseSqlite(connectionString))` from `Monica.JobScheduler.EfCore`, then apply `JobSchedulerDbContext` migrations before running the host. Production multi-replica deployments use a shared PostgreSQL or explicitly verified Vastbase/GaussDB provider. Those paths use short read-committed transactions with native row locks; SQLite retains the serializable fixture boundary. Provider naming alone does not prove server product or lock behavior: verify the deployed database build and run the external-provider concurrency suite before deployment. The database owns the definition, policy, queue, lease, and history consistency boundary; all replicas for one scope must point to it. The EF Core registration supplies its own repository context in `DbContextProviderType.Default` mode; no application UnitOfWork context is needed for the scheduler store. A custom `UseStore<TStore>()` implementation must satisfy the full `IJobSchedulerStore` contract. `examples/JobSchedulerMinimal/Program.cs` is a runnable in-memory composition example.

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

Automatic replicas elect one scheduling coordinator per `(scope, owner)` and one maintenance coordinator per scope using the scheduler database. ServiceDiscovery leadership is unrelated. Scheduling, lease recovery, and history cleanup run in separate bounded loops; a scope maintainer recovers work belonging to owners no longer deployed. Offline owners are not automatically retired, and pending work is not discarded on a missing heartbeat. Coordinator grants last 30 seconds by default and are checked under their row lock by each coordinated mutation. Long cursor-reconciliation and materialization cycles renew between bounded store calls; losing or reacquiring a different token ends the old cycle. Manual hosts do not acquire these shared grants.

All admission and capacity-changing commands follow coordinator metadata (when present), Gate, Definition, Cursor, Execution, then History ordering. Claims exclude saturated gates and future work before their candidate limit, skip locked native gates, and rotate by stable job key. `ActiveCount` is released only by the winning fenced transition; an expired Running lease remains counted until recovered. Lowering concurrency lets existing work drain. Scheduled occurrences probe at most the current concurrency limit of Queued plus Running records under the same Gate, including future queued records. They still record Skipped at capacity; Triggered and RunNow still queue. There is no separate outstanding-count ledger.

Renewal locks only the addressed execution, observes database time after the lock wait, and conditionally updates lease expiry without rewriting history or the execution concurrency token. It never renews an expired token. `JobExecutionLease.GrantedAtUtc` and successful `JobLeaseRenewalResult.GrantedAtUtc`/`LeaseExpiresAtUtc` are required authority for the worker's monotonic watchdog. Custom stores must return that pair; do not substitute a worker wall clock. Renewal does not acquire a Gate.

The worker establishes supervision before dispatching user code, including its synchronous preamble. Its conservative deadline starts at the local request-send timestamp and uses the store-granted duration, subtracting the safety margin and cancellation grace. A stalled renewal cannot stop the watchdog, and a late result cannot revive a retired attempt. Cancellation callbacks count inside the same capped grace period. Uncooperative code retains its execution slot, stops new admission, and requests host termination; `StopApplication` is a request, so deployment supervision must actually terminate an unsafe process. Completion, release, and renewal I/O have independent deadlines. Pending calls remain observed and cannot accumulate through continued admission.

| Option | Default | Contract |
| --- | --- | --- |
| `ExecutionLeaseDuration` | 30 s | Exceeds renewal interval + cancellation grace + safety margin |
| `ExecutionLeaseRenewInterval` | 10 s | Renews only a current unexpired attempt |
| `ExecutionLeaseSafetyMargin` | 2 s | Reserved before conservatively estimated expiry |
| `ExecutionCancellationGracePeriod` | 15 s | Includes user cancellation callbacks and is capped by confirmed lease runway |
| `ExecutionLeaseRenewalTimeout` | 5 s | An unresponsive renewal does not block supervision or spawn overlapping renewals |
| `ExecutionFinalizationTimeout` | 5 s | Bounds completion/release and triggers admission shutdown if the store does not exit |
| `MaxConcurrentStoreOperations` | 8 | Total per-process relational operation admission |
| `ReservedLeaseStoreOperations` | 2 | Slots ordinary operations cannot consume; reserved for renewal/completion |
| `StoreOperationTimeout` | 5 s | Includes admission waiting, transaction work and bounded transient retries |

The store creates and disposes a context per operation, with no connection held during user code. Claim, coordinator acquisition and recovery share one `StoreOperationTimeout` across their initial read and all following transactions; moving to another job does not reset that request deadline. It retries known transaction conflicts and narrowly identified scheduler identity races; arbitrary update, connection, or unknown commit errors are not blindly replayed. When Vastbase omits structured constraint metadata, the fallback requires its exact known primary-key error and a matching tracked scheduler insertion. An unrecognized response fails closed. Application code still owns business idempotence because a lost lease cannot undo external side effects. Durable execution logging remains awaited and never becomes a lossy buffer.

Worker polling backs off with jitter within `WorkerPollInterval`; execution-slot completion wakes the local worker immediately. Scheduling loops also jitter within `SchedulingPollInterval`. On native providers, empty/future-only/saturated claim probes and active-other coordinator denials each use one read-only statement without entering a write transaction. Acquisition still rechecks the coordinator under its row lock and samples the database clock after waiting. History passes scan at most 32 job identities and delete at most the configured deletion budget, checking a two-second budget between bounded per-job operations. Keyset continuation revisits a partially deleted job before advancing. Scope maintenance includes absent definitions and execution-only owners.

Relational operation telemetry uses meter `Monica.JobScheduler.Store` (`JobSchedulerStoreMetricNames`): operation duration in seconds, operation outcomes, and transaction retries. Subscribe with the host's exporter; operation/outcome tags contain no execution IDs, lease tokens, or arguments. Read-only probes and transaction commands have their own bounded operation observations. Pair these with actual provider SQL/connection evidence when measuring throughput or contention.

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

Scheduler composition and defaults: `Monica.JobScheduler/Modules/ModuleJobScheduler.cs`; EF store registration: `Monica.JobScheduler.EfCore/Modules/ModuleJobSchedulerEfCore.cs`; job contracts and admission: `Monica.JobScheduler/Annotations/JobConfigAttribute.cs`, `Monica.JobScheduler/Abstractions/TriggeredJob.cs`, and `Monica.JobScheduler/Services/TriggeredJobManager.cs`. Tests: `tests/Test.Monica.JobScheduler/Modules/ModuleJobSchedulerCompositionTests.cs`, `Modules/ModuleJobSchedulerActivationTests.cs`, `Modules/ManualJobSchedulerTests.cs`, `Services/JobSchedulingHostedServiceTests.cs`, and `Services/JobExecutorExecutionPipelineTests.cs` under the same test project.

Store protocol checks: `tests/Test.Monica.JobScheduler/Stores/JobSchedulerCoordinationStoreContractTests.cs`, `JobSchedulerStoreCleanupTests.cs`, and the opt-in `Stores/External/` suites. External tests require explicit `MONICA_JOBSCHEDULER_TEST_POSTGRES` or `MONICA_JOBSCHEDULER_TEST_VASTBASE` admin-test connection settings, verify actual product identity, create uniquely owned test databases, and never reuse application databases. Run them serially against a shared physical test server. Worker fault tests cover hung renewal, blocking cancellation callbacks, stale grants, delayed claims, and bounded finalization with a monotonic test clock.
