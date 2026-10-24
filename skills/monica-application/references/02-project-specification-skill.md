# Repository Application Specification Skill

Use a repository-owned `<project>-application-spec` skill for the application architecture. Keep its canonical folder in the repository's declared skill source and name that entry in `AGENTS.md` or equivalent. The managed Monica Guide block routes to the repository-designated skill; it does not own, install, or hardcode a particular project's specification.

## Required Contract

The entrypoint activates for design, implementation, refactoring, and review of that application. It requires `$monica-application`, states the unified architecture and actual deployment placement, and routes directly to the relevant local references. Direct invocation of either skill still loads both. Keep universal guidance in the shared skills instead of reproducing it locally.

Local references own:

- For applications with UI: presentation projects, task features, layout/navigation, invocation profile, caller/tenant context, and contract-only references. The same project specification owns backend and UI.
- The actual macro view: business host artifacts and their local implementation sets, generated ownership catalog where needed, domain cores, role libraries, AppId placement, shared projects, persistence/migration owners and dependency direction. Distinguish code facts from approved target intent.
- Platform contents: which of the four allowed responsibilities actually exist, their real consumers and dependency checks. Record host Composition ownership and the need for any optional generated catalog; do not create empty projects to match the template.
- Startup and Managed ownership: option locations, stable section/definition identities, environment-file locations and deployment mounts, private-file ignore rules, and prerequisite validation. Keep canonical Application identity and placement instead of a product-maintained service-name or role registry.
- Local execution: the real host launch profile, current AppId and complete topology, explicit AutonomousWork setting, diagnostics exposure, selected resource environment and verified native manual actions. Record unavailable capabilities and remaining migration gaps; do not imply Manual prevents explicit writes to shared resources.
- Concrete local conventions: namespaces, placement, metadata language and requirement source, public/internal boundaries, and approved extension families with their consumers and lifecycle.
- Deliberate deviations: shared rule, precise scope, rationale, human approval evidence, and any conditions.
- Validation: repository commands and focused behavioral checks for its adopted patterns.
- Test organization: the test root, shared support library, and concrete adoption or migration gaps for the [shared test naming rules](../../monica-application-unit-testing/references/project-naming.md).
- Known migration gaps: actual drift, expected target, evidence, and boundaries for new work.

Use one reference when that is enough. Split only when tasks need independent subsets, such as the macro view versus extension contracts. Preserve the language of existing repository artifacts. Do not reproduce the shared ProjectUnit catalog or add an approval workflow to the skill.

## Adoption and Maintenance

Start from the [entrypoint template](../assets/project-application-spec/SKILL.md.template) and [local specification template](../assets/project-application-spec/references/application-specification.md.template) when creating a project skill. Replace every template input with verified repository facts or an explicitly identified proposal; do not present unapproved patterns as adopted rules. The template's headings are optional organization, not additional application requirements.

Move the authoritative application architecture from a parallel document or Workflow topic into the project skill's references, preserving incoming navigation through a link when needed. Repository documentation may explain products and operations, and Workflow may own behavioral requirements and decision evidence; neither should maintain a second current architecture specification.

Before finalizing a change, reconcile the affected local facts, rules, examples, and migration gaps. Link to [unified architecture](03-unified-architecture.md) and [startup/local execution](04-startup-and-local-execution.md) for shared contracts; keep concrete project paths, role assignments and environment choices locally. Use [specification evolution](01-specification-evolution.md) to distinguish autonomous factual correction from a change requiring human adoption. The project skill must remain accurate enough to guide the next feature without reading historical discussions to discover the current rule.
