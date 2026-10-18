# Migrate a merged bounded context

Follow the [unified application specification](../../monica-application/references/03-unified-architecture.md) for new domains. For an existing merged Domains.* project, retain entities, domain services, repositories, contexts and passive synchronization in `Domains/{Domain}/Domains.{Domain}`. Move application handlers and autonomous entries to `Roles.{Domain}` or a justified purpose role beneath the same domain.

The role project already expresses the application boundary; remove redundant Application/ nesting as files move. Shared same-domain rules remain in the core. Add domain and role assembly declarations, keep stable Protocol role identifiers, and reference only the implementations included in each justified host artifact; use the generated lightweight catalog for shared ownership.

Preserve generated interface names, published request namespaces, routes and durable identities. WorkloadOwner may preserve an already issued scheduler owner, independently of the new AppId placement. Do not copy active integrations into every role or introduce a Monica module per business feature.

Adopt combined placement first where appropriate, verify local operations and selected workload behavior, then consider split placement separately. Migrate actual domain migrations to the top-level Migrations home while preserving their assembly/context identities. Record remaining source drift in the project specification rather than describing the legacy structure as a second new-project convention.
