# Monica

**Architecture agents can follow. Systems humans can inspect.**

Monica is an agent-governed application architecture for observable .NET backends. Its packages provide host-scoped module composition, typed DDD Building Blocks, infrastructure adapters, operational dashboards, and runtime diagnostics.

## Install

Install only the capabilities the host uses:

```bash
dotnet add package Monica.BuildingBlocks --prerelease
dotnet add package Monica.JobScheduler --prerelease
dotnet add package Monica.JobScheduler.UI --prerelease
```

## Compose one host

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);

builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options =>
    {
        options.AppName = "Orders";
        options.AppId = "orders";
    });

    monica.AddEventBus().UseNoOpDistributedEventBus();
    monica.AddBuildingBlocks();

    monica.AddJobScheduler()
        .UseSchedulerScope("orders")
        .UseInMemoryStore();

    monica.AddJobSchedulerUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

With a durable production store, execution leases provide at-least-once recovery after a worker crash, while fencing rejects mutations from an expired lease. Each host schedules and executes the jobs it discovers under its own owner identity, and replicas coordinate through durable compare-and-swap operations. Concurrency gates use scheduler scope, owner, and logical `JobKey`. Jobs must still make external side effects idempotent or transactional; fencing does not turn arbitrary business effects into exactly-once operations.

`AddMonica(...)` records and validates the complete module graph for this host before applying registrations. The composition is isolated from other hosts in the same process.

Each `monica.Add*()` call returns a host-bound `ModuleRegistration<,>` that provider and capability extensions enrich inside the same callback. Module strategies declare dependencies with `Describe(ModuleDescriptor)` and declare structural business-type queries with `DeclareTypeDiscovery(...)`; Monica evaluates all non-empty plans through one type-universe scan and commits their matches deterministically.

Hosts that call `monica.AddModuleSystem()` receive an immutable, revisioned diagnostics snapshot, lazy assembly inventory, bounded option catalog with sensitive-value redaction, and sanitized portable exports. When `Monica.UI` is installed, `monica.AddModuleSystemUI()` includes that Core diagnostics module automatically.

## Maturity tiers

- **Stable:** Core, BuildingBlocks, WebApi, Configuration, Repository, JobScheduler, OpenTelemetry, and UI runtime inspection.
- **Integrations:** optional EF Core, Kafka, Redis/StackExchange, Dapr, SignalR, and other provider adapters.
- **Labs:** fast-moving AI/RAG/MCP, DataChannel, DevOps and profiling, Office, and Experimental capabilities.

The package catalog is validated in CI: Stable can depend only on Stable, Integrations can depend on Stable or Integrations, and Labs may opt into any tier.

## Learn more

- Documentation: https://monica.dpdns.org/
- Reference application: https://github.com/Tairitsua/Monica/tree/dev/examples/Monica.ReferenceApplication
- Repository: https://github.com/Tairitsua/Monica
- Issues: https://github.com/Tairitsua/Monica/issues

Monica is MIT licensed and remains pre-1.0 while its public architecture is finalized.
