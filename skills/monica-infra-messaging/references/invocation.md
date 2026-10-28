# Call a published application contract

## Vocabulary and packages

Application invocation means executing a published application operation through its stable API, independently of deployment. `Monica.Invocation` owns API clients, invocation routing and delivery adapters. `Api` names the contract, `ApiClient` its consumer-side implementation, and `Invoker` the service that routes or executes a call. `Caller` identifies the initiating principal or calling context, not a generated client type. Local means execution in the same process through the application boundary; it does not mean a loopback HTTP request or another process on the same machine.

Generated domain interfaces extend `IApplicationApi`. An application host generates `Application{Domain}{Command|Query}ApiClient`, which delegates to `IApplicationApiInvoker` without inheriting a transport base. Explicit adapters generate `Local{Domain}{Command|Query}ApiClient` or `Http{Domain}{Command|Query}ApiClient`, deriving from `LocalApiClient` or `HttpApiClient`; their shared base is `ApiClient`. `ILocalApiInvoker` owns the Local execution boundary.

Use `AddApplicationInvocation()` for application-model routing. It composes `ModuleApiClient` internally; configure `AddApiClient()` only when selecting standalone adapters or customizing the shared HTTP provider and call limits. Standalone transport defaults to HTTP. Application-model routing chooses each operation from authority, required role and actual local implementation availability.

`ConfigHttpClientBuilder` contributions accumulate in declaration order for each generated named HTTP client, including authority-scoped clients. A later contribution no longer replaces an earlier one; remove obsolete contributions explicitly when migrating a host that depended on replacement. Use this hook for service discovery, handlers and primary-handler selection. HTTP factory handlers are pooled: resolve request-specific state at send time from its owning request/operation context, rather than capturing a request-scoped service in a handler constructor. Framework authentication and configured call limits continue to apply.

## Contract and generation ownership

`Monica.Application.Annotations.ApiEndpoint(ApiHttpMethod, route)` is the single operation declaration for Local and HTTP clients. It retains HTTP method, relative route and binding even when only Local adapters are generated. In the exact `*.PublishedLanguages.Domain{Domain}.Requests` namespace, it generates `I{Domain}CommandApi` / `I{Domain}QueryApi` beside the request. Domain and command/query kind follow namespace/request conventions. The generated method name is always the request type name with its `Command` or `Query` prefix removed: `CommandApproveOrder` becomes `ApproveOrder`, and `QueryGetOrder` becomes `GetOrder`. The HTTP path is independent of this naming rule. Supply a non-empty XML summary and an `IRemoteResultEnvelope<TSelf>` result (`Res`, `Res<T>` and `ResPaged<T>` qualify). Internal unattributed requests remain mediator-only. Declaring HTTP metadata does not publish a receiver or select HTTP transport.

```csharp
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

/// <summary>Reads one order.</summary>
[ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
public sealed record QueryGetOrder(Guid Id) : IResultRequest<OrderDto>;
```

There is no second HTTP endpoint attribute or parameterless operation-only variant. `ApiEndpoint`, `ApiHttpMethod`, `ApiRequestBinding` and the optional assembly-level `HttpApiContract` are metadata in Monica.Application and do not require Monica.WebApi.

Add the AutoController generator as an analyzer to the contract assembly and to each application host. Protocol owns generated interfaces; the host owns implementations, the manifest, and HTTP receivers. Do not generate the same adapters in both projects.

## Application hosts and selective artifacts

Declare one domain core with `[assembly: ApplicationDomain(ProductDomains.Ordering)]` and each role library with `[assembly: ApplicationRole("Ordering", ProductDomains.Ordering)]`. These annotations live in `Monica.ApplicationModel.Annotations`. A role ID is independent of the wire domain and AppId. Optional `WorkloadOwner` on ApplicationRole preserves a pre-existing durable job owner when projects are renamed; it does not select API destinations. A domain's optional `BackgroundRole` identifies a role in that same domain and survives ownership catalog export. `Registration` identifies executable local composition and is never exported to a remote catalog; see [native registration](../../monica-infra-hosting/references/application-model.md).

