# Split placement boundaries and consistency

[Unified architecture](../../monica-application/references/03-unified-architecture.md) defines the same dependency graph in combined and split deployments. A role references its own domain core and shared contracts, never peer domain or role implementations. Business collaboration uses published APIs; same-domain shared invariants stay in the domain core.

Keep every domain's repositories and contexts domain-owned. Current AppId and role selection do not choose the database. Ordinary local operations need their passive domain dependencies on every business process; role activation is not an isolation boundary for database credentials.

A remote operation has a separate execution scope and transaction. Several physical stores do not become atomic when co-located. Use explicit non-atomic coordination or durable messaging for independent stores. Route before invocation; a failed delivery must not fall back to executing on another process because the first attempt may have changed state.

Keep real migrations in top-level `Migrations.Domain{Domain}` projects. Preserve current/history migration chains, issued identities and shared-table ownership; do not create migrations per role. Design-time factories and MigrationRunner do not start business workloads. Use [persistence](../../monica-infra-persistence/SKILL.md) and [messaging](../../monica-infra-messaging/SKILL.md) for provider mechanics.
