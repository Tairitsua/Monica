# Inspect Repository contexts and database evidence

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

## Navigate by the exact context identity

The UI contributes four localized Monitor destinations:

| Route | Operator task |
| --- | --- |
| `/repository-dashboard` | Select a registered context and inspect connection evidence, provider identity, and configured pool limits. |
| `/repository-dashboard/activity` | Search and compare provider-visible sessions, then inspect a session's complete captured query and metadata. |
| `/repository-dashboard/migrations` | Compare the current migration assembly with database history and review or apply pending migrations. |
| `/repository-dashboard/options` | Read captured Repository and DbContext option values; this view does not edit them. |

All four routes accept `?context=` containing `RepositoryDbContextRegistration.ContextId`. Treat this assembly-qualified identity as opaque and URI-escape the complete value:

```csharp
var url = "/repository-dashboard/activity?context="
    + Uri.EscapeDataString(registration.ContextId);
```

Match the decoded value exactly with `StringComparison.Ordinal`. Task links and context selection preserve it, including an explicit ID that is currently unavailable. An unavailable explicit ID produces a missing-context state; another route must not silently substitute the first registration. Without an explicit query, a successful initial capture selects the circuit's retained context when available, otherwise the first registered context. Use `GetCleanName()` or `GetCleanFullName()` for visible CLR type names, preserving generic arguments; readable labels and assembly names do not replace the exact navigation or command ID.

While asynchronous selection is pending, task links still prioritize the incoming explicit query over the previously selected borrowed state. This keeps a second navigation from silently replacing the requested identity during capture or synchronization.

Routed workspaces reuse an existing successful circuit snapshot and selected context; moving between tools does not automatically recapture the registry. Refresh explicitly, or reload into a new circuit, to read new evidence. These are manual snapshots, not polling or durable history. Registry and detail adoption have separate UTC capture times. Connection, migration, and activity reads run sequentially in independently owned scopes, so the captured page is not one atomic database observation. Connection strings begin masked and are revealed only on an explicit operator action.

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

The ledger searches query text, state, user, application, client address/hostname/port, database, backend/session ID, provider detail, and waiting evidence. It retains six sort keys—query age, state, user, application, client, and query—in both directions, plus no grouping or grouping by state, client, user, or application. Native paging keeps every matching row reachable, including within groups. Filters change the ledger view rather than the underlying snapshot or database summary.

Opening a session freezes a detached normalized row with its originating context, database, provider, and capture metadata. Refresh must not replace evidence inside that open inspector. Full SQL remains available for inspection and copying even when the ledger preview is shortened. The guide preserves the actual provider SQL and its selected columns; additional openGauss SQL columns such as query or trace identifiers are not normalized row fields unless the mapper actually supplies them.

Distinguish result states before reporting counts:

| State | Interpretation |
| --- | --- |
| No result / initial loading | No completed activity capture is available. |
| `UnsupportedProvider` | No built-in activity query applies; the selected database is not thereby empty or disconnected. |
| `Failed` | The activity query failed. Missing rows or summary are not zero-session evidence. |
| `Supported` with zero rows | The provider query completed successfully and returned no visible sessions. |
| Nonempty snapshot with zero filtered rows | A search miss; clearing the native input restores the snapshot's rows. |
| Refresh with an earlier capture | Retained evidence, explicitly presented as the previous snapshot until a new result is adopted. |

The facade returns `Res<T>`, but a successful envelope can still contain `Support.Failed`, a migration status with `HasError`, or a failed per-context command outcome. Inspect the model's diagnostic status as well as the envelope.

The Options workspace also checks the selected registry snapshot's `HasError`. A failed context snapshot has no successfully captured options; surface its `ErrorMessage` rather than treating the default empty list as a successful empty configuration ledger.

## Inspect and apply migrations