Mark each executable `[assembly: ApplicationHost]` and reference only its included domain and role libraries. Selective artifacts also reference the lightweight catalog generated from owned implementation metadata; see [catalog build integration](../../monica-infra-hosting/references/application-model.md). The generator combines shared ownership with actual local bindings, scoped clients and local HTTP receivers in both Debug and Release. Private/internal local handler types are supported through CLR metadata names; their request types must be accessible to the host. Duplicate local handlers, undeclared ownership, missing implementations for included roles, and a RunOnRole requirement that contradicts the handler owner fail generation. A remote operation description does not require its handler assembly locally.

```csharp
builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options => options.AppId = "ordering-api");
    monica.AddApplicationModel(options => options.Applications = new Dictionary<string, ApplicationPlacement>
    {
        ["ordering-api"] = new() { Roles = ["Ordering"] },
        ["ordering-realtime"] = new() { Roles = ["Ordering.Realtime"] }
    });
    monica.AddApplication();
    monica.AddApplicationInvocation();
    monica.AddApiClient().ConfigHttpClientRegisterProvider<OrderingHttpProvider>();
    monica.AddAutoControllers();
});
```

The placement map contains every known product role exactly once; every role assigned to the current AppId must be implemented by the current artifact. `ApplicationPlacement` lives in `Monica.ApplicationModel.Models`; the example's provider implements `IApiHttpClientRegisterProvider`. Its `ConfigureHttpClientFactoryOptions` receives the actual destination AppId. `UseDaprProvider()` can supply that HTTP delivery provider instead. No domain-to-AppId provider or per-interface registration list is needed for this host model. Finish the normal Web lifecycle with `UseMonica()` and `MapMonica()`.

Inject the generated `IOrderingCommandApi` / `IOrderingQueryApi` interfaces. Ordinary operations execute locally when their implementation is included, even when their role does not own ingress on the current AppId. An omitted published operation uses its catalog owner and configured AppId through HTTP. `[RunOnRole("Ordering.Realtime")]` requires that exact role: execute locally only when the implementation is included and the role is active, otherwise call its configured owner. Do not add RunOnRole merely to compensate for a selective artifact. Missing remote providers fail composition; unavailable remote owners produce the existing remote failure envelope and never trigger Local fallback. Cancellation propagates. gRPC remains unsupported. A host whose calls all have eligible local implementations and no authority forwarding needs no HTTP provider. Omitted implementations cannot become local through deployment configuration.

Role ownership selects individual generated HTTP actions, so one domain controller can safely aggregate operations from several roles. Only included, active role actions enter MVC's endpoint descriptors; remote-only operations never create forwarding receivers. Receiver actions dispatch directly through the application mediator; they never call the generated outbound caller. The mediator also rejects inactive RunOnRole requests before constructing their handlers. Role selection does not disable included ordinary application handlers or in-operation local event handlers.

An application with primary/secondary or similar authority rules can select one scoped policy with `AddApplicationInvocation().UseAuthorityPolicy<TPolicy>()`. `IApplicationInvocationAuthorityPolicy.ResolveAsync` runs before normal placement/local-availability routing. `ApplicationInvocationAuthorityContext.HandlerType` is nullable: it identifies a verified local handler when present and is null for remote-only operations. Apply authority rules from trusted request/operation metadata even when it is null; do not load a remote handler assembly or silently skip policy. Keep required policy metadata on shared compiled contracts available to catalog consumers. Return null to preserve normal placement, or `ApplicationInvocationDestination(appId, providerKey)` to force a preconfigured remote destination. Register a named authority transport with `ConfigAuthorityHttpClientProvider<TProvider>(providerKey)`. A different provider scope can target the same logical AppId in another node/namespace. For RunOnRole operations the destination must still be that role's mapped logical owner. Unknown targets/provider keys fail before sending. Policy cannot trust request payloads as identity or decide authority from network failure.

