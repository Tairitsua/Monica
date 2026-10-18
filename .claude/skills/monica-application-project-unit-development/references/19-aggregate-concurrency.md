# Aggregate Concurrency and Actor Migration

ApplicationService, Entity/DomainService, Repository, and UnitOfWork compose ordinary aggregate operations. An Actor is a runtime choice, not a mandatory business ProjectUnit. Replacing its adapter must preserve the actual concurrency, state, routing, and delivery guarantees in use.

## Protected boundary

Identify the business key, all write entry points, authoritative state, and side effects. Choose conflict detection or mutual exclusion and define whether callers need synchronous results. A process-local keyed lock protects one process. `RunOnRole` selects placement, not a singleton replica or per-key mailbox. Serializable transactions can reject conflicts; they promise neither FIFO admission nor automatic retry.

All relevant writers must obey the same consistency contract, including API calls, events, jobs, synchronization, and administration. An Actor does not serialize writers that bypass it. Independent databases and external caches do not join a transaction merely through co-location.

## Compose existing units

- Publish normal Command/Query requests and generated domain clients. Keep transport names and Actor identities out of new business contracts.
- Entities or meaningful DomainServices own invariants. Repositories own database-specific concurrency access.
- Use the [transaction contract](../../monica-infra-persistence/references/transactions.md) for scopes, participants, commit, and rollback.
- Select concurrency tokens, conditional updates, or provider-appropriate transactional locking for the invariant. Test production-provider semantics with that provider; SQLite cannot prove another database's isolation.
- When exclusion must cover nontransactional state, define shared coordination, timeout, loss, ownership, and release. A lease without loss handling or fencing is not an unlimited exclusivity guarantee.
- Use an Outbox for reliable notifications where adopted. Preserve after-commit behavior: SQL commit and external delivery are separate outcomes.

Retry only identified transient conflicts from fresh scopes and fresh state. Never blindly replay validation failures, ambiguous commits, or irreversible external effects. Bound retries and surface explicit conflict/retryable outcomes. Acquire multiple keys in stable order; nested calls must not reacquire a non-reentrant lock they already own.

## Migration evidence

Inventory actual use of per-key serialization, activation state, reminders, timers, routing, and method identity. Replace capabilities in use. A handler can combine existing domain behavior when an Actor only routed requests and created scopes; persistent mailboxes and reminders require explicit replacements.

Verify same-key concurrent writes, independent-key progress, rollback, conflicts, cancellation, lock/scope release, and repeated delivery. Preserve public results and commit-versus-notification behavior. Exercise all participating writers. Remove obsolete adapters after callers adopt the standard contract; do not retain an empty Actor-shaped business layer.
