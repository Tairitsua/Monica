# Infrastructure table layout

Use `Monica.EntityFrameworkCore` to place explicitly marked technical tables consistently across EF contexts. It is a leaf relational library, with no Monica module registration, connection allocation, transaction selection, or automatic database migration.

```csharp
using Monica.EntityFrameworkCore.Extensions;
using Monica.EntityFrameworkCore.Models;

var layout = InfrastructureTableLayout.PreferSchema("infra");
options.UseNpgsql(connection, provider =>
        provider.MigrationsAssembly("Migrations.DomainFlight"))
    .UseMonicaTableLayout(layout)
    .UseMonicaMigrationHistory("Flight");
```

Configure the **provider, layout, then history**, in that order, in runtime registration, design-time factories, configuration bootstrap, and the migration runner. Generic overloads preserve `DbContextOptionsBuilder<TContext>`. Identical repetitions are idempotent; conflicting declarations, later provider/history changes, invalid identifiers, and excessive identifier lengths fail before database access. History keys are explicit stable migration-chain identities, not CLR class, deployment role, environment, or process names.

## Placement

| Policy | Schema-capable provider | Provider without independent schemas |
| --- | --- | --- |
| `PreferSchema("infra")` | Marked tables use `infra` | Marked tables use the default namespace |
| `DefaultSchema` | Provider/context default namespace | Default namespace |
| No layout call | Existing mappings remain | Existing mappings remain |

Business entities are never rewritten. Prefer one physical application database where local atomic collaboration is needed, business tables in the default namespace, and descriptive business names instead of domain prefixes. Rename colliding persistent entities/tables by meaning; DTO names do not need global uniqueness. A provider's default namespace is still a schema such as PostgreSQL `public` when applicable.

Built-in capability adapters recognize the installed PostgreSQL, GaussDB, and SQLite provider types exactly. For another relational provider, supply `InfrastructureProviderCapabilities` explicitly; for example, a MySQL adapter uses `new(false, 64)` because its `SCHEMA` is a database synonym. Unknown providers may use `DefaultSchema` without capabilities, but need an identifier limit before opting into Monica history. Capabilities never come from a connection string or a substring of a provider name.

The policy adds no prefix and does not strip existing module-owned identifiers. Repository marks `MonicaOutbox`, `MonicaInbox`, and `MonicaLeases`. Configuration, JobScheduler, and Kafka console mark their own existing tables. Placement does not enable these stores, merge data, share dispatchers, or make two connections one transaction.

Schema support is resolved without database I/O. A missing schema or denied permission is an error for deployment to address, never a reason to fall back. Prepare the infrastructure schema before the provider's first history-table access requires it; prove bootstrap order on an empty instance of the actual provider.

## Module-owned mappings

```csharp
modelBuilder.Entity<AuditQueueRow>()
    .ToMonicaInfrastructureTable("AuditQueue", table =>
    {
        if (excludeFromMigrations)
            table.ExcludeFromMigrations();
    });
```

Mark infrastructure explicitly; do not infer it from namespaces, class names, audit bases, or table prefixes. Unmarked business entities keep their mappings. Owned dependents sharing the same table follow its placement; separately mapped owned tables use the owned-navigation overload. EF entity splitting of a marked entity is currently rejected with a diagnostic: map separate infrastructure entities/tables explicitly. Views and unrelated sequences require their own provider-specific mapping and migration plan.

Do not combine the common policy with a conflicting explicit per-table schema. `LeaseTableOptions.Schema` has been removed: configure `UseMonicaTableLayout` on every participant and retain `LeaseTableOptions.ExcludeFromMigrations`. Without an explicit layout, the table declaration has normal `ToTable` behavior.

## Model identity and ownership

The EF options extension and model convention also work with a plain DbContext and contexts overriding `OnConfiguring`. EF-managed internal services separate models by immutable layout/provider identity without replacing a caller's `IModelCacheKeyFactory`. Repository retains shard/model variants, message-store participation, migration exclusions, and design-time identity. Overrides skipping `RepositoryDbContext.OnConfiguring` call `ConfigureRepositoryOptions` to retain repository behavior without implicit sensitive logging.

A supplied `UseModel` model must have been built for the same resolved namespace and provider; mismatches are rejected even after another context warmed EF's cache. Advanced callers supplying an EF internal service provider must install the options extension's services and keep that provider's layout fixed. Normal application DI registration needs neither extra service registration nor a second service provider.

`UseMonicaMigrationHistory("Flight")` selects `infra.__EFMigrationsHistory_Flight` for the schema-capable example and `__EFMigrationsHistory_Flight` for the flat layout. Layout alone leaves history unchanged. The helper preserves the provider's history implementation and migration assembly. It does not transfer old history, create tables, or infer migration ownership.

Every shared physical table has exactly one migration owner; all other mapping contexts explicitly exclude it. Application-wide preflight must check table collisions, history-key uniqueness, compatible shared definitions, and owner counts across **all** selected contexts. One model cannot prove deployment-wide uniqueness. Use the [application migration contract](../../monica-application/references/05-database-migrations.md) for existing-database upgrades, fresh baselines, strict drift checks, data preservation, and rollback.

Shared cross-context atomic work additionally requires one scoped `DbConnection` and its `DbTransaction`; a common database name is insufficient. Explicit `Independent` APIs can cross databases or run remotely and commit independently; they do not promise atomic rollback with the caller. See [transactions](transactions.md).

## Verification

Test metadata and generated SQL for each supported provider, then execute a real-provider bootstrap/upgrade and persistence smoke test. Include plain and framework contexts, two layouts for the same type, model/shard variants, migration exclusions, cached-service reuse, and supplied-model rejection. SQLite rollback proves its flat layout only; it is not evidence for GaussDB schema creation, quoting, lease claims, or migration-history locking.