AddApplication supplies the operation boundary: outer Local calls have fresh scopes and trusted identity; nested Local calls default to sharing the current operation and UnitOfWork. Its behavior covers every business execution entry, including EventBus, and establishes the receiving scope before business work. HTTP begins a separate receive operation.

Generated interfaces expose `WithTransaction(ApiTransaction.Independent)` from `Monica.Invocation.Models`; the original client remains Shared. The returned typed view does not mutate the injected client or create a transaction until a method executes. An independent Local invocation resolves a fresh operation scope and preserves trusted caller identity; an independent HTTP invocation uses the receiver's normal policy. Descendants default to Shared inside their selected operation. Query/None entries are not forced to create write transactions.

When a caller already has a local transaction, Shared rejects a remote destination **before sending**, including RunOnRole and authority forwarding to another instance with the same AppId. A normal root remote call without an active transaction is allowed. Routing never bypasses required roles, and Shared never silently becomes Independent. Even a handled shared failure leaves the caller rollback-only. Choosing Independent accepts that the child's successful commit survives a later caller failure. See [transaction participation and database composition](../../monica-infra-persistence/references/transactions.md#shared-and-independent-api-calls) for examples, same-connection requirements, default database guidance and recovery responsibilities.

## Standalone adapter generation

When adopting explicit transaction participation, rebuild Protocol and every consuming host with matching Invocation and generator versions. Generated interfaces now include typed `WithTransaction` views; custom `IApplicationApiInvoker` and `ILocalApiInvoker` implementations must accept and honor `ApiTransaction`. Existing nested remote writes that relied on implicit independent commit must choose Independent explicitly. There is no compatibility switch that silently weakens Shared.

Existing hosts that have not adopted the application model can still select client transport and handler assemblies explicitly:

```csharp
[assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(
    Monica.Invocation.Models.ApiClientTransportKind.Local, typeof(CommandCreateOrder),
    HandlerAssemblies = new[] { typeof(CommandHandlerCreateOrder) })]
#if DEBUG || MONICA_HTTP_ENDPOINTS
[assembly: Monica.WebApi.Annotations.WebApiGenerationConfig]
#endif
```

Marker types select assemblies, not individual operations. Source handlers are included automatically; referenced handlers require explicit ownership. Local mode generates Local clients and no network receivers unless HTTP publication is enabled. Http mode generates HTTP clients and matching receivers for owned handlers in Debug and Release. A remote UI can omit HandlerAssemblies to generate clients only. Referencing a protocol does not publish another service's operations. Host generation can consume Release-built protocol and handler assemblies; shared interfaces never depend on the host's DEBUG symbol.

For these standalone adapters, Local supplies in-process collaboration and Http supplies remote collaboration; Dapr supplies HTTP delivery. Grpc generation and runtime selection fail explicitly until a real schema/client/receiver adapter exists. Runtime registration must match generated capabilities. This standalone selection is not the placement policy used by ApplicationHost.

## HTTP wire mapping and publication

Generated HTTP clients and receivers use the same ApiEndpoint method, route and binding. `Binding = Auto` chooses query binding for GET/DELETE and body binding for other methods; explicit binding overrides remain supported. The complete path defaults to `api/v1/{Domain}/{Route}`. Query read-only semantics are independent of the HTTP method. Local calls dispatch the typed request without performing HTTP serialization or route binding.

The optional `[assembly: HttpApiContract("api/v2")]` in the request assembly changes the shared prefix without selecting transports or exposing receivers. It is assembly-wide configuration, not a second per-operation declaration. Private HTTP contracts outside PublishedLanguages infer their domain from the declaring assembly's `ApplicationRole`; an explicit `DomainName` preserves an existing wire name. Without a declaring role, or when its domain is not a valid generated C# identifier, supply `DomainName`. Use the default prefix without any contract configuration when it is sufficient.

