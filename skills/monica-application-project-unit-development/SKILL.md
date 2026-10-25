---
name: monica-application-project-unit-development
description: Apply Monica application unit rules when implementing, refactoring, or reviewing handlers, entities, repositories, events, jobs, seeders, host services, CRUD adapters, controllers, Facades, and support types. Load monica-application and the project specification first.
---

# Monica Application ProjectUnit Development

## Overview

Use this skill for unit-level application development in Monica-based DDD projects. Before changing or reviewing units, load `$monica-application` and the repository-designated project specification, even when this skill was invoked directly. The [shared specification](../monica-application/references/00-application-specification.md) defines rule strength; [specification evolution](../monica-application/references/01-specification-evolution.md) governs concrete architectural gaps and rule changes, not ordinary combinations of these units.

## Workflow

Use the [unified architecture](../monica-application/references/03-unified-architecture.md) and project specification to locate the owning domain core or role library. Use [unit overview](references/00-project-unit-overview.md) and [composition map](references/02-project-unit-composition-map.md) when the feature's units are unclear; load only the matching unit reference below. Keep invariants on entities and orchestration in handlers. A DomainService is an optional home for a meaningful domain rule with no natural entity owner, not a required hop for every command.

When adding or refactoring application EventBus events and consumers, including outbox or inbox adoption, load the [event contract](references/12-entity-request-event-template.md) and [handler](references/14-event-handler-template.md) templates before changing their delivery configuration.

## Ground Rules

- Prefer Monica-native types. Follow the [type naming, source filename, placement, boundary, and project-level extension rules](references/01-project-unit-naming-and-boundaries.md) when selecting a reusable specialization. Keep the application's concrete names and adopted extension families in its local guidance.
- Keep persistence concerns in repositories and persistence classes, not in request handlers. Use `$monica-infra-persistence` for the current operation, transaction, and provider contracts.
- Keep repository implementations in the owning subdomain or service infrastructure boundary. Do not move a repository or adapter to `Platform` just because it uses an external library; only project-common reusable infrastructure belongs in `Platform`.
- Keep contracts stable: requests, DTOs, and events are not persistence entities.
- Add explicit `[ProjectUnitMetadata]` to each application-authored discovered unit and `[ProjectUnitRequirement]` for applicable actual requirement IDs, following the [metadata and coverage-debt rules](references/03-project-unit-context-metadata.md). Do not inherit metadata or invent a requirement merely to fill the catalog.
- Keep metadata, description, and requirement traceability explicit. Resolve optional ownership through the [metadata rules](references/03-project-unit-context-metadata.md).
- If a handler returns `Res<string>`, use `Res.Ok<string>(value)` instead of `Res.Ok(value)` to avoid the non-generic string overload.
- Make HTTP contracts request-owned. Put `[ApiEndpoint]` on the request type and keep route, verb, and binding off the `ApplicationService` handler.
- A request is published for generated API clients only when it is attributed source in the exact namespace `*.PublishedLanguages.Domain{DomainName}.Requests`. Attributed requests outside that boundary remain local HTTP endpoints.
- Generate interfaces in Protocol independently of placement. The complete `[ApplicationHost]` generates per-operation clients and owned receivers; ordinary calls prefer Local and RunOnRole is reserved for a real execution requirement. See [unified architecture](../monica-application/references/03-unified-architecture.md) and [API generation](../monica-infra-messaging/references/invocation.md).
- Do not add MVC `[Http*]`, `[Route]`, or `[From*]` attributes to generated `ApplicationService` endpoints. The request contract is the single endpoint authority.
- Use transport-independent ApplicationService for use cases. `[ApiEndpoint(ApiHttpMethod, route)]` publishes a domain interface for UI and application callers while retaining HTTP metadata without exposing receivers. Unattributed requests and plain IRequestHandler remain internal mediator operations.
- For capability setup and runtime behavior, use `$monica-infra-configuration`, `$monica-infra-jobs`, `$monica-infra-messaging`, and `$monica-infra-web` as applicable. This skill owns the application unit's shape and placement.

## Reference Navigation

- Unit catalog and responsibilities: [00-project-unit-overview.md](references/00-project-unit-overview.md)
- Naming, placement, boundaries, and project-level extensions: [01-project-unit-naming-and-boundaries.md](references/01-project-unit-naming-and-boundaries.md)
- Unit selection by feature shape: [02-project-unit-composition-map.md](references/02-project-unit-composition-map.md)
- Metadata, ownership, and requirement traceability: [03-project-unit-context-metadata.md](references/03-project-unit-context-metadata.md)
- Bounded CRUD, seeders, hosted services, controllers, internal mediator handlers, Facades, and support types: [04-adapters-lifecycle-and-support-types.md](references/04-adapters-lifecycle-and-support-types.md)
- `ApplicationService` templates: [10-application-service-template.md](references/10-application-service-template.md)
- `DomainService` templates: [11-domain-service-template.md](references/11-domain-service-template.md)
- Entity, request, and event templates: [12-entity-request-event-template.md](references/12-entity-request-event-template.md)
- Repository and DbContext templates: [13-repository-and-persistence-template.md](references/13-repository-and-persistence-template.md)
- Event handler templates: [14-event-handler-template.md](references/14-event-handler-template.md)
- Job templates: [15-job-template.md](references/15-job-template.md)
- Continuous consumers, scoped work items, and shutdown: [18-hosted-service-template.md](references/18-hosted-service-template.md)
- Aggregate concurrency and Actor replacement: [19-aggregate-concurrency.md](references/19-aggregate-concurrency.md)
- Configuration templates: [16-configuration-template.md](references/16-configuration-template.md)
- Connected request, handler, entity, and repository example: [17-connected-use-case-example.md](references/17-connected-use-case-example.md)

## Scope Notes

- This skill is architecture-agnostic. It explains unit design, not solution topology.
- Use `monica-application-microservice` for split deployment and migration from legacy service hosts.
- Use `monica-application-modular-monolith` for combined deployment and migration from merged legacy contexts.
