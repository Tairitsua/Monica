# Shared Application Specification

The `monica-application*` skills form the shared specification for Monica applications. The router owns applicability and evolution; the unified architecture owns topology and boundaries; the legacy microservice and modular-monolith skills cover split/combined deployment and migration; Building Block development owns building block responsibilities and kinds; application testing owns verification boundaries. Application UI owns the presentation branch and its development workflow; role placement and UI inclusion are independent selections. Pure backend applications need no UI skills. Infrastructure skills explain the native capabilities used by these patterns.

Read [unified architecture](03-unified-architecture.md) for the single domain-core/role-project layout, selective host composition, generated ownership catalogs, placement, activation, API and migration ownership. Backend-only and backend-with-UI remain independent skill installation choices.

Use [startup and local execution](04-startup-and-local-execution.md) for configuration authority, environment inputs and the single-host Manual profile. These use the same domain/role placement and native lifecycle; local development adds no separate application model.

Use [database layout and migrations](05-database-migrations.md) for migration placement, schema and history ownership, deployment preflight, and consolidation. Inspect mappings in the model and migration projects. Consult deployment assets for installation and rollback procedures.

## Rule Strength

| Kind | Meaning |
| --- | --- |
| **Mandatory** | A requirement expressed by `must`, `must not`, a rule, or a required convention applies within the adopted profile. A local deviation needs an explicit human decision with an exact scope. |
| **Allowed** | An option introduced by `may`, `optional`, or a stated selection condition is available within its documented boundary. Select and combine allowed capabilities as the authorized feature needs them, preserving each capability's prerequisites and contracts. A permitted combination needs no separate architectural approval. |
| **Example** | Code blocks, business names, placeholder values, and illustrative trees demonstrate a rule. Create only the types and folders the feature needs. An example does not require an extra service, event, job, or abstraction. |

Recommendations and defaults guide selection among allowed choices. When a project adopts a particular convention as a rule, its project specification records that choice. Runtime discoverability and successful compilation establish technical eligibility; they do not establish compliance, approval, or fitness for a feature.

## Shared and Project Ownership

Shared skills own the unified domain/role architecture, role vocabulary, dependency direction, lifecycle boundaries, native extension mechanisms, and test patterns. The repository-owned `<project>-application-spec` records only adopted local choices or exceptions that change how future code is written. Locate it through `AGENTS.md` or equivalent. See the [project spec contract](02-project-specification-skill.md) for what qualifies.

[Application test project naming](../../monica-application-unit-testing/references/project-naming.md) follows the production behavior owner across domain cores, roles, shared libraries, and executable hosts. Find the test root and shared support library in the repository.

The project specification links to shared rules instead of copying their catalog. A local specialization preserves its native building block kind and preserves the role's ownership and execution contracts. Existing code is evidence of current facts, including drift; it is not implicit approval for a new pattern. New work follows the adopted convention while older code is brought into line.

## Apply Rules to a Change

1. Load this router and the designated project specification, then read the affected architecture, building block, and test references.
2. Identify the owning state and business boundary. Entities protect their invariants. Handlers own application entry and execution policy. A DomainService may carry out a complete business operation within its domain, even for a single caller. Repositories stage and materialize persistence operations.
3. Map the work to existing Building Block responsibilities and compose native capabilities already allowed by the profile. New concrete building blocks, business state transitions, delivery steps, and documented transaction-boundary choices are ordinary implementation when they preserve those contracts. The specification supplies composable building blocks; it need not contain an exact recipe for every business flow.
4. If no compliant composition meets the requirement, identify the specific conflicting or missing architectural contract and use [specification evolution](01-specification-evolution.md) before changing it. An unfamiliar combination or an absent end-to-end example alone is not a specification gap. Keep independent work inside the current contract moving.
5. Validate the changed behavior. Update the project spec only if the change affects a recorded convention or introduces an approved difference.

Source and executable tests establish current framework behavior. A mismatch with the specification requires correction or an explicit proposal; neither automatically overrides the approved architectural intent.
