# Application Invocation

`ApplicationService`, `IQuery<TResponse>` and `ICommand<TResponse>`/`ICommand` belong to `Monica.Application.Abstractions`. UI and application services consume the same generated `I{Domain}CommandApi` and `I{Domain}QueryApi` contracts from Protocol. Do not introduce a per-page forwarding Facade or another generic business-facing dispatch interface.

## Published contracts and adapters

`Monica.Application.Annotations.ApiEndpoint(ApiHttpMethod, route)` declares the operation and its HTTP metadata even for Local-only clients. Requests in the exact `*.PublishedLanguages.Domain{Domain}.Requests` namespace generate stable interfaces without HTTP configuration. Unattributed requests remain internal mediator operations. Each `[ApplicationHost]` combines the generated shared catalog where used with its referenced local role assemblies. Ordinary operations prefer Local when their implementation is included, including operations whose role is inactive; otherwise a published call uses its known remote owner. RunOnRole constrains only operations needing that role's runtime resources. See the authoritative [API generation and registration contract](../../monica-infra-messaging/references/invocation.md) for configuration, HTTP mapping, legacy migration and unsupported gRPC.

UI injects `IOrderingCommandApi` or `IOrderingQueryApi` and calls the named method with a Protocol request and cancellation token. The generated Local implementation delegates to invocation infrastructure; application handlers use the same interfaces for published cross-domain operations.

## Same-process Interactive Server

Compose `AddApplicationUI()`, `AddApplicationModel(...)` and `AddApplicationInvocation()` in the native business Host. Configure discovery to retain Protocol, the catalog where used, and actual local domain cores and role assemblies. AddWebApi serves the selected role receivers. See [application model composition](../../monica-infra-hosting/references/application-model.md). `AddApplicationUI` requires the application execution and UI hosting modules, without adding the default Shell. Persistence and `AddUnitOfWork()` remain explicit host choices.

An outer Local call captures the trusted caller, creates an asynchronous DI scope, initializes `IApplicationOperationContributor` instances, and enters Monica's mediator/execution pipeline. Circuit calls read the current `AuthenticationStateProvider`; prerender, HTTP and background callers use their native current principal. An explicit anonymous principal suppresses ambient fallback. Context is restored and the scope disposed after completion. Concurrent outer calls have independent scopes.

Application business entries mark the active receiving operation, including HTTP mediator and EventBus entries. Nested generated calls default to Shared: eligible Local calls reuse that provider and UnitOfWork, propagating failures until the outer pipeline has unwound. Use `.WithTransaction(ApiTransaction.Independent)` when a separate operation and commit are intended; the typed view leaves the original client unchanged. Independent children preserve trusted caller identity and restore the parent after completion. Await nested calls; do not retain operation services or launch detached work with their context. Shared rejects remote dispatch while the caller has an active transaction; a root remote call without one remains valid. See the [transaction contract](../../monica-infra-persistence/references/transactions.md) for participation and recovery semantics.

`IApplicationOperationContributor` belongs to `Monica.Application.Abstractions`. Register contributors in the caller scope to copy trusted tenant/application context into the operation before resolving handlers. They must tolerate concurrent calls, must not retain the supplied provider, and must not trust client-supplied identity/tenant claims directly. Culture flows through the asynchronous context.

Published `Query*` requests default to read-only execution even without HTTP or with HTTP POST. Published `Command*` requests retain write semantics even with an explicit HTTP GET mapping. Concrete handler `ExecutionTransaction` metadata is authoritative. Query semantics do not promise that UI orchestration across several calls is atomic; atomic orchestration belongs in one backend use case.

## Validation, failures and cancellation

`AddApplication()` composes mediator dispatch, conventional DI, mapping, authorization and DataAnnotations input validation without HTTP endpoints. Configure real host authentication/policies; unannotated handlers remain public. `AddAuthorization<TEnum>` is available for Monica permission bits.

Validate request DataAnnotations and `IValidatableObject` at the application boundary. `[ValidateChildren]` explicitly traverses nested contracts. Form validation is feedback, not authoritative validation; malformed JSON and HTTP binding remain adapter concerns. Domain invariants remain in the domain.

The outer Local entry maps failures only after the backend pipeline unwinds, retaining rollback semantics. It constructs the contract's result envelope and preserves structured public errors while removing reserved diagnostics before presentation. Caller cancellation propagates and does not prove a committed write was rolled back. Return materialized DTOs, not entities, deferred queries or scope-owned services.

Do not inject handlers, repositories, DbContexts or UnitOfWork managers into circuit state. Inject generated interfaces rather than concrete LocalApiClient implementations. Their infrastructure boundary owns operation lifetimes.

## Remote presentation

An independently deployed server UI without handlers uses the existing explicit ApplicationApiGenerationConfig HTTP callers from the same Protocol interfaces and configures the existing HTTP provider or Dapr integration. It need not own handlers or publish receivers. The backend must publish the matching operations and enforce authorization; define authentication propagation explicitly. A business Host retains eligible Local calls and publishes only included active receivers. A selective server host may instead reference Protocol and the build-generated lightweight catalog without backend implementations; this is not arbitrary runtime manifest loading. Browser-executed clients need a separate compatible contract/dependency profile; these server libraries do not establish WebAssembly compatibility.
