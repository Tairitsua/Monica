# Startup seeders

`monica.AddSeeder()` discovers concrete `ISeeder` implementations, builds a dependency graph, registers eligible seeders as transient, and starts its scheduler **after** the Generic Host publishes `ApplicationStarted`. HostedService and HealthCheck dependencies are registered automatically. The scheduler remains available in Manual mode to execute explicit requests; this admission never starts held product work. Derive from `SeederBase` for a host logger and override `SeedingAsync`:

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

The `Seeder` activation boundary separates discovery from execution. Automatic seeders run after application startup; Manual seeders remain `Held` until explicitly selected; Disabled definitions remain visible but receive no executable service registration. With `AddApplicationModel`, role placement and domain `BackgroundRole` determine ownership. Standalone hosts use the same `Application:AutonomousWork` input, which defaults to true. Missing declarations and cycles remain graph errors even when a branch is not scheduled.

`[ProcessLocal]` is reserved for finite initialization needed by included passive behavior that only changes process memory. Read-only input access is allowed; shared writes, event publication, locks, queue claims, and service advertisement are not. Such initialization may be Automatic in a Manual host, but it cannot start a Manual or Disabled prerequisite. The initializer remains Held, with the blocking dependency chain visible, and still contributes its Required/Optional readiness semantics. Completing a prerequisite does not implicitly run downstream seeders: select the initializer explicitly when ready.

`ISeederExecutor.RunAsync(fullSeederTypeName, cancellationToken)` and `SeederFacade.RunAsync(...)` use the same native scheduler and execution pipeline. The target identity must match a discovered full type name. A run executes the selected target and only its unsatisfied eligible prerequisites; already successful prerequisites and unrelated held seeders are untouched. Selecting a successful target intentionally reruns that target. Disabled dependencies reject the whole request before anything is resolved. Only one batch can execute at a time, including startup; requests before startup scheduling completes or while another batch is running fail admission. Cancellation cooperatively stops the admitted batch without cancelling the host; host shutdown also cancels explicit work. Attempt history remains process-local and cumulative, while `MaxAttempts` applies separately to each run.

Seeder execution defaults to an automatic UnitOfWork when that behavior is registered. Use the persistence [transaction contract](../../monica-infra-persistence/references/transactions.md) when a seeder needs a transaction override or explicit context selection. For `SeederBase`, declare it on an overridden `SeedAsync` entry method; `SeedingAsync` is the inner work method and does not own the execution boundary.

The `monica.seeder` readiness check ignores unrequested Manual seeders and Disabled definitions. Automatic initialization and explicitly admitted work retain Required/Optional semantics: a Required failure, cancellation, or blocked execution remains unhealthy until a successful explicit retry; running an unrelated batch never clears that evidence. Ordinary optional failures degrade readiness. A failed fail-fast seeder remains unhealthy even if optional. Held is never success. `ISeederState.IsSucceeded(typeof(RequiredInitializer))` lets a dependent API check its real prerequisite and return its existing unavailable result without implicitly starting work. There is no inferred API dependency graph.

Seeder state belongs to the current host process, rather than a durable queue. `SeederFacade.GetSnapshotAsync()` returns current-host configuration plus the latest batch status and all per-seeder outcomes, including activation reasons, readiness participation, dependency blocks, and attempt history. `Held` run status means no batch is executing and held definitions remain; it does not mean initialization succeeded. For a stuck startup, distinguish host startup from readiness and inspect the prerequisite chain.

Shutdown before `ApplicationStarted`, including before the queued background operation begins, publishes a cancelled waiting run with no attempts. The hosted-service stopping hook owns that pre-start outcome; cancellation after the run starts continues through the scheduler's existing cancellation path.

`monica.AddSeederUI()` adds the `/seeders` workbench and brings shell/localization dependencies; complete a Web host with `app.UseMonica()` and `app.MapMonica()`. The current-run summary and effective host defaults describe the whole snapshot. Search, status, criticality, and failure-behavior filters narrow only its execution ledger; clearing them preserves the snapshot and selected details. Terminal completion includes failed, blocked, and cancelled work, but excludes Held and Disabled definitions. Completion does not mean success. Seeder readiness is this subsystem's contribution, not aggregate host health. Empty discovery remains explicit.

The page polls once per second while startup or a batch is active and stops while idle. Refresh and initial-load retry only query diagnostics. The existing details drawer has a separate **Run with prerequisites** action explaining that execution may mutate shared resources. Access is rechecked before execution as well as reads; disabled definitions and concurrent submissions cannot be run. Leaving the page cancels and observes its outstanding request. Details show resolved policies, direct dependencies, UTC timing, cumulative attempts, and bounded diagnostics. Failed refreshes preserve older evidence with a warning. There are no policy-edit commands or durable multi-run history.

The details drawer stays attached to the browser viewport while the ledger scrolls; its header and Close action remain visible while evidence scrolls inside the panel. It uses the shared [viewport drawer placement](../../monica-infra-ui/references/theme-and-localization.md) contract.

Seeder graph, defaults, and diagnostics: `Monica.Framework/Modules/ModuleSeeder.cs`, `Monica.Framework/Seeder/Models/Internal/SeederGraph.cs`, `Monica.Framework/Seeder/Annotations/SeederPolicyAttribute.cs`, and `Monica.Framework/Seeder/Facades/SeederFacade.cs`. UI: `Monica.Framework.UI/Modules/ModuleSeederUI.cs`, `Pages/UISeederPage.razor`, and `UISeeder/State/SeederPageSession.cs`. Tests: `tests/Test.Monica.Framework/Seeder/ModuleSeederTests.cs`, `tests/Test.Monica.Framework/Seeder/SeederFacadeTests.cs`, `tests/Test.Monica.Framework/Seeder/SeederHealthCheckTests.cs`, and the pure session checks in `tests/Test.Monica.Framework.UI/Seeder/SeederPageSessionTests.cs`.
