# Diagnose persistence operations

Start with the failed boundary and inspect its owning contract:

| Symptom | Check |
| --- | --- |
| Repository write is missing or a save fails | Context registration mode and ownership in [repositories](repositories.md); returned result envelope, flush behavior, and fresh-scope retry in [transactions](transactions.md) |
| Operation rolls back unexpectedly | Nested failure, participant selection, and physical connection identity in [transactions](transactions.md) |
| Event is not delivered or is delivered again | Primary-context outbox/inbox setup, migrations, and staging in [transactional events](transactional-events.md); then EventBus transport and worker health |
| Outbox cleanup is slow or the table keeps growing | Independent cleanup controls, retention volume, existing timestamp index, and provider execution plan in [Outbox delivery and maintenance](transactional-events.md#outbox-delivery-and-maintenance) |
| PostgreSQL/GaussDB rejects a UTC timestamp parameter | Outbox/inbox UTC mapping in [transactional events](transactional-events.md) |
| An activity table includes another database, or session counts differ from pool limits | Server-visible rows, selected-database summaries, and configured pool limits in [Repository diagnostics](repository-diagnostics.md#read-provider-activity) |
| Migration history counts differ from assembly counts, or a bulk update has partial outcomes | Assembly/history scope and sequential command outcomes in [Repository diagnostics](repository-diagnostics.md#inspect-and-apply-migrations) |

For implementation changes, run the affected `Test.Monica.Repository` project, then the repository's required solution build and standard non-UI test gate.
