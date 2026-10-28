# Database layout and migrations

Use this contract when placing migration projects, sharing a database across domains, or changing an application's physical database layout. The [unified architecture](03-unified-architecture.md) owns domain and role boundaries. The project specification records concrete context, schema, history and deployment choices; an environment cutover remains a separately reviewed operation.

## Ownership and source layout

A domain owns its persistence model and migration project regardless of how many roles, hosts or databases deploy it. Keep one real migration assembly per persisted domain under `src/Migrations/Migrations.Domain{Domain}`. Current and history contexts keep independent migration chains and snapshots in that assembly. Do not introduce a migration assembly per role, AppId, environment or physical database, and do not merge domain models into a product-wide DbContext to obtain local transactions.

An illustrative new-project layout is:

```text
src/
  Domains/
    Ordering/Domains.Ordering/Repository/
      OrdersDbContext.cs
      OrdersHistoryDbContext.cs
  Migrations/
    Migrations.DomainOrdering/
      Factories/
        OrdersContextFactory.cs
        OrdersHistoryContextFactory.cs
      Migrations/
        OrdersDb/
          <timestamp>_<Change>.cs
          <timestamp>_<Change>.Designer.cs
          OrdersDbContextModelSnapshot.cs
        OrdersHistoryDb/
          <timestamp>_<Change>.cs
          <timestamp>_<Change>.Designer.cs
          OrdersHistoryDbContextModelSnapshot.cs
    MigrationPlatform/                 # Only if common migration composition exists
    MigrationRunner/                   # Application-owned deployment executable
```

Folder names within an existing assembly may remain unchanged. A layout cleanup must preserve issued migration IDs, CLR context identities, assembly names, snapshot semantics and configured history locations. Framework stores retain the project's existing migration owner; their placement does not justify a dependency on a business role or host. Migration projects reference domain cores and migration helpers. The runner references the selected migration assemblies; business libraries never reference it.

## Physical placement

Prefer one relational database when isolation, capacity and operational ownership permit it. Sharing a database does not permit direct access to another domain's repositories or tables. Both combined and split role deployments retain the same domain contracts.

Prefer one physical application database when local atomic collaboration is required. Put business tables in the default namespace with descriptive names and no added domain prefix. Rename colliding persistent entities/tables by business meaning. Put marked infrastructure tables in `infra` when the provider supports an independent schema within the same database; otherwise use the default namespace without adding a prefix. Explicitly independent APIs may use different databases and commit independently. Retain existing namespaces only through a deliberate project contract and migration plan.

Use [Monica infrastructure table layout](../../monica-infra-persistence/references/infrastructure-table-layout.md) for explicitly marked technical tables and per-context history. It does not rewrite business mappings or consolidate databases. Verify tables, indexes, sequences, foreign keys, views, raw SQL, bulk operations and shard creation. A connection-level search path cannot resolve colliding business names; use distinct names and EF metadata/provider quoting instead of switching session state between calls.

Use the same schema model for development, UAT and production; environment inputs select the physical database. A different database topology must not silently change the migration model. If a product deliberately supports multiple model variants, it must explicitly own their snapshots, selection and validation rather than generating one chain under whichever environment happens to be selected.

Cross-context Shared transactions still require the same scoped `DbConnection`, provider and physical transaction. Multiple schemas in that database do not prevent sharing. Matching connection strings or one physical database do not create a shared connection, and HTTP calls cannot join the caller's local transaction. See [transactions](../../monica-infra-persistence/references/transactions.md) for Shared, Independent, cancellation and ownership.

## Migration history and shared tables

Distinguish three identities:

| Identity | Owner and purpose |
| --- | --- |
| Migration chain | One DbContext's ordered migrations and snapshot in its domain assembly |
| History location | The physical schema/table where EF records applied migration IDs |
| Physical object | One database/schema/table, sequence, index or other database object |