For standalone generation, `WebApiGenerationConfig` in the publishing host enables optional HTTP receivers; also compose `AddWebApi`, discover the generated controller assembly, and use the normal web host lifecycle. A Local-only host can opt in with a dedicated compilation symbol. Keep interfaces and Local generation outside that conditional. ApplicationHost always compiles its required receivers and chooses active actions from role placement. Host publication does not independently rewrite shared wire metadata.

The request-assembly `WebApiGenerationConfig(routePrefix, ApiClientTargets = ..., ...)` configuration path remains supported. Consumer imports for ApiEndpoint and its enums use Monica.Application.Annotations. To adopt host-owned generation, move wire-only settings to HttpApiContract, remove Protocol client targets, configure ApplicationApiGenerationConfig in the host and opt into its receivers there.

When upgrading from the former RPC names, replace the `Monica.Rpc` project/package reference and namespace imports with `Monica.Invocation`, `AddRpcClient()` with `AddApiClient()`, and `AddApplicationRpc()` with `AddApplicationInvocation()`. Rename custom client bases from `RpcApi` / `LocalRpcApi` / `HttpRpcApi` to `ApiClient` / `LocalApiClient` / `HttpApiClient`; markers and providers become `IApplicationApi`, `ApiClientDomain`, `IApiClientDomainInfoProvider` and `IApiHttpClientRegisterProvider`. Generation options use `ApiClientTransportKind`, `ApiClientGenerationTargets` and `ApiClientTargets`. Authority extensions use `IApplicationInvocationAuthorityPolicy`, `ApplicationInvocationAuthorityContext` and `ApplicationInvocationDestination`. Regenerate clients so concrete types carry the `ApiClient` suffix; domain interface names, operation names and HTTP mappings are stable. There are no aliases for the former API names.

Requests that used `RpcRequest` or `IHasRpcHttpInfo` now use `ApiRequest` or `IHasApiHttpInfo`. Their optional `Headers` payload keeps its existing shape and trust rules. Ordinary application requests can implement `IResultRequest` directly without inheriting a transport-metadata base. Actor clients remain explicit adapters and retain their runtime routing and execution behavior. Actor chain tracing uses `UseActorInvocationTracing()`.

## Future gRPC adapter boundary

gRPC remains unsupported; the following is the intended extension boundary, not a current feature. Reuse the domain, command/query interface, operation name, and request/result contracts to generate paired client and owned receiver adapters. Start with unary calls; streaming needs an explicit contract design. Receivers enter the existing application pipeline, with consistent identity, authorization, cancellation/deadline and result/error mapping.

A gRPC channel needs a destination host/port. Native gRPC method addressing follows `/fully.qualified.Service/Method`, using HTTP/2 POST; it does not reuse an arbitrary REST route such as `orders/{Id}`. Derive stable service/method names from versioned domain contracts, independently of HTTP paths. See the [gRPC protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md).