`AllMigrations` is the selected DbContext's current migration-assembly inventory. `AppliedMigrations` is the database history returned by EF Core. `AppliedCurrentAssemblyMigrations` intersects the two; `ExternalAppliedMigrations` contains database history IDs absent from the current assembly. External history is already recorded evidence, not pending work. Current-assembly entries can be Applied, Pending, or Unknown. A failed history read or unknown count must not become an empty inventory or zero pending migrations.

The UI's default migration policy and execution deadline are:

| `ModuleRepositoryUIOption` | Default | Meaning |
| --- | --- | --- |
| `AllowMigrationUpdateInProduction` | `false` | The UI state denies migration commands in Production unless the host explicitly opts in. Other environments permit the UI actions. |
| `MigrationCommandTimeout` | 60 minutes | Each context gets a fresh linked deadline and EF command timeout for pending discovery, migration execution, and commit; this is not one deadline for the entire batch. |

The production gate belongs to the UI state. It does not prohibit an application's direct `RepositoryDiagnosticsFacade` calls or replace database permissions.

Freeze the selected context's exact ID before opening a confirmation dialog and pass that same ID after approval. Do not reread mutable selection as the command target. Bulk confirmation means all registered contexts, independent of ledger filters or the selected detail view. Registration membership is captured by the backend registry; display its scope explicitly rather than presenting the bulk command as an update of visible or filtered rows.

Bulk updates visit every registration sequentially and recheck each context's pending migrations. Each visit produces an applied, skipped, or failed outcome; an ordinary per-context failure does not make previous changes roll back or prevent later visits. The service attempts a transaction for an individual context when supported and restores the previous EF timeout afterward. There is no shared transaction across the batch. `AttemptedMigrations` records the pre-command pending inventory, not a separate per-migration proof of application.

Store actual command outcomes before the follow-up evidence refresh. `RepositoryDashboardState.MigrationCommandError` identifies a failed command envelope; `ErrorMessage` can also describe a subsequent registry refresh failure. A refresh failure must not reclassify successful migration outcomes. A failed command envelope clears stale command results and is not a successful empty batch. Report the actual applied/skipped/failed counts when a command envelope succeeds.

Cancellation can interrupt observation after database changes committed. Provider/service catches may return failed data; cancellation between bulk visits can instead become a failed facade envelope without the partial result list. Cancellation or missing outcomes are not rollback evidence. Refresh migration history before deciding whether another attempt is needed.

## Preserve UI lifetime ownership

`RepositoryDashboardState` is a scoped, circuit-owned service borrowed by the four routed workbenches. It serializes selection, reads, and commands through one gate and rechecks the caller's token before adopting results. This coordination is local to the circuit, not a distributed or cross-user migration lock.

The consuming component owns its cancellation source and admitted operation, parameter synchronization, confirmation, and focus tasks. Retirement stops admission, cancels, closes or observes late native dialog references, drains all admitted tasks, and then disposes the component lifetime. A failure while draining one task does not excuse abandoning another. Session/guide dialogs own and drain their clipboard work. Do not dispose injected state during route retirement. The backend diagnostics service owns its separate async scopes and DbContexts; UI code does not retain or dispose those contexts. See page-state ownership in `$monica-architecture` (`references/page-state-pattern.md`) and Blazor lifecycle mechanics in `$monica-ui-development` (`references/blazor-best-practices.md`).

Source: `Monica.Repository.UI/Modules/ModuleRepositoryUI.cs`, its four `Pages/UIRepository*Page.razor` wrappers, `UIRepository/Components/RepositoryWorkbench.razor`, `RepositoryActivityTable.razor`, `RepositoryActivityDetailDialog.razor`, `UIRepository/State/RepositoryDashboardState.cs`, `Monica.Repository/Facades/RepositoryDiagnosticsFacade.cs`, `Persistence/Models/RepositoryDiagnosticsModels.cs`, and `Persistence/Services/RepositoryDbContextDiagnosticsService.cs` with its `Support/` activity queries, registry, guide catalog, and pool parser.
