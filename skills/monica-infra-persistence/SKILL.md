---
name: monica-infra-persistence
description: Use when integrating Monica Repository, UnitOfWork, transactional domain events, outbox, or inbox in an application, diagnosing save and commit behavior, or inspecting DbContext connections, activity, and migrations.
---

# Monica persistence

Repositories and UnitOfWork cover a local database operation; use transactional-event guidance when a committed write must drive external delivery.

| Task | Read |
| --- | --- |
| Register a context, choose its provider mode, or use repositories | [Repositories](references/repositories.md) |
| Inspect registered contexts, provider sessions, migration history, or migration updates | [Repository diagnostics](references/repository-diagnostics.md) |
| Set a write boundary, select participants, or understand save and rollback | [Transactions](references/transactions.md) |
| Choose before-commit effects or committed delivery; configure outbox, inbox, and projections | [Transactional events](references/transactional-events.md) |
| Diagnose a failed or slow save, transaction, or delivery | [Troubleshooting](references/troubleshooting.md) |

For key-value storage, use [state stores](../monica-infra-hosting/references/state-stores.md); for application placement, use [ProjectUnit development](../monica-application-project-unit-development/SKILL.md). For module implementation, use `$monica-framework`, then `$monica-development`.

If a routed authoring skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md`.
