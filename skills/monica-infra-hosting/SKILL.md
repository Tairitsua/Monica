---
name: monica-infra-hosting
description: Compose a Monica host and use module diagnostics, conventional DI, hosted services, distributed locks, service discovery, and state stores. Use for application setup or runtime integration; use monica-development when authoring a module.
---

# Monica infrastructure hosting

Start with composition for module-graph or host-lifecycle problems, then select the runtime capability involved.

| Task | Read |
| --- | --- |
| Compose a Web or generic host, select discovery assemblies, or inspect module diagnostics | [Host composition](references/host-composition.md) |
| Contribute host-owned object-mapping profiles or review mapping upgrade compatibility | [Object mappings](references/host-composition.md#contribute-object-mappings) |
| Compose domain/role ownership, deployment placement, or autonomous activation | [Unified application model](references/application-model.md) |
| Discover application services, choose DI lifetimes, or decorate an interface | [Dependency injection](references/dependency-injection.md) |
| Register workers or diagnose their startup, state, and shutdown | [Hosted services](references/hosted-services.md) |
| Select a lock provider, configure shared lock keys, or check lease constraints | [Lockers](references/lockers.md) |
| Register an observable object or inspect captured state, retained history, and exceptions | [Observable instance evidence](references/observable-instances.md) |
| Configure registry/worker roles or inspect registered services | [Service discovery](references/service-discovery.md) |
| Store key-value state using a memory, distributed, or keyed provider | [State stores](references/state-stores.md) |
| Inspect local or remote failures and choose response diagnostic visibility | [Exception diagnostics](references/exception-diagnostics.md) |

For database aggregates and write boundaries, use [persistence](../monica-infra-persistence/SKILL.md); for telemetry, use [observability](../monica-infra-observability/SKILL.md). For module implementation, use `$monica-framework`, then `$monica-development`.

If a routed authoring skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md`.
