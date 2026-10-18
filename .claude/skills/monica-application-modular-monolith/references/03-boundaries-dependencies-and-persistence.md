# Combined placement boundaries and persistence

Co-location preserves the [unified domain/role boundaries](../../monica-application/references/03-unified-architecture.md). Role libraries reference their own core and shared contracts. A domain core does not reference its roles or foreign domain implementations. Each Host references the concrete implementations included in its artifact; Platform.Hosting does not become that umbrella.

Repositories and context mappings belong to the domain core. Operation ownership selects the default context, independently of current AppId or active roles. Current/history stores remain distinct context chains. A role split does not create another schema or migration project.

Real migration projects live at top-level `src/Migrations/Migrations.Domain{Domain}`. Preserve issued migrations, snapshots, context identities and shared infrastructure-table ownership. Design-time factories and explicit MigrationRunner composition must not activate role jobs, subscriptions or channels. Do not apply schema migrations in every business replica.

A shared process does not make independent stores atomic. Preserve actual provider participation and shared connection requirements, and reject silent context switching or independent nested commits. Use [persistence contracts](../../monica-infra-persistence/SKILL.md) for transaction behavior and [application tests](../../monica-application-unit-testing/SKILL.md) to verify real persisted outcomes.
