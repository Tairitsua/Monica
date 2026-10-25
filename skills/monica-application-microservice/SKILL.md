---
name: monica-application-microservice
description: Plan split role deployments or migrate existing Monica service hosts into the unified domain/role application model. Load monica-application and the project specification first.
---

# Split deployment and service migration

Load `$monica-application`, the repository-designated project specification, and the [unified architecture](../monica-application/references/03-unified-architecture.md). New applications use that one domain/role layout. This skill covers deployment and migration; it does not define an alternative new-project structure.

Apply the [shared specification](../monica-application/references/00-application-specification.md) within the adopted project contracts. Use [specification evolution](../monica-application/references/01-specification-evolution.md) for a concrete architectural gap or rule change.

- [Deployment layout](references/00-solution-layout.md): place roles under separate AppIds using shared or justified specialized host artifacts.
- [Migrate an existing service](references/01-create-subdomain-and-service.md): map API/Domain projects into domain cores and roles while preserving behavior.
- [Contracts and runtime composition](references/02-protocol-platform-and-service-contracts.md): preserve one published domain API, route only constrained operations remotely, and select ingress.
- [Boundaries and persistence](references/03-service-boundaries-and-collaboration.md): preserve domain data ownership and explicit consistency across stores.

Use `$monica-application-project-unit-development` for feature units and matching `monica-infra-*` skills for native registration. Existing service names, wire routes, durable identities and operating modes require deliberate preservation or an approved migration.
