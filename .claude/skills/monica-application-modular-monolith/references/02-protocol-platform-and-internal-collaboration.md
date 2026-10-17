# Platform Protocol and Internal Collaboration

Use this file before introducing a new dependency between domains.

## Default Rule

Other domains may depend on `Shared/Platform.Protocol/PublishedLanguages`, shared `Contracts/`, and only the deliberate `Implementations/*` surfaces exposed there, not on another domain's internal implementation or another domain's `Application` handlers.

Within solution-project references, use the chain `Domains.{Subdomain} -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks`. Do not make domains jump directly to `Platform.Protocol` or `Platform.BuildingBlocks`.

## What Belongs in Platform.Protocol

- Stable published requests under strict `PublishedLanguages.Domain{Domain}.Requests` namespaces. Add `[ApiEndpoint]` only when the request is an RPC operation.
- Published RPC results use `Res`, `Res<T>`, `ResPaged<T>`, or a custom `IRemoteResultEnvelope<TSelf>` whose `CreateRemoteFailure` preserves constructor invariants.
- DTOs
- Enums
- Cross-domain events
- Optional `Contracts/` for deliberate synchronous collaboration
- Optional `Implementations/Local` for checked-in local or actor-backed providers

## What Stays Internal

- Entities
- Repositories
- EF configuration
- Infrastructure adapters
- Domain-owned application handlers and background workers
- Internal domain services and implementation details

## Collaboration Choices

- Use direct contract-driven requests when another domain needs synchronous data or command execution.
- Use events when the collaboration can be asynchronous and loosely coupled.
- Generated interfaces are I{Domain}CommandApi and I{Domain}QueryApi. Host ApplicationApiGenerationConfig selects Local or Http adapters independently of interface generation. Do not check in duplicate generated surfaces.
- Keep multipart, download, raw-object, and other transport-only requests beside their handlers as local HTTP endpoints; they must not enter published RPC language.
- Do not use JSON snapshots, `AdditionalFiles`, bootstrap scans, or source-tree writes for RPC discovery.
- Keep collaboration intentional. A modular monolith should not simulate service calls for everything, but it should still preserve domain boundaries.
