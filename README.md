# Monica

<p align="center">
  <img src="logo.png" alt="Monica Logo" width="176" />
</p>

<p align="center">
  <strong>Architecture agents can follow. Systems humans can inspect.</strong>
</p>

<p align="center">
  <a href="https://github.com/Tairitsua/Monica/actions/workflows/unit-tests.yml"><img src="https://github.com/Tairitsua/Monica/actions/workflows/unit-tests.yml/badge.svg" alt="Build"></a>
  <a href="https://www.nuget.org/packages?q=Monica"><img src="https://img.shields.io/nuget/v/Monica.Core.svg" alt="NuGet"></a>
  <a href="https://github.com/Tairitsua/Monica/blob/main/LICENSE.txt"><img src="https://img.shields.io/github/license/Tairitsua/Monica" alt="License"></a>
  <a href="https://monica.dpdns.org/"><img src="https://img.shields.io/badge/docs-monica.dpdns.org-6d5ce7.svg" alt="Documentation"></a>
</p>

<p align="center">
  English | <a href="README.zh_CN.md">简体中文</a>
</p>

Monica is modular .NET infrastructure for agent-driven development. Applications can adopt its modules independently, while developers and coding agents share a typed vocabulary for composition, DDD application units, and runtime diagnostics. That makes generated code structurally predictable and running systems inspectable.

> Monica is approaching 1.0 and is still allowed to make breaking architectural improvements before the stable release.

## Why Monica

- **A graph, not startup glue.** `AddMonica(...)` records the complete module graph for one host, validates dependencies and cycles, then applies registrations in deterministic phases.
- **A vocabulary agents can follow.** ProjectUnits describe application services, domain services, entities, repositories, events, handlers, configurations, and jobs as explicit architectural roles.
- **Inspectable by default.** Operational modules expose the same graph, configuration, jobs, telemetry, and application structure that produced the running process.
- **Host-scoped state.** A Monica composition belongs to its host. Multiple hosts in one process do not share module registries, options, mapping rules, or ProjectUnit catalogs.

## Start in one minute

Install only the packages used by your application:

```bash
dotnet add package Monica.ProjectUnits --prerelease
dotnet add package Monica.JobScheduler --prerelease
dotnet add package Monica.JobScheduler.UI --prerelease
```

Compose the host in one bounded callback:

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

    monica.ConfigureModuleSystem(options =>
    {
        options.DefaultApiGroupName = "Orders";
    });

    monica.AddProjectUnits(options =>
    {
        options.ConventionOptions.EnableNameConvention = true;
    });

    monica.AddJobScheduler(options =>
        {
            options.ProjectName = "Orders";
        })
        .UseSchedulerScope("orders")
        .UseInMemoryStore();

    monica.AddJobSchedulerUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

With a durable production store, execution leases provide at-least-once recovery after a worker crash, while fencing rejects mutations from an expired lease. Each host synchronizes, schedules, and executes the jobs it discovers under its own owner identity; replicas coordinate through durable compare-and-swap operations. Concurrency gates are keyed by scheduler scope, owner, and `JobKey`. Fencing cannot make arbitrary job side effects exactly-once, so jobs should use idempotency keys or transactional business boundaries for those effects.

The JobScheduler UI workspace includes a runtime tab showing the host's effective configuration, storage identity, local scheduling-plane readiness, and every owner's durable footprint with a documented liveness threshold (three snapshot-sync intervals). Hosts that also register ServiceDiscovery can add `monica.AddJobSchedulerServiceDiscoveryInsight()` (package `Monica.JobScheduler.ServiceDiscovery`) to enrich that tab with live worker-instance heartbeats; without it, the tab hides the worker table behind a setup hint instead of failing.

The callback is the complete composition boundary. Captured `ModuleRegistration<,>` handles cannot mutate the graph after it closes, and startup fails early when the graph is incomplete or cyclic.

For a runnable DDD application rather than a toy snippet, see [`examples/Monica.ReferenceApplication`](examples/Monica.ReferenceApplication). For the smallest operational scheduler UI host, see [`examples/JobSchedulerMinimal`](examples/JobSchedulerMinimal).

## The architecture contract

Every module follows the same public shape:

- `Module{Name} : MonicaModule<Module{Name}Option>` — the host-owned lifecycle strategy.
- `Module{Name}Option : ModuleOptions<Module{Name}>` — startup-frozen configuration and defaults.
- `Module{Name}BuilderExtensions` — the discoverable `monica.Add{Name}()` entry point, returning a host-bound `ModuleRegistration<,>`.
- `Module{Name}RegistrationExtensions` — optional provider and capability choices that enrich that registration.

The strategy declares hard dependencies and optional ordering in `Describe(ModuleDescriptor)`. It reads its own finalized `Option` directly and can read another module's options only through one of those declared relationships. Business-type work is declared once through `DeclareTypeDiscovery(...)`; Monica combines non-empty structural queries into one type-universe scan and commits matches serially in graph order.

ProjectUnits add the application vocabulary:

`ApplicationService` · `RequestDto` · `DomainService` · `Entity` · `Repository` · `DomainEvent` · `DomainEventHandler` · `LocalEventHandler` · `Configuration` · `RecurringJob` · `TriggeredJob`

Each discovered unit can also carry explicit agent context and requirement traceability:

