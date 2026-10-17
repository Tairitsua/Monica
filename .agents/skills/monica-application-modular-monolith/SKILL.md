---
name: monica-application-modular-monolith
description: Apply the Monica modular-monolith specification when designing, implementing, refactoring, or reviewing domain ownership, host composition, project dependencies, contracts, or persistence. Load monica-application and the repository's project specification first.
---

# Monica Application Modular Monolith

Before using this profile, load `$monica-application` and the repository-designated project specification. The [shared specification](../monica-application/references/00-application-specification.md) defines mandatory rules, allowed options, and examples; the project specification records the adopted profile and actual topology. Use [specification evolution](../monica-application/references/01-specification-evolution.md) for a new pattern or deviation.

Keep one deployment with business boundaries under `Domains/`, rather than global Application/Domain/Infrastructure buckets. Each bounded context uses one `Domains.{Subdomain}.csproj` containing its application units, domain model, and domain-owned infrastructure. The intended chain is `AppHost → Domains.{Subdomain} → Platform.Infrastructure → Platform.Protocol → Platform.BuildingBlocks`. Put project-common libraries in BuildingBlocks, solution wiring in Infrastructure, and subdomain-only dependencies in the owning project. AppHost remains composition-only. See [solution layout](references/00-solution-layout.md) and [new bounded context](references/01-create-subdomain-and-domain.md).

Published operations belong in `Platform.Protocol.PublishedLanguages.Domain{Subdomain}.Requests`. `[ApiEndpoint(ApiHttpMethod, route)]` generates stable interfaces without publishing HTTP receivers. The host uses `ApplicationApiGenerationConfig` with `Local` for this profile and explicitly selects owned handler assemblies. UI and application callers inject these interfaces. HTTP metadata stays with requests even for Local calls; receiver publication stays with the host. Use the [RPC contract](../monica-infra-messaging/references/rpc.md) for generation and runtime composition.

Keep domain-owned handlers in `Application/HandlersCommand`, `HandlersQuery`, `HandlersEvent`, and `BackgroundWorkers`; repository implementations and context files belong in `Repository/`. Read [boundaries and persistence](references/03-boundaries-dependencies-and-persistence.md) when placing data and host dependencies. Use `$monica-application-project-unit-development` for individual units, and `$monica-infra-persistence`, `$monica-infra-messaging`, or `$monica-infra-configuration` for those capabilities.
