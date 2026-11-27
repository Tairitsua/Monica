# Inspect Repository contexts and database evidence

Activity rows, inspection timestamps and capture receipts follow page time display in `$monica-infra-ui` (`references/time-display.md`). UTC activity instants and database audit wall-time values keep their distinct source semantics; UI formatting does not rewrite either stored representation.

`Monica.Repository.UI` adds operational views for contexts registered through `AddRepositoryDbContext<TContext>()`. It does not enumerate every EF Core context in DI. Register the application's context and provider through [repositories](repositories.md), then add the UI to the same web host:

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
{
    // The application's Repository DbContext registration is composed in this host.
    monica.AddRepositoryUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

`AddRepositoryUI()` requires Repository, module-local localization, and the shell. The host owns access to these diagnostics and to any exposed facade operations; navigation visibility and the migration-production option are not authorization. Configure the host through shell and access in `$monica-infra-ui` (`references/shell-and-access.md`).

## Inspect an explicitly selected entity design model

Use `Monica.EntityFrameworkCore.EntityModelInspector` when tooling needs bounded, portable model evidence. The caller selects and safely creates the context; the inspector receives its design model and performs no database query, migration or host startup:

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Monica.EntityFrameworkCore;

var snapshot = EntityModelInspector.Inspect(
    context.GetService<IDesignTimeModel>().Model,
    typeof(Order),
    context.GetType().FullName!,
    context.Database.ProviderName!);
```

The `monica-entity-model/v1` export includes the selected CLR entity and its owned dependents, inherited table mappings, property/column names, store types, nullability, shadow properties, concurrency tokens, length/precision, generation, SQL defaults/computed expressions, indexes and foreign keys. A single entity can span several tables. Runtime/compiled models can omit design metadata, so pass `IDesignTimeModel.Model`. Missing entities fail explicitly. Serialize the snapshot with the application's normal JSON options; a consumer such as Workflow must retain context/provider and source-revision provenance. The export describes the supplied model, never whether a deployed database matches it. Do not execute arbitrary application contexts merely for discovery.

## Navigate by the exact context identity

The Persistence workbench has three Monitor destinations and retains the old options URL for bookmarks.

| Route | Operator task |
| --- | --- |
| `/repository-dashboard` | Find registrations needing attention, compare physical targets and configured pool limits, inspect a registration, and read host and context options. |
| `/repository-dashboard/activity` | Compare sessions by database, state and query age. Filter the ledger and open captured SQL and provider evidence. |
| `/repository-dashboard/migrations` | Reconcile every registered migration assembly with database history, then review and apply pending migrations for the chosen scope. |
| `/repository-dashboard/options` | Redirect to the options section on Overview. The redirect maps `context`, `q`, and `group` to the corresponding options selection and filters. |

Pass `RepositoryDbContextRegistration.ContextId` as `context` on the Activity and Migrations routes and in single-context option links. Use `inspectContext` to open the Overview dossier. Treat this assembly-qualified identity as opaque and URI-escape the complete value.

```csharp
var url = "/repository-dashboard/activity?context="
    + Uri.EscapeDataString(registration.ContextId);
```

The UI matches decoded identities with `StringComparison.Ordinal`. A missing explicit identity has its own state. Without an explicit selection, a successful initial capture selects the circuit's retained context when available, then the first registered context. The command and navigation identity is independent of its displayed name.

While asynchronous selection is pending, task links still prioritize the incoming explicit query over the previously selected borrowed state. This keeps a second navigation from silently replacing the requested identity during capture or synchronization.

The three workspaces share the last successful registry snapshot in their circuit. Refresh re-reads the registry. Initial Activity visits and selection changes fetch a provider sample for that registration on demand. Returning to a sampled registration can reuse its capture until an explicit refresh. A topology session badge is available after a context on that physical target has returned activity. Registry and activity have separate capture times. Connection strings begin masked. Revealing requires an operator action, and masking clears revealed values from circuit evidence even when refreshing fails.

Overview records use `q`, `standing`, `provider`, and `target`. The options section uses `section=options`, `optScope=one`, `context`, `optQ`, and `optGroup`. Host-wide flags are stated once above the context comparison. The single-context ledger includes the same host flags with captured registration and EF options. A failed snapshot exposes the context error separately from the host flags.

## Read provider activity

PostgreSQL and openGauss-compatible activity templates read `pg_stat_activity` without a database `WHERE` clause. A connection can therefore return sessions from other databases and clients, including the diagnostics session itself; database-login permissions determine the evidence visible in each row. SQL-template availability does not establish that a query succeeded.

Keep the following scopes distinct:

| Evidence | Scope |
| --- | --- |
| `RepositoryActivityResult.Rows`, `SessionCount`, and summary `TotalSessions` | All rows returned by the provider query. |
| Summary `DatabaseSessions`, `ActiveSessions`, `IdleSessions`, `IdleInTransactionSessions`, and `WaitingSessions` | Rows matching the selected connection's database name without regard to case; if that name is unavailable to the backend, all returned rows are included. |
| Connection pool minimum, maximum, and pooling switch | Settings parsed from the selected connection string, not a measured count of borrowed or open application-pool connections. |

Database session counts include other clients. Never divide them by a configured application pool maximum and label the result as pool occupancy or utilization. Missing or invalid pool keywords remain distinguishable from explicit values. The model exposes shared maximum/minimum fallbacks of `100`/`0` and enabled pooling; the UI marks applicable common-provider defaults and leaves unresolved provider-specific defaults explicit instead of inventing measured limits.

`Runtime` is `current_timestamp - query_start`: age since the current or most recent query began. For an idle session, it can describe the last query rather than active execution time. Display complete durations, including days, and retain missing values. Provider timestamps and an observation timestamp are different evidence; do not infer an unavailable timestamp or timezone.

`Waiting` is nullable provider evidence. PostgreSQL derives it specifically from `wait_event_type = 'Lock'`; false does not prove the absence of every other wait category. openGauss uses its native waiting field. Waiting can overlap active or idle-in-transaction states, so it is not another mutually exclusive session-state bucket.

The ledger searches query text, state, user, application, client address, hostname, port, database, backend or session ID, provider detail, and waiting evidence. The URL carries `scope`, `state`, `q`, `sort`, `dir`, and `group`. Scope includes all server-visible rows, the selected database, or other databases. Grouping supports state, database, client, user, and application. The logarithmic ruler exposes the same sampled sessions, with separate marks for lock waits and sessions from other databases. Filtering leaves the signature counts scoped to the capture.

Opening `inspectSession` freezes the normalized row with its originating context, database, provider, and capture time in a right-side sheet. The sheet provides full SQL, a wrap toggle, and copyable JSON. The troubleshooting sheet opens with `guide=1` and shows the provider's SQL and selected columns. The guide's projected columns and the normalized session evidence are separate contracts.

Distinguish result states before reporting counts:

| State | Interpretation |
| --- | --- |
| No result / initial loading | No completed activity capture is available. |
| `UnsupportedProvider` | No built-in activity query applies; the selected database is not thereby empty or disconnected. |
| `Failed` | The activity query failed. Missing rows or summary are not zero-session evidence. |
| `Supported` with zero rows | The provider query completed successfully and returned no visible sessions. |
| Nonempty snapshot with zero filtered rows | A search miss; clearing the native input restores the snapshot's rows. |
| Refresh fails after a successful sample | The previous sample stays visible with its capture time and the read failure. |

The facade returns `Result<T>`, but a successful envelope can still contain `Support.Failed`, a migration status with `HasError`, or a failed per-context command outcome. Inspect the model's diagnostic status as well as the envelope.

## Inspect and apply migrations

`AllMigrations` is the selected DbContext's current migration-assembly inventory. `AppliedMigrations` is the database history returned by EF Core. `AppliedCurrentAssemblyMigrations` intersects the two; `ExternalAppliedMigrations` contains database history IDs absent from the current assembly. External history is already recorded evidence, not pending work. Current-assembly entries can be Applied, Pending, or Unknown. A failed history read or unknown count must not become an empty inventory or zero pending migrations.

`RepositoryDbContextSnapshot.MigrationStatus` provides assembly entries and applied history for every resolved registration in the registry capture. The fleet track draws one bead per compiled migration where history was readable and a hatch where it was unavailable. The pending total carries a `+` when readings are incomplete. The selected ledger keeps its search and state filter in `q` and `state`. Overview can open the selected registration's reconciliation through `inspectContext` and `tab=schema`.

The UI's default migration policy and execution deadline are:

| `ModuleRepositoryUIOption` | Default | Meaning |
| --- | --- | --- |
| `AllowMigrationUpdateInProduction` | `false` | The UI state denies migration commands in Production unless the host explicitly opts in. Other environments permit the UI actions. |
| `MigrationCommandTimeout` | 60 minutes | Each context gets a fresh linked deadline and EF command timeout for pending discovery and migration execution. |

The production gate belongs to the UI state. It does not prohibit an application's direct `RepositoryDiagnosticsFacade` calls or replace database permissions.

Freeze the selected context's exact ID before opening a confirmation dialog and pass that same ID after approval. Do not reread mutable selection as the command target. Bulk confirmation means all registered contexts, independent of ledger filters or the selected detail view. Registration membership is captured by the backend registry; display its scope explicitly rather than presenting the bulk command as an update of visible or filtered rows.

Bulk updates visit every registration sequentially and recheck each context's pending migrations. Each visit produces an applied, skipped, or failed outcome. Completed updates remain committed when a later context fails. The EF Core provider's migrator owns its transactions and can execute transaction-suppressed commands. The service restores the previous EF timeout afterward. `AttemptedMigrations` records the pending inventory found before the command.

Store actual command outcomes before the follow-up evidence refresh. `RepositoryDashboardState.MigrationCommandError` identifies a failed command envelope; `ErrorMessage` can also describe a subsequent registry refresh failure. A refresh failure must not reclassify successful migration outcomes. A failed command envelope clears stale command results and is not a successful empty batch. Report the actual applied/skipped/failed counts when a command envelope succeeds.

Cancellation can interrupt observation after database changes committed. Provider/service catches may return failed data; cancellation between bulk visits can instead become a failed facade envelope without the partial result list. Cancellation or missing outcomes are not rollback evidence. Refresh migration history before deciding whether another attempt is needed.

## Preserve UI lifetime ownership

`RepositoryDashboardState` is scoped to the circuit and borrowed by the three workbenches. It serializes selection, reads, and commands through one gate and rechecks the caller's token before adopting results. This coordination applies within one circuit.

The consuming component owns its cancellation source and admitted operation, parameter synchronization, confirmation, and focus tasks. Retirement stops admission, cancels, closes or observes late native dialog references, drains all admitted tasks, and then disposes the component lifetime. A failure while draining one task does not excuse abandoning another. Session/guide dialogs own and drain their clipboard work. Do not dispose injected state during route retirement. The backend diagnostics service owns its separate async scopes and DbContexts; UI code does not retain or dispose those contexts. See page-state ownership in `$monica-architecture` (`references/page-state-pattern.md`) and Blazor lifecycle mechanics in `$monica-ui-development` (`references/blazor-best-practices.md`).

See `Monica.Repository.UI/Modules/ModuleRepositoryUI.cs`, its `Pages/UIRepository*Page.razor` wrappers, `UIRepository/Components/RepositoryWorkbench.razor`, `RepositorySessionEvidence.razor`, `UIRepository/State/RepositoryDashboardState.cs`, `Monica.Repository/Facades/RepositoryDiagnosticsFacade.cs`, and `Persistence/Services/RepositoryDbContextDiagnosticsService.cs` for implementation.
