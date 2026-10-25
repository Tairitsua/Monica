---
name: monica-application
description: Apply the shared Monica application specification and the repository's project specification before designing, implementing, refactoring, or reviewing a Monica application, its boundaries, or ProjectUnits.
---

# Monica Application

Use this skill before application design, implementation, refactoring, or review, including when entering through a granular application skill. Read the [shared specification](references/00-application-specification.md) and [unified domain/role architecture](references/03-unified-architecture.md), then the repository-designated `<project>-application-spec` skill and the references relevant to the affected boundary. That project skill owns the application's actual architecture and adopted conventions.

Read [specification evolution](references/01-specification-evolution.md) when a gap, deviation, or new pattern is involved. Use the [project specification contract and template](references/02-project-specification-skill.md) when adopting or maintaining a repository specification. A missing project specification does not authorize inventing its architecture; inspect the source and prepare a concrete adoption proposal while continuing work within already approved patterns.

## Routing

- Runtime failures, missing routes, inactive UI, or unsuccessful infrastructure operations: use `$monica-troubleshooting` to select the failed boundary and its diagnostic guidance.

- Business presentation layout, references, product navigation, or local/remote calls: use `$monica-application-ui`; implement its pages and state with `$monica-application-ui-development`.

- Exact Monica source or an unavailable named skill/reference: use `$monica-guide source resolve --repository monica --json` as a read-only locator, then read canonical `skills/<name>/SKILL.md` and the specified reference.
- Split role placement or migration from existing service hosts: use `$monica-application-microservice`.
- Combined role placement or migration from merged monolith projects: use `$monica-application-modular-monolith`.
- Feature implementation or review of handlers, entities, repositories, events, jobs, seeders, host services, CRUD adapters, controllers, Facades, and other support types: use `$monica-application-project-unit-development`.
- Sociable application tests, `MonicaTestApplicationFactory`, handler or repository scenarios, database isolation, and migration away from mock-heavy tests: use `$monica-application-unit-testing`.
- Consumer setup and use of Monica infrastructure: route to `$monica-infra-persistence`, `$monica-infra-messaging`, `$monica-infra-jobs`, `$monica-infra-configuration`, `$monica-infra-hosting`, `$monica-infra-observability`, `$monica-infra-ai`, `$monica-infra-web`, or `$monica-infra-ui` for the capability in use.

## Apply the Specification

Use the unified domain/role layout and the deployment placement adopted by the project specification; do not choose a new deployment model for an ordinary feature. Load the matching architecture and unit references before changing their contracts. Reconcile affected project-specification facts and approved rules with the implementation in the same change. Record the rationale beside a rule; use Git history and existing project decision records for its evolution.
