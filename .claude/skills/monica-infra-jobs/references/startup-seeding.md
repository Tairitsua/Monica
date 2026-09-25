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

The `monica.seeder` readiness check remains unhealthy until required seeders succeed. Optional failures degrade the seeder state, while required failures keep it unhealthy. Seeder run state belongs to the current host process, rather than the scheduler's durable queue. `SeederFacade.GetSnapshotAsync()` returns current-host configuration plus a run snapshot with readiness status, counts, dependency blocks, and per-seeder attempt history. `monica.AddSeederUI()` adds a read-only current-host dashboard and brings shell/localization dependencies; complete a Web host with `app.UseMonica()` and `app.MapMonica()`. For a stuck startup, distinguish host startup from readiness and inspect blocked dependencies and each seeder's final attempt.

Seeder graph, defaults, and diagnostics: `Monica.Framework/Modules/ModuleSeeder.cs`, `Monica.Framework/Seeder/Models/Internal/SeederGraph.cs`, `Monica.Framework/Seeder/Annotations/SeederPolicyAttribute.cs`, and `Monica.Framework/Seeder/Facades/SeederFacade.cs`. UI: `Monica.Framework.UI/Modules/ModuleSeederUI.cs`. Tests: `tests/Test.Monica.Framework/Seeder/ModuleSeederTests.cs`, `tests/Test.Monica.Framework/Seeder/SeederFacadeTests.cs`, and `tests/Test.Monica.Framework/Seeder/SeederHealthCheckTests.cs`.