```csharp
[ProjectUnitMetadata(
    "Approve Order",
    Owner = "Ordering Team",
    Description = "Approves an eligible order.",
    Tags = ["ordering", "approval"])]
[ProjectUnitRequirement("ORD-REQ-001")]
public sealed class CommandHandlerApproveOrder : ApplicationService<CommandApproveOrder>
{
    // ...
}
```

`Monica.Framework.UI` exposes a first-tab status dashboard for the current host with unit distribution, dependency health, alerts, and independent metadata, description, ownership, and requirement coverage. The typed `/framework/units`, `/framework/units/dashboard`, and `/framework/units/{key}` APIs expose the same catalog without leaking reflection objects.

## Module diagnostics

Hosts opt in with `monica.AddModuleSystem()`. Its `ModuleDiagnosticsFacade` exposes one immutable, revisioned snapshot of composition outcome, timings, callbacks, startup work, type-discovery stages, findings, and direct dependency topology. Assembly inventory is lazy, option diagnostics are loaded separately, and portable exports omit option data, assembly paths, stack traces, and raw exception details. `monica.AddModuleSystemUI()` includes the Core diagnostics module automatically.

Every public module-option property remains visible by clean type name. Ordinary bounded values are shown automatically; `[ModuleOptionDiagnosticsSensitive]` or a host policy reduces sensitive content to a non-secret presence, count, or protected-address representation as appropriate. `RevealSensitive` can reveal only bounded sensitive scalars and is restricted to Development-only debugging. The Module System workbench is also Development-only by default; exposing it elsewhere requires both explicit enablement and a host authorization policy.

## Knowledge for coding agents

The canonical Monica Agent Skills live under [`skills/`](skills/). Nine consumer skills are the single source of reusable infrastructure usage guidance: [`monica-infra-persistence`](skills/monica-infra-persistence), [`monica-infra-messaging`](skills/monica-infra-messaging), [`monica-infra-jobs`](skills/monica-infra-jobs), [`monica-infra-configuration`](skills/monica-infra-configuration), [`monica-infra-hosting`](skills/monica-infra-hosting), [`monica-infra-observability`](skills/monica-infra-observability), [`monica-infra-ai`](skills/monica-infra-ai), [`monica-infra-web`](skills/monica-infra-web), and [`monica-infra-ui`](skills/monica-infra-ui). Other skills cover application architecture, framework authoring, testing, UI development, and contribution workflows. Start with `monica-guide` for installation and framework source discovery, then use the skill that owns the task.

Repository-local discovery copies under `.agents/skills/` and `.claude/skills/` are generated on demand by `python scripts/sync_agent_skills.py --write`; they are local agent state and are not tracked in git. [Monica.Docs](https://monica.dpdns.org/) publishes the skills from an immutable release knowledge archive (`monica-knowledge.json` and its checksum) alongside stable editorial guides. Local site development can preview the current source checkout.

## Third-party ecosystem

Independent packages use the publisher-first ID `<Publisher>.Monica.<Package>[.<Variant>]`; one NuGet package may contain any coherent number of infrastructure, provider, web, and UI modules. The official `Monica.*` prefix and purple logo remain reserved for first-party packages.

Start with the [`monica-third-party-module-development`](skills/monica-third-party-module-development) skill to scaffold, validate, test, pack, and publish an independent package. The compatibility identity and usage rules are summarized in [BRANDING.md](BRANDING.md), with the complete bilingual guide in [Monica.Docs](https://monica.dpdns.org/).

## Package maturity

The maturity label describes the compatibility promise, not package quality.

| Tier | Promise | Representative packages |
|---|---|---|
| **Stable** | The supported 1.0 application path | Core, ProjectUnits, WebApi, Configuration, Repository, JobScheduler, OpenTelemetry, UI |
| **Integrations** | Versioned adapters around external systems | EF Core, Kafka, Redis/StackExchange, Dapr, SignalR |
| **Labs** | Deliberately fast-moving exploration | AI/RAG/MCP, DataChannel, DevOps and profiling, Office, Experimental |

Applications can adopt Stable without taking a dependency on Labs. Integration packages remain optional and provider-specific; CI validates both complete package classification and one-way maturity dependencies.

## Runtime surfaces

Monica includes operational Blazor surfaces for module dependencies, ProjectUnits, configuration, jobs, dependency injection, telemetry, repositories, and other registered capabilities. They share a theme contract and first-party `en-US` and `zh-CN` localization resources.

The one-minute demo shows JobScheduler operations, module dependency inspection, and runtime configuration:

https://github.com/user-attachments/assets/250e1e5f-0a78-4b8b-b832-756d682a01bd

## Documentation and community

- Documentation: <https://monica.dpdns.org/>
- Read-only documentation API: <https://api.monica.dpdns.org/>
- Example documentation host: <https://github.com/Tairitsua/Monica.Docs>
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Issues: <https://github.com/Tairitsua/Monica/issues>
- Discussions: <https://github.com/Tairitsua/Monica/discussions>
- Security: [SECURITY.md](SECURITY.md)

Contribution and release guidance is in [CONTRIBUTING.md](CONTRIBUTING.md).

## License and acknowledgements

Monica is MIT licensed. See [LICENSE.txt](LICENSE.txt).

A small subset of Monica modules was informed by the [ABP Framework](https://github.com/abpframework/abp), licensed under LGPL-3.0. Directly adapted code retains its original notices. Monica is an independent project and is not affiliated with ABP.
