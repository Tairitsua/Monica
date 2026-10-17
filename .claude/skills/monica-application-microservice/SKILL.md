---
name: monica-application-microservice
description: Apply the Monica microservice specification when designing, implementing, refactoring, or reviewing service ownership, project dependencies, published contracts, or persistence boundaries. Load monica-application and the repository's project specification first.
---

# Monica Application Microservice

Before using this profile, load `$monica-application` and the repository-designated project specification. The [shared specification](../monica-application/references/00-application-specification.md) defines mandatory rules, allowed options, and examples; the project specification records the adopted profile and actual topology. Use [specification evolution](../monica-application/references/01-specification-evolution.md) for a new pattern or deviation.

Split services by business capability and data ownership. The intended solution chain is `{Subdomain}Service.API → {Subdomain}Service.Domain → Platform.Infrastructure → Platform.Protocol → Platform.BuildingBlocks`. Put project-common libraries in `Platform.BuildingBlocks`, solution wiring in `Platform.Infrastructure`, shared business language in `Platform.Protocol`, and service-only dependencies in the owning Domain project. A service's API, Domain, and migrations move together; AppHost or gateway projects remain composition adapters. See [solution layout](references/00-solution-layout.md) and [new subdomain](references/01-create-subdomain-and-service.md) when changing topology.

Published operations belong in `Platform.Protocol.PublishedLanguages.Domain{Subdomain}.Requests`. `[ApiEndpoint(ApiHttpMethod, route)]` generates stable interfaces without publishing HTTP receivers. The host uses `ApplicationApiGenerationConfig` with `Http` for this profile and explicitly selects owned handler assemblies. UI and application callers inject these interfaces. HTTP metadata stays with requests even for Local calls; receiver publication stays with the host. Use the [RPC contract](../monica-infra-messaging/references/rpc.md) for generation and runtime composition.

Keep persistence entities and repositories inside the owning service. Cross-service collaborators consume deliberate Protocol contracts rather than another service's implementation. Read [service boundaries](references/03-service-boundaries-and-collaboration.md) for coordination choices. Use `$monica-application-project-unit-development` for handlers and domain units, and `$monica-infra-persistence`, `$monica-infra-messaging`, or `$monica-infra-configuration` for those infrastructure capabilities.
