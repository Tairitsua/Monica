---
name: monica-infra-hosting
description: Compose a Monica host and use module diagnostics, conventional DI, hosted services, service discovery, and state stores. Use for application setup or runtime integration; use monica-development when authoring a module.
---

# Monica infrastructure hosting

Start with composition for module-graph or host-lifecycle problems, then select the runtime capability involved.

| Task | Read |
| --- | --- |
| Compose a Web or generic host, select discovery assemblies, or inspect module diagnostics | [Host composition](references/host-composition.md) |
| Discover application services, choose DI lifetimes, or decorate an interface | [Dependency injection](references/dependency-injection.md) |
| Register workers or diagnose their startup, state, and shutdown | [Hosted services](references/hosted-services.md) |
| Configure registry/worker roles or inspect registered services | [Service discovery](references/service-discovery.md) |
| Store key-value state using a memory, distributed, or keyed provider | [State stores](references/state-stores.md) |
| Inspect local or remote failures and choose response diagnostic visibility | [Exception diagnostics](references/exception-diagnostics.md) |

For database aggregates and write boundaries, use [persistence](../monica-infra-persistence/SKILL.md); for telemetry, use [observability](../monica-infra-observability/SKILL.md); for module implementation, use [development](../monica-development/SKILL.md).
