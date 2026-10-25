---
name: monica-application-modular-monolith
description: Plan combined role deployments or migrate existing Monica monolith projects into the unified domain/role application model. Load monica-application and the project specification first.
---

# Combined deployment and monolith migration

Load `$monica-application`, the repository-designated project specification, and the [unified architecture](../monica-application/references/03-unified-architecture.md). New applications use that one domain/role layout. This skill covers deployment and migration; it does not define an alternative new-project structure.

Apply the [shared specification](../monica-application/references/00-application-specification.md) within the adopted project contracts. Use [specification evolution](../monica-application/references/01-specification-evolution.md) for a concrete architectural gap or rule change.

- [Combined deployment](references/00-solution-layout.md): assign included roles to one AppId; separate specialized runtime dependencies only where needed.
- [Migrate a bounded context](references/01-create-subdomain-and-domain.md): separate shared domain/persistence from role-owned application entries.
- [Published collaboration](references/02-protocol-platform-and-internal-collaboration.md): retain generated APIs and Local operation scopes.
- [Boundaries and persistence](references/03-boundaries-dependencies-and-persistence.md): keep domain stores explicit and migrations independent of host startup.

Use `$monica-application-project-unit-development` for feature units and matching `monica-infra-*` skills for native registration. Co-location does not grant peer repository access or atomic commits across independent stores.