For new contexts, use an explicit history key per chain, such as `UseMonicaMigrationHistory("Orders")` and `UseMonicaMigrationHistory("OrdersHistory")`. With the preferred layout these become `infra.__EFMigrationsHistory_Orders` and `infra.__EFMigrationsHistory_OrdersHistory`; flat providers omit the schema. Keep the location identical in design-time factories, the runner and runtime diagnostics. A project may retain an existing shared history table if migration IDs remain unique across its chains and the runner verifies the mapping. A shared history table is not a shared migration chain; foreign IDs are not pending work. See [migration diagnostics](../../monica-infra-persistence/references/repository-diagnostics.md#inspect-and-apply-migrations).

Changing a history location after deployment requires an explicit transfer of the verified applied records. Match records against the original chain, retain their IDs and product versions, and reject ambiguous or missing ownership. Merely configuring a new empty history table makes applied migrations appear pending. Do not insert a latest migration ID to conceal an unverified schema or treat an empty pending list as proof that copied data is correct.

Every physical table has exactly one migration owner. The key includes **database, schema and table**, not just the database name:

- Contexts sharing the same Outbox/Inbox tables choose one migration owner and exclude those tables from the other contexts' migration models, following [transactional events](../../monica-infra-persistence/references/transactional-events.md).
- Existing distinct Outbox/Inbox stores are not merged merely because their unqualified names match. A shared-store transition must reconcile delivery, receipt identity, local-handler eligibility and maintenance scope with the actual physical target.
- Lease and framework tables follow the same common infrastructure layout and physical-object ownership rule. Sharing a database does not automatically merge coordination authorities or queues.
- Transferring ownership is a schema transition: review the generated operations and snapshots for every previous and new owner. `ExcludeFromMigrations` on today's model does not rewrite old migrations, transfer data, or neutralize old `CreateTable` operations.

## Generate and validate a change

Design-time factories compose only the selected persistence dependencies. They must not activate business roles, jobs, subscribers, seeders or channel workers. Use the project's explicit environment-selection mechanism, inspect the resolved target and overrides, and retain its provider-specific design-time services. Schema generation can require configuration or a connection even when no migration is applied.

For an application whose factories and tool configuration are already in place:

```text
cd src/Migrations/Migrations.DomainOrdering
dotnet ef migrations has-pending-model-changes --context OrdersDbContext
dotnet ef migrations add AddOrderReference --context OrdersDbContext --output-dir Migrations/OrdersDb
dotnet ef migrations script <FromMigration> <ToMigration> --context OrdersDbContext
```

Use the actual existing directory for an existing chain. Names describe the change; EF owns the timestamp. A script generated without an explicit range is not a query for the target database's unapplied migrations. Review model changes, generated `Up`/`Down`, snapshot, SQL, destructive operations and any backfill. Validate against the provider version actually deployed. Do not substitute SQLite success for a provider-specific schema or data-transfer test.

Released migration IDs and applied migration bodies are immutable inputs. Correct later behavior with an additive migration or an explicitly reviewed repair/cutover artifact. Do not hand-edit snapshots to suppress drift, silently drop/recreate populated tables for a rename, or reset history to make a failed deployment appear current.

Before delivery, prove both relevant paths:

1. Upgrade an isolated restore of the supported previous version, including data and migration history.
2. Bootstrap an empty database through the documented installation path and then upgrade it again without additional work.

Changing `HasDefaultSchema` affects the current model; it does not change schema arguments embedded in old migration operations. A consolidated fresh installation can therefore still encounter duplicate old `CreateTable` operations. The runner's explicit ordering/bootstrap or a separately reviewed baseline must handle that case. A baseline is a deliberate installation contract with schema/data verification, not permission to overwrite issued history. Do not advertise consolidation until the chosen fresh and existing-database paths both pass.

## Deployment runner contract

`MigrationRunner` here means an application-owned executable; Monica does not supply a universal runner, connection registry or database consolidation service. Use the project's existing deployment mechanism. Its observable contract should cover:

- An explicit inventory of contexts, assemblies, provider, resolved database/schema/history identity, target migration and dependency order. Include optional stores only when deliberately selected. Record the artifact/source revision that supplied the migrations.
- A strict preflight for missing migrations, ambiguous history, duplicate physical owners, incompatible targets and unsupported model/provider differences. Build the full plan before schema application. Keep schema creation and application out of a read-only dry-run, and report missing databases as unverified prerequisites rather than success.
- One authorized migration execution per target database at a time, across CI jobs and operator tools. Sequence dependent contexts; do not rely on a UI circuit lock or assume provider migration locking coordinates the entire multi-context release.
- A separate deployment identity for required DDL where practical. Ordinary replicas consume the prepared schema and do not race to migrate at startup.
- Per-context results and durable release logs. Batch execution is not one atomic transaction, even after databases are combined; a later failure does not undo earlier migrations. Stop promotion, inspect applied history and actual schema, then resume from verified state. Cancellation is not evidence of rollback.

Strict model drift checking is part of the release gate. An existing application's relaxed runner default is a migration gap to record and correct; scripts must explicitly select strict behavior until that default is fixed. Do not bypass model checking or swallow migration errors to complete a release.

## Consolidation and rollback

A database merge is a data and deployment cutover, not just an EF migration. Keep ordinary schema evolution in the domain migration projects. Put the reviewed one-time export/import, history relocation, validation and rollback procedure in the application's existing deployment assets, with the chosen target layout in its project specification. Do not create UAT-only copies of domain migrations.

The plan must inventory same-named objects, current/history data, sharded tables, generated sequences, configuration, scheduler state, Inbox receipts and Outbox delivery state. Restore backups into isolated targets for rehearsal. Copying several live databases at unrelated instants is not a consistent application snapshot; a simple cutover quiesces all writers, autonomous work and input consumers before taking the final copies. Preserve offsets and durable identities. Prove dump/restore compatibility with the actual provider, available disk space and restore duration before estimating the outage.

Keep durable receipts and queued work. Do not convert delivered Outbox rows back into pending rows, combine unrelated receipt keys, or delete coordination records wholesale. Active leases, fencing counters and durable job recovery need their provider-specific handling after old writers have stopped. A schema change does not authorize resetting business data, queues or deduplication history.

Update every source of database selection together, including environment files, deployment overrides, factories, runner inputs, shard initialization and operational guards. Verify Shared rollback across domains, Independent survival of parent rollback, rejection of remote Shared calls, and receiving Inbox/business/Outbox atomicity on the real provider. Check all context histories and model drift before reopening ingress.

Retain the original databases and deployment inputs during the agreed rollback window. Before new writes, reverting to the frozen originals can be straightforward. After new writes, switching a connection back would lose those writes: stop ingress and use a rehearsed data reconciliation or recovery plan. Neither an EF `Down` nor a configuration rollback is a general database-merge rollback.

## Project specification checklist

Record the actual domain → context → migration assembly → schema/history mapping, physical shared-table owners, current/history placement, scoped connection ownership, provider/version source, runner entrypoint, strict flags, installation path and verification commands. Link to operational assets instead of duplicating credentials or environment runbooks. Mark unimplemented layout changes as proposals or migration gaps, never as current deployment facts.

EF reference behavior: [separate migration projects](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects), [custom history tables](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/history-table), [applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying), and [cross-context transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions#cross-context-transaction). Product ownership and deployment requirements above are Monica application conventions; provider-specific support still requires verification.
