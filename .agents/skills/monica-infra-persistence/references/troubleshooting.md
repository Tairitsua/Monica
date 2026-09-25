# Diagnose persistence operations

Start with the failed boundary and inspect its owning contract:

| Symptom | Check |
| --- | --- |
| Repository write is missing or a save fails | Context registration mode and ownership in [repositories](repositories.md); returned result envelope, flush behavior, and fresh-scope retry in [transactions](transactions.md) |
| Operation rolls back unexpectedly | Nested failure, participant selection, and physical connection identity in [transactions](transactions.md) |
| Event is not delivered or is delivered again | Primary-context outbox/inbox setup, migrations, and staging in [transactional events](transactional-events.md); then EventBus transport and worker health |
| PostgreSQL/GaussDB rejects a UTC timestamp parameter | Outbox/inbox UTC mapping in [transactional events](transactional-events.md) |

For implementation changes, run the affected `Test.Monica.Repository` project, then the repository's required solution build and standard non-UI test gate.
