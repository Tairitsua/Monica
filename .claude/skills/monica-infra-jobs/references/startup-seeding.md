# Startup seeders

`monica.AddSeeder()` discovers concrete `ISeeder` implementations, builds a dependency graph, registers seeders as transient, and starts a background scheduler **after** the Generic Host publishes `ApplicationStarted`. HostedService and HealthCheck dependencies are registered automatically. Derive from `SeederBase` for a host logger and override `SeedingAsync`:

```csharp
[SeederPolicy(MaxAttempts = 3)]
public sealed class DefaultOrdersSeeder(ILogger<DefaultOrdersSeeder> logger) : SeederBase(logger)
{
    public override Task SeedingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Seeding default orders");
        return Task.CompletedTask;
    }
}
```

Replace the logging body with an idempotent seed operation. Add `[SeederDependsOn<PrerequisiteSeeder>]` when this seeder needs another discovered seeder to succeed first. The graph validates missing dependencies and cycles before execution. A seeder can override inherited `ExecutionMode`, `Criticality`, `FailureBehavior`, and `MaxAttempts` through `[SeederPolicy]`. Defaults are concurrent execution, required criticality, continue-and-record after exhausted attempts, and one attempt; `ModuleSeederOption.MaxConcurrency` defaults to the process's processor count. Retry only idempotent work. An exclusive seeder runs alone, regardless of maximum concurrency.

With `AddApplicationModel`, a seeder in a role assembly is startup work for that role. The module applies the `HostedService` activation boundary before registering seeders or validating the graph, so inactive role seeders are neither constructed nor executed. A selected seeder whose prerequisite belongs to an inactive role fails composition as an undiscovered dependency; co-locate the prerequisite or redesign that startup dependency. Shared infrastructure seeders remain available in every host. Without an activation policy, discovery retains its existing behavior. See `tests/Test.Monica.Framework/Seeder/SeederActivationTests.cs` for selected execution and dependency rejection.

Seeder execution defaults to an automatic UnitOfWork when that behavior is registered. Use the persistence [transaction contract](../../monica-infra-persistence/references/transactions.md) when a seeder needs a transaction override or explicit context selection. For `SeederBase`, declare it on an overridden `SeedAsync` entry method; `SeedingAsync` is the inner work method and does not own the execution boundary.

The `monica.seeder` readiness check remains unhealthy until required seeders succeed. Ordinary optional failures degrade the seeder state, while required failures keep it unhealthy. An aborting or aborted fail-fast run is unhealthy even when its triggering Seeder is optional. Seeder run state belongs to the current host process, rather than the scheduler's durable queue. `SeederFacade.GetSnapshotAsync()` returns current-host configuration plus a run snapshot with readiness status, counts, dependency blocks, and per-seeder attempt history. For a stuck startup, distinguish host startup from readiness and inspect blocked dependencies and each seeder's final attempt.

Shutdown before `ApplicationStarted`, including before the queued background operation begins, publishes a cancelled waiting run with no attempts. The hosted-service stopping hook owns that pre-start outcome; cancellation after the run starts continues through the scheduler's existing cancellation path.

`monica.AddSeederUI()` adds the read-only `/seeders` workbench and brings shell/localization dependencies; complete a Web host with `app.UseMonica()` and `app.MapMonica()`. The current-run summary and effective host defaults describe the whole snapshot. Search, status, criticality, and failure-behavior filters narrow only its execution ledger; clearing them preserves the snapshot and selected details. Terminal completion includes failed, blocked, and cancelled work, so full completion does not mean success. Seeder readiness is this subsystem's contribution, not aggregate host health. Empty discovery remains explicit.

The page polls once per second only while the run is waiting, running, or aborting, then stops when it reaches a terminal state. Refresh queries diagnostics; the initial-load retry also retries the query, not Seeder execution. Details show resolved policies with their individual default/override origins, direct dependencies resolved against the full snapshot, UTC timing, actual process-local attempts, and bounded diagnostics. Failed refreshes preserve older evidence with a warning; access is rechecked at each query boundary. There are no rerun or policy-edit commands and no durable multi-run history.

Seeder graph, defaults, and diagnostics: `Monica.Framework/Modules/ModuleSeeder.cs`, `Monica.Framework/Seeder/Models/Internal/SeederGraph.cs`, `Monica.Framework/Seeder/Annotations/SeederPolicyAttribute.cs`, and `Monica.Framework/Seeder/Facades/SeederFacade.cs`. UI: `Monica.Framework.UI/Modules/ModuleSeederUI.cs`, `Pages/UISeederPage.razor`, and `UISeeder/State/SeederPageSession.cs`. Tests: `tests/Test.Monica.Framework/Seeder/ModuleSeederTests.cs`, `tests/Test.Monica.Framework/Seeder/SeederFacadeTests.cs`, `tests/Test.Monica.Framework/Seeder/SeederHealthCheckTests.cs`, and the pure session checks in `tests/Test.Monica.Framework.UI/Seeder/SeederPageSessionTests.cs`.
