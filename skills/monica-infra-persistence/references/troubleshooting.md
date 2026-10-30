# Diagnose persistence operations

Start with the failed boundary and inspect its owning contract:

| Symptom | Check |
| --- | --- |
| Repository write is missing or a save fails | Context registration mode and ownership in [repositories](repositories.md); returned result envelope, flush behavior, and fresh-scope retry in [transactions](transactions.md) |
| Operation rolls back unexpectedly | Nested failure, participant selection, and physical connection identity in [transactions](transactions.md) |
| Saves stall while `Executed DbCommand` timings remain short | Enable `Microsoft.EntityFrameworkCore.Database.Transaction` at Debug and correlate transaction/savepoint commands with provider wait samples. Command logging alone does not cover savepoint operations. Current managed saves omit savepoints; see [save ownership](transactions.md#save-ownership-and-database-commands). |
| A later Outbox/save/commit rejection hides an earlier caught failure | Inspect `TransactionRollbackOnlyException.FirstFailure`, its `InnerException`, and transaction/trace IDs. The first rollback cause is retained even when later work fails; see [first-failure diagnostics](transactions.md#first-failure-diagnostics). |
| A DbContext is not a participant in the active transaction | Compare the error's context, logical transaction owner and selected participants; inspect the consumer's domain mapping, actual DI scope, connection instance and any physical sharding adapter. Same database names alone do not establish participation. |
| A Shared call rejects a remote or independent store | Check resolved role/authority routing and whether a caller transaction is active. Co-locate compatible participants for atomic work, or explicitly select Independent when separate commit is intended. |
| Event is not delivered or is delivered again | Primary-context outbox/inbox setup, migrations, and staging in [transactional events](transactional-events.md); then EventBus transport and worker health |
| Outbox cleanup is slow or the table keeps growing | Independent cleanup controls, retention volume, existing timestamp index, and provider execution plan in [Outbox delivery and maintenance](transactional-events.md#outbox-delivery-and-maintenance) |
| PostgreSQL/GaussDB rejects a UTC timestamp parameter | Outbox/inbox UTC mapping in [transactional events](transactional-events.md) |
| An activity table includes another database, or session counts differ from pool limits | Server-visible rows, selected-database summaries, and configured pool limits in [Repository diagnostics](repository-diagnostics.md#read-provider-activity) |
| Migration history counts differ from assembly counts, or a bulk update has partial outcomes | Assembly/history scope and sequential command outcomes in [Repository diagnostics](repository-diagnostics.md#inspect-and-apply-migrations) |

For implementation changes, run the affected `Test.Monica.Repository` project, then the repository's required solution build and standard non-UI test gate.

For GaussDB `acquire lwlock / UniqueSqlEvictLock` samples, inspect the deployed build's Unique SQL collection capacity and eviction settings. This wait identifies SQL-statistics eviction contention, not a business row lock; see the [GaussDB wait-event reference](https://support.huaweicloud.com/intl/en-us/centralized-ref-v10-gaussdb/gaussdb-38-1605.html). Older Monica saves used a fresh GUID in each savepoint name; providers that fingerprint these commands by full SQL text can accumulate distinct statistics entries even for empty saves. Verify the deployed fingerprint behavior and compare command counts, statistics growth and waits before attributing the entire delay to that source. Keep the actual transaction guarantees when removing savepoints; changing statistics settings requires checking the deployed version's reload/restart and statistics-reset semantics.

For a repeatedly failing event, reproduce the receiving handler with its actual method policy and primary Inbox context. Preserve admission filters and intentional prepare/commit phases. Do not remove participant guards, make every child call Independent, or classify an unexplained database failure as a permanent business rejection to stop retries. A database consolidation still requires correct scoped connection sharing and logical/physical context ownership.
