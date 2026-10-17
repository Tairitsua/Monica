# Platform Protocol and Service Contracts

Use this file before adding anything to `Shared/Platform.Protocol`.

## Shared Platform Responsibilities

- `Platform.BuildingBlocks` contains project-agnostic infrastructure building blocks and third-party framework extensions.
- `Platform.Infrastructure` contains solution-owned infrastructure setup and integration wiring.
- `Platform.Protocol` contains the shared business language used across services.

Within solution-project references, use the chain `{Subdomain}Service.API -> {Subdomain}Service.Domain -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks`. Do not make service projects jump directly to `Platform.Protocol` or `Platform.BuildingBlocks`.

## What Belongs Here

- Put requests that another service or shared gateway may invoke into the strict `Platform.Protocol/PublishedLanguages/Domain{Domain}/Requests` namespace.
- Add `[ApiEndpoint(ApiHttpMethod, route)]` to published `Command*` or `Query*` operations. This one attribute owns HTTP method, route and binding; namespace and request family determine domain and command/query kind. Generated method names always remove the request name's Command or Query prefix. Retain its HTTP metadata for Local calls without publishing HTTP receivers.
- Use `Res`, `Res<T>`, `ResPaged<T>`, or a custom `IRemoteResultEnvelope<TSelf>` for published RPC results. A custom envelope must create transport failures through its normal constructor in `CreateRemoteFailure`.
- Put DTOs and enums that form part of a stable service contract into `Platform.Protocol/PublishedLanguages`.
- Put events that other services may subscribe to into `Platform.Protocol/PublishedLanguages`.
- Put optional checked-in synchronous abstractions into `Platform.Protocol/PublishedLanguages/.../Contracts`.
- Put optional checked-in local or actor-backed providers into `Platform.Protocol/PublishedLanguages/.../Implementations/Local`.

## What Does Not Belong Here

- Persistence entities
- EF configuration
- Repository abstractions
- Internal-only domain objects
- Transport-only controller or endpoint models that are not part of the service contract
- Multipart, download, raw-object, and other HTTP-only request shapes; co-locate these with their service handlers instead

## Collaboration Defaults

- Prefer async collaboration through events when eventual consistency is acceptable.
- Prefer direct service contracts only when the caller truly needs synchronous data or command execution.
- Generated interfaces are I{Domain}CommandApi and I{Domain}QueryApi. Host ApplicationApiGenerationConfig selects Local or Http adapters independently of interface generation. Do not check in duplicate generated surfaces.
- Keep `PublishedLanguages` narrow. If a contract is only used inside one service, keep it local.
- Do not use metadata JSON, `AdditionalFiles`, bootstrap scans, or source-tree writes to discover RPC operations; attributed published source is the contract.

## Temporal Transport Contract

- Use `DateTime` only for a timezone-free wall-clock value. Monica's default wire policy transmits it as `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF`; a host may select the built-in space-separated wall-clock policy for its complete RPC contract. Both policies omit trailing fractional zeroes, preserve significant ticks, and do not transport `DateTimeKind`.
- Expect Monica's default HTTP query binding and canonical JSON converter to deliver `DateTimeKind.Unspecified`. A host that replaces the `DateTime` JSON converter owns its custom body semantics. Do not infer UTC or the server's local zone from the received value.
- Use `DateTimeOffset` for an instant or explicit offset. Monica preserves it with the round-trip `"O"` format.
- Do not choose the shorter `"s"` format for contract fields because it drops sub-second precision, and do not add per-request formatting workarounds. Generated query/route clients and host-owned JSON options already apply the transport contract.

## Stability Rules

- Change published contracts deliberately. They are not internal implementation details.
- Prefer additive evolution over rewriting contract meaning in place.
- Keep payloads explicit and serializable.
