---
name: monica-infra-persistence
description: Use when integrating Monica repositories, transactions, object storage, transactional domain events, outbox, or inbox in an application, diagnosing save and commit behavior, or inspecting DbContext connections, activity, and migrations.
---

# Monica persistence

Repositories and UnitOfWork cover a local database operation; use transactional-event guidance when a committed write must drive external delivery.

| Task | Read |
| --- | --- |
| Register a context, choose its provider mode, or use repositories | [Repositories](references/repositories.md) |
| Cache immutable repository projections using transactional dataset versions | [Repository snapshots](references/repository-snapshots.md) |
| Place infrastructure tables and per-context history across relational providers | [Infrastructure table layout](references/infrastructure-table-layout.md) |
| Place migration projects, consolidate databases, or define schema/history ownership | [Application migrations](../monica-application/references/05-database-migrations.md) |
| Inspect registered contexts, provider sessions, migration history, or migration updates | [Repository diagnostics](references/repository-diagnostics.md) |
| Set Shared/Independent boundaries, compose one database, select participants, or understand rollback | [Transactions](references/transactions.md) |
| Store binary objects or select a Minio provider | [Blob storage](references/blob-storage.md) |
| Choose before-commit effects or committed delivery; configure outbox, inbox, and projections | [Transactional events](references/transactional-events.md) |
| Diagnose a failed save, transaction, or delivery | [Troubleshooting](references/troubleshooting.md) |

For key-value storage, use [state stores](../monica-infra-hosting/references/state-stores.md); for application placement, use ProjectUnit development in `$monica-application-project-unit-development`. For module implementation, use `$monica-framework`, then `$monica-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
