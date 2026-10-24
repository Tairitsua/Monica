# Job Template

## Use When

- Work should run on a schedule.
- Work should be triggered asynchronously with typed parameters.
- Work needs its own execution policy, operator control, or execution history.

## Rules

- Use `RecurringJob` for scheduled work and name it `Worker*`.
- Use `TriggeredJob<TArgs>` for on-demand asynchronous work and name it `Job*`.
- Keep the job focused on its execution lifecycle and orchestration. Entities own invariants; use a DomainService only for a meaningful rule without a natural entity owner. Retry or durability configuration needs an actual feature requirement.
- Add `[JobConfig]` only when defaults are not enough. Use `$monica-infra-jobs` for scheduler registration, durable store, owner/scope identity, and execution semantics.
- Inject `ILogger<TConcreteJob>` and pass it to the job base constructor.
- Use `$ApplicationNamespace$` for the application-layer namespace chosen by the architecture skill.
- Place jobs in `BackgroundWorkers/`.
- Use the owning role and the shared [local execution profile](../../monica-application/references/04-startup-and-local-execution.md) for activation. Manual jobs retain native explicit execution in a fresh local store; do not add compile-symbol checks, another debug-mode switch, or a separate local job runner.

## RecurringJob Example

```csharp
using Microsoft.Extensions.Logging;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Annotations;
using Monica.ProjectUnits.Annotations;

namespace $ApplicationNamespace$.BackgroundWorkers;

[JobConfig(CronSchedule = "0 */5 * * * *", RetryCount = 3)]
[ProjectUnitMetadata(
    "Refresh Order Snapshot Worker",
    Description = "Refreshes order snapshots on a fixed schedule.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class WorkerRefreshOrderSnapshot(
    DomainRefreshOrderSnapshot domainService,
    ILogger<WorkerRefreshOrderSnapshot> logger)
    : RecurringJob(logger)
{
    public override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await domainService.ExecuteAsync(cancellationToken);
    }
}
```

## TriggeredJob Example

```csharp
using Microsoft.Extensions.Logging;
using Monica.JobScheduler.Abstractions;
using Monica.ProjectUnits.Annotations;

namespace $ApplicationNamespace$.BackgroundWorkers;

public sealed class RefreshOrderSnapshotArgs
{
    public long OrderId { get; init; }
}

[ProjectUnitMetadata(
    "Refresh Order Snapshot Job",
    Description = "Refreshes one order snapshot asynchronously.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class JobRefreshOrderSnapshot(
    DomainRefreshOrderSnapshot domainService,
    ILogger<JobRefreshOrderSnapshot> logger)
    : TriggeredJob<RefreshOrderSnapshotArgs>(logger)
{
    public override async Task ExecuteAsync(
        RefreshOrderSnapshotArgs parameters,
        CancellationToken cancellationToken)
    {
        await domainService.ExecuteAsync(parameters.OrderId, cancellationToken);
    }
}
```

## Notes

- Keep job argument types simple and serializable.
- If a job does not need its own identity, scheduling metadata, or retry policy, an event handler may be sufficient.
