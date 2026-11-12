---
name: monica-coding-standards
description: Apply Monica coding standards when designing, implementing, reviewing, or simplifying framework code. Use for responsibility boundaries, unnecessary fallbacks, validation, abstractions, and C# contracts. Keep cleanup within the requested scope; consumer repositories own their coding rules.
---

# Monica Coding Standards

Implement the smallest coherent design that fulfills a verified contract. Keep ownership explicit, make invalid states visible, and remove mechanisms that do not earn their complexity. Elegance means fewer concepts and clearer behavior, not fewer lines at any cost.

Apply these standards during implementation and review, not only in a final cleanup pass. Read [C# contracts](references/csharp-contracts.md) for developer-facing APIs, options, registration methods, and language conventions. Use `$monica-architecture` for module boundaries and `$monica-development` for module lifecycle and result-envelope rules; these standards do not replace those contracts.

## Design principles

### Solve a demonstrated problem at its owner

Require a concrete use case, caller, or existing contract before adding a branch, option, extension point, or abstraction. Hypothetical future flexibility is insufficient. An uncommon requirement is not automatically invalid: establish whether Monica owns it before deciding how to handle it.

Keep reusable infrastructure mechanisms in Monica. Application-specific sequencing, data preparation, recovery policy, and business exceptions belong to the application. Let callers compose existing capabilities or supply explicitly prepared data; do not make the framework infer business intent or silently switch semantics to accommodate a special workflow.

### Prefer subtraction and explicit data flow

First remove obsolete paths, redundant checks, aliases, pass-through wrappers, duplicated state, and unnecessary indirection. Simplify ownership and control flow before adding another conditional. An abstraction should name a real operation, enforce an invariant, or reduce actual coupling; an extra interface or factory is not a quality improvement by itself.

Prefer direct implementations and existing platform capabilities. Avoid speculative configuration switches and permanent old/new implementations when the task permits one supported migration path. Preserve compatibility mechanisms only when compatibility is an actual requirement. Make expensive I/O and batch preparation explicit rather than hiding repeated work inside a fallback.

### Fail clearly at the boundary that owns the contract

Validate external input, configuration, and lifecycle prerequisites at their authoritative boundary. Use types and validated state so internal code can trust those guarantees; do not repeat the same null checks or invent defaults at every layer. Reject an unsupported operation with an actionable error before it causes invalid side effects. Follow the owning API's exception or result contract; fail-fast does not mean converting every expected business outcome into an exception.

Do not turn missing configuration, invalid state, or infrastructure failure into apparent success with catch-all handlers, empty results, silent repair, or an unbounded/expensive alternate path. Recovery belongs in code only when its trigger, semantics, cost, and stopping condition are part of the supported contract. Propagate cancellation and preserve the original failure when no such recovery exists.

### Preserve essential correctness

Keep checks and coordination that protect promised isolation, authorization, transaction integrity, concurrency, cancellation, or resource ownership. Their necessity comes from the contract and consequences, not event frequency. A race or rollback may be uncommon and still be fundamental framework behavior. Do not relabel correctness guarantees as defensive clutter.

Encapsulate invariant enforcement with the state owner. Keep external or expensive work outside critical sections. Do not move I/O onto a data model just because it updates many fields; DTOs, options, immutable snapshots, and transport models may remain data-focused.

### Document limits instead of implementing speculative compatibility

State prerequisites, supported operations, failure behavior, and unsupported combinations at the public declaration and owning spec/reference. Explain the supported caller action when one exists. Keep each rule with one owner and link to it instead of duplicating manuals.

Documentation is sufficient for a limitation that does not need runtime enforcement. If misuse can silently corrupt data, violate isolation, leak resources, or report false success, retain the smallest necessary guard. A warning in a spec does not replace that guard. Remove obsolete guidance when the contract changes.

## Decisions in practice

| Situation | Decision |
| --- | --- |
| One application must modify data and then calculate from its pending changes. | Keep the shared read contract explicit; the application prepares current-operation data for its calculation. Do not add a generic shared-cache bypass. |
| A required option is missing. | Reject it at configuration validation; do not guess a value repeatedly during execution. |
| A stale-state or duplicate-write guard is rarely exercised but protects consistency. | Keep it at the state owner and test the protected behavior. |
| A dependency fails and an empty collection would let the request continue. | Preserve the failure unless the API explicitly defines and exposes that degraded result. |
| A second provider or legacy mode is proposed for possible future use. | Omit it until a supported use case requires it; use a direct implementation now. |
| A small application adapter expresses a business exception. | Keep the policy local; generalize only after demonstrating a shared mechanism and a simpler contract. |

## Scoped simplification workflow

1. Match the request: review means report; implementation or cleanup means edit. Use the named target or the active task's diff. Keep unrelated worktree changes outside the scope.
2. Inspect relevant callers, tests, and contracts. Identify the owner and distinguish required guarantees, application policy, and speculative mechanisms before deleting anything.
3. Preserve the task's intended behavior. Check observable results, errors, cancellation, ordering, side effects, ownership, and concurrency. For wrapper removal, also compare dependency-specific semantics such as tracking or transaction participation. Treat any narrowing of supported behavior as an explicit behavior change within the authorized scope, not incidental cleanup.
4. Edit the smallest coherent responsibility. Prefer subtraction, direct data flow, and meaningful names. Do not split methods merely to shorten them, merge distinct responsibilities to save lines, or replace readable control flow with clever expressions. Retain comments explaining non-obvious intent or constraints.
5. Verify the affected behavior and run the repository-required gates. Tests should protect contracts and failure boundaries, not implementation shape. Report what became simpler, any deliberate behavior changes, and the verification performed. Stop when further edits would only express taste or anticipate hypothetical needs.