The existing HTTP metadata could later generate optional `google.api.http` mappings for JSON transcoding. Validate route/binding compatibility: ASP.NET route constraints and composite binding cannot be copied blindly into a transcoding rule. Keep ordinary HTTP controller generation independent until that adapter has equivalent behavior. See [ASP.NET Core gRPC JSON transcoding](https://learn.microsoft.com/en-us/aspnet/core/grpc/json-transcoding).

Before generating Protobuf messages, define stable field numbers, supported DTO/result types and versioning. Never recalculate field numbers from the current C# property order or reuse removed numbers. The choice of schema tooling remains future work; adding a gRPC route string alone is insufficient. See the [Protobuf field-number contract](https://protobuf.dev/programming-guides/proto3/#assigning-field-numbers).

## Standalone runtime registration

`monica.AddApiClient()` discovers concrete `ApiClient` subclasses and registers the dependency domains selected by `IApiClientDomainInfoProvider.GetDependencyDomains()`. Supply that provider even when the selection is empty. Each client declares `[ApiClientDomain("DomainName")]` matching the dependency enum and implements exactly one interface extending `IApplicationApi`. Missing selected clients or incompatible transport implementations fail composition. Add adapter assemblies to Monica type discovery; source generation is not runtime scanning.

```csharp
builder.AddMonica(monica =>
{
    monica.AddApplication();
    monica.AddApiClient()
        .ConfigDomainInfoProvider(domainProvider)
        .UseLocalTransport();
});
```

`domainProvider` is the application's existing IApiClientDomainInfoProvider. Local never needs its network destination. AddApplication supplies the operation boundary: independent outer calls have fresh scopes and trusted identity, nested Local calls share the active operation/UnitOfWork. A host deliberately using only ApiClient retains mediator dispatch in its caller-owned scope. UI composition and result/identity rules are detailed in application invocation in `$monica-application-ui` (`references/application-invocation.md`).

HTTP remains the default standalone transport. Configure `ConfigHttpClientRegisterProvider<TProvider>()` and `UseHttpTransport()`, or use Dapr's `UseDaprProvider()`. Providers implement IApiHttpClientRegisterProvider and configure named-client destinations from GetDomain; destination discovery is not automatic. Retain existing domain-to-app-ID mapping and ConfigHttpClientBuilder policies in standalone consumers. ApplicationHost clients use the operation/role map and local implementation availability described above. Standalone domain selection uses ConfigDomainInfoProvider; there is no ForDomains API.

HTTP calls use IRemoteCallClient, propagate cancellation and add no retries. CallTimeout defaults to 60 seconds including body reads; MaxResponseBodyBytes defaults to 16 MiB. A shorter borrowed-client timeout can win. Handle result status and required data separately; IsOk does not guarantee a payload. See [exception diagnostics](../../monica-infra-hosting/references/exception-diagnostics.md) for structured failure handling.

Source and checks: `Monica.Invocation/Modules/ModuleApplicationInvocation.cs`, `Monica.Invocation/Modules/ModuleApiClient.cs`, `Monica.Application/Invocation`, and `Monica.Generators.AutoController`. `tests/Test.Monica.Generators.AutoController/ApplicationHostSourceGeneratorTests.cs` covers host generation; `ApplicationApiGenerationTests.cs` covers standalone adapters. Runtime placement, authority, cancellation, and receiver selection are covered by `tests/Test.Monica.ApplicationInvocation/ApplicationInvocationTests.cs`; `RemoteCallClientTests.cs` covers HTTP failure decoding.

## Native discovery and complete-call telemetry

Invocation has no project dependency on `Monica.ServiceDiscovery`. A host may compose its endpoint module and attach `http.AddMonicaServiceDiscovery()` through `AddApiClient().ConfigHttpClientBuilder(...)`; use the [endpoint discovery contract](../../monica-infra-hosting/references/service-discovery.md#outbound-endpoint-discovery) for registration, native `Services` configuration and diagnostics. `IApiHttpClientRegisterProvider` maps the chosen AppId/provider scope to a logical base URI. Authority policy still runs before Local selection; discovery failure never permits local fallback. Register the handler once and avoid global retries that could replay commands.

Meter `Monica.Invocation` emits `monica.invocation.remote.duration` in seconds across the complete remote exchange, including decoding. Its bounded `outcome` distinguishes `success`, `business_failure`, `boundary_failure`, `timeout`, `canceled` and unclassified `exception`; `transport` is `http`, `dapr`, `grpc` or `other`. No request path, identifier, body or exception text becomes a metric tag. The existing call Activity also records transport, logical service and operation. Host exporters own storage and aggregation. Endpoint UI HTTP observations end at response headers and must not be interpreted as these complete-call results. gRPC remains explicitly unsupported by the application invocation adapter.
