# Shared Application Specification

The `monica-application*` skills form the shared specification for Monica applications. The router owns applicability and evolution; the unified architecture owns topology and boundaries; the legacy microservice and modular-monolith skills cover split/combined deployment and migration; ProjectUnit development owns unit responsibilities and shapes; application testing owns verification boundaries. Application UI owns the presentation branch and its development workflow; role placement and UI inclusion are independent selections. Pure backend applications need no UI skills. Infrastructure skills explain the native capabilities used by these patterns.

Read [unified architecture](03-unified-architecture.md) for the single domain-core/role-project layout, selective host composition, generated ownership catalogs, placement, activation, API and migration ownership. Backend-only and backend-with-UI remain independent skill installation choices.

Use [startup and local execution](04-startup-and-local-execution.md) for configuration authority, environment inputs and the single-host Manual profile. These use the same domain/role placement and native lifecycle; local development adds no separate application model.

## Rule Strength

| Kind | Meaning |
| --- | --- |
| **Mandatory** | A requirement expressed by `must`, `must not`, a rule, or a required convention applies within the adopted profile. A local deviation needs an explicit human decision with an exact scope. |
| **Allowed** | An option introduced by `may`, `optional`, or a stated selection condition is available within its documented boundary. Select it when the feature needs it; permission to use one option does not authorize a new role or execution model. |
| **Example** | Code blocks, business names, placeholder values, and illustrative trees demonstrate a rule. Create only the types and folders the feature needs. An example does not require an extra service, event, job, or abstraction. |

Recommendations and defaults guide selection among allowed choices. When a project adopts a particular convention as a rule, its project specification records that choice. Runtime discoverability and successful compilation establish technical eligibility; they do not establish compliance, approval, or fitness for a feature.

## Shared and Project Ownership

Shared skills own the unified domain/role architecture, role vocabulary, dependency direction, lifecycle boundaries, native extension mechanisms, and test patterns. The repository-owned `<project>-application-spec` owns the adopted layout and deployment placement, actual domains/roles/hosts, dependency map, local naming and placement, adopted specializations, explicit deviations, validation commands, and known migration gaps. Locate it through `AGENTS.md` or the repository's equivalent instructions. Use its references as the current application architecture source; do not maintain a parallel `docs/architecture.md` or Workflow architecture topic.

[Application test project naming](../../monica-application-unit-testing/references/project-naming.md) follows the production behavior owner across domain cores, roles, shared libraries, and executable hosts. The project specification records its test root, shared support library, concrete migration mappings, and remaining mixed-owner projects.

The project specification links to shared rules instead of copying their catalog. A local specialization remains in its native role and preserves the role's ownership and execution contracts. Existing code is evidence of current facts, including drift; it is not implicit approval for a new pattern. A known migration gap describes that drift without making it the target convention for new work.

## Apply Rules to a Change

1. Load this router and the designated project specification, then read the affected architecture, unit, and test references.
2. Identify the owning state and business boundary. Entities protect their invariants; handlers orchestrate use cases; repositories stage and materialize persistence operations. Add a DomainService only for a meaningful domain rule that has no natural entity owner.
3. Select native capabilities already allowed by the profile. A routine algorithm, method, feature name, or additional unit in an approved family does not require a new architecture decision.
4. If the required shape is absent, use [specification evolution](01-specification-evolution.md) before adopting it. Keep independent work inside the current contract moving.
5. Validate the changed behavior and reconcile impacted project-specification facts and rules. Explain a rule's rationale where the rule lives; retain approval evidence in Git/history or the project's existing decision mechanism.

Source and executable tests establish current framework behavior. A mismatch with the specification requires correction or an explicit proposal; neither automatically overrides the approved architectural intent.
