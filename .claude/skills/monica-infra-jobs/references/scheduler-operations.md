# Operate scheduled work

Register [JobScheduler](scheduler.md) with a store and scope before exposing operator actions. `JobSchedulerFacade` is the host-facing API for durable definitions, policy, queue, history, and diagnostics. It returns `Res<T>`; check status and data, and authorize any UI or API that exposes commands. `monica.AddJobSchedulerUI()` adds the optional operational workspace and brings the scheduler, shell, and localization modules, but still needs an explicit scheduler store and scope; a Web host must complete `app.UseMonica()` and `app.MapMonica()`.

Start with `GetRuntimeOverviewAsync()` for this host's configuration, store identity, worker/scheduling readiness, and owner footprint. `GetOverviewAsync()` gives a bounded scope summary. `QueryOperationalSummariesAsync(new JobDefinitionQuery())` or `GetOperationalSummaryAsync(new JobId(ownerKey, jobKey))` shows each definition's recurring cursor, suspension reasons, latest execution, and active queue counts. `QueryExecutionsAsync(query)` returns a bounded page; `GetExecutionAsync(instanceId)` includes append-only attempt history. These views help distinguish an undiscovered definition, a suspended schedule, a waiting execution, and an unhealthy worker.

Application code should enqueue a local typed triggered job through `ITriggeredJobManager`. Using the `RefreshOrderArgs` and discovered job defined in [scheduler](scheduler.md):

```csharp
var instanceId = await triggeredJobs.EnqueueAsync(
    new RefreshOrderArgs { OrderId = 42 },
    delay: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
```

For an operator command against a persisted definition, use `TriggerAsync(new JobTriggerRequest { OwnerKey = ownerKey, JobKey = jobKey, JobArgs = json })`; the JSON must match the declared argument type. `RunRecurringNowAsync(new JobRecurringRunNowRequest { OwnerKey = ownerKey, JobKey = jobKey })` queues an immediate recurring execution without advancing its schedule cursor, even when automatic scheduling is paused. Both return an execution instance and accept an optional caller-supplied `InstanceId` for idempotent retries. `CancelExecutionAsync(instanceId, reason)` cancels queued work or requests cooperative cancellation from the running worker.

Code declarations set the initial policy; operators change only policy overrides. Read `GetDefinitionAsync(new JobId(ownerKey, jobKey))`, then call `UpdatePolicyAsync(ownerKey, jobKey, new JobPolicyChange { Overrides = ..., ExpectedConcurrencyStamp = definition.Policy.ConcurrencyStamp })`. A stale stamp returns a conflict; reload the definition before retrying. Null override fields inherit the code declaration or scheduler default. `UpdatePoliciesAsync` applies a bounded batch independently and reports each item, so inspect partial successes. Schedule overrides cannot change the host-configured cron time zone.

Operations and result behavior: `Monica.JobScheduler/Facades/JobSchedulerFacade.cs`, `Monica.JobScheduler/Abstractions/ITriggeredJobManager.cs`, `Monica.JobScheduler/Services/TriggeredJobManager.cs`, `Monica.JobScheduler/Models/Operations/JobSchedulerOperations.cs`, and `Monica.JobScheduler/Models/Definitions/JobPolicyModels.cs`. Tests: `tests/Test.Monica.JobScheduler/Facades/JobSchedulerFacadeTests.cs` and `tests/Test.Monica.JobScheduler/Facades/JobSchedulerFacadeAnalyticsTests.cs`. UI dependencies: `Monica.JobScheduler.UI/Modules/ModuleJobSchedulerUI.cs`.
