---
name: monica-infra-persistence
description: Use when integrating Monica Repository, UnitOfWork, transactional domain events, outbox, or inbox in an application, or diagnosing their save and commit behavior.
---

# Monica persistence

Repositories and UnitOfWork cover a local database operation; use transactional-event guidance when a committed write must drive external delivery.

| Task | Read |
| --- | --- |
| Register a context, choose its provider mode, or use repositories | [Repositories](references/repositories.md) |
| Set a write boundary, select participants, or understand save and rollback | [Transactions](references/transactions.md) |
| Stage domain events or configure outbox, inbox, and projections | [Transactional events](references/transactional-events.md) |
| Diagnose a failed save, transaction, or delivery | [Troubleshooting](references/troubleshooting.md) |

For key-value storage, use [state stores](../monica-infra-hosting/references/state-stores.md); for application placement, use [ProjectUnit development](../monica-application-project-unit-development/SKILL.md); for module implementation, use [development](../monica-development/SKILL.md).
