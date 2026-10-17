# Call a published application contract

## Contract and generation ownership

`Monica.Application.Annotations.ApiEndpoint(ApiHttpMethod, route)` is the single operation declaration for Local and HTTP callers. It retains HTTP method, relative route and binding even when only Local adapters are generated. In the exact `*.PublishedLanguages.Domain{Domain}.Requests` namespace, it generates `I{Domain}CommandApi` / `I{Domain}QueryApi` beside the request. Domain and command/query kind follow namespace/request conventions. The generated method name is always the request type name with its `Command` or `Query` prefix removed: `CommandApproveOrder` becomes `ApproveOrder`, and `QueryGetOrder` becomes `GetOrder`. The HTTP path is independent of this naming rule. Supply a non-empty XML summary and an `IRemoteResultEnvelope<TSelf>` result (`Res`, `Res<T>` and `ResPaged<T>` qualify). Internal unattributed requests remain mediator-only. Declaring HTTP metadata does not publish a receiver or select HTTP transport.

```csharp
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

/// <summary>Reads one order.</summary>
[ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
public sealed record QueryGetOrder(Guid Id) : IResultRequest<OrderDto>;
```

There is no second HTTP endpoint attribute or parameterless operation-only variant. `ApiEndpoint`, `ApiHttpMethod`, `ApiRequestBinding` and the optional assembly-level `HttpApiContract` are metadata in Monica.Application and do not require Monica.WebApi.

Add the AutoController generator as an analyzer to the contract assembly and to the host/adapter that generates implementations. The host owns the caller transport and its handler assemblies:

```csharp
[assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(
    Monica.Rpc.Models.RpcTransportKind.Local, typeof(CommandCreateOrder),
    HandlerAssemblies = new[] { typeof(CommandHandlerCreateOrder) })]
#if DEBUG || MONICA_HTTP_ENDPOINTS
[assembly: Monica.WebApi.Annotations.WebApiGenerationConfig]
#endif
```

Marker types select assemblies, not individual operations. Source handlers are included automatically; referenced handlers require explicit ownership. Local mode generates Local clients and no network receivers unless HTTP publication is enabled. Http mode generates HTTP clients and matching receivers for owned handlers in Debug and Release. A remote UI can omit HandlerAssemblies to generate clients only. Referencing a protocol does not publish another service's operations. Host generation can consume Release-built protocol and handler assemblies; shared interfaces never depend on the host's DEBUG symbol.

The monolith profile uses Local for internal collaboration. The microservice profile uses Http for cross-service collaboration; Dapr supplies HTTP delivery. Grpc generation and runtime selection fail explicitly until a real schema/client/receiver adapter exists. Runtime registration must match generated capabilities. Do not generate the same client adapters both in Protocol and in the consuming host.

## HTTP wire mapping and publication

Generated HTTP clients and receivers use the same ApiEndpoint method, route and binding. `Binding = Auto` chooses query binding for GET/DELETE and body binding for other methods; explicit binding overrides remain supported. The complete path defaults to `api/v1/{Domain}/{Route}`. Query read-only semantics are independent of the HTTP method. Local calls dispatch the typed request without performing HTTP serialization or route binding.

The optional `[assembly: HttpApiContract("api/v2")]` in the request assembly changes the shared prefix without selecting transports or exposing receivers. It is assembly-wide configuration, not a second per-operation declaration. `DomainName` is needed for private HTTP contracts outside PublishedLanguages. Use the default prefix without any contract configuration when it is sufficient.

`WebApiGenerationConfig` in the publishing host enables optional HTTP receivers; also compose `AddWebApi`, discover the generated controller assembly, and use the normal web host lifecycle. A Release monolith can opt in with a dedicated compilation symbol. Keep interfaces and Local generation outside that conditional. Host publication does not independently rewrite shared wire metadata.

The existing request-assembly `WebApiGenerationConfig(routePrefix, RpcClientTargets = ..., ...)` configuration path remains supported: its HTTP mapping and generated transport bases are retained. Consumer imports for ApiEndpoint and its enums now use Monica.Application.Annotations. To adopt host-owned generation, move wire-only settings to HttpApiContract, remove Protocol client targets, configure ApplicationApiGenerationConfig in the host and opt into its receivers there. RPC types now live in the Monica.Rpc package/namespaces rather than Monica.WebApi.RpcClient; application abstractions live in Monica.Application. Registration method names are unchanged.

## Future gRPC adapter boundary

gRPC remains unsupported; the following is the intended extension boundary, not a current feature. Reuse the domain, command/query interface, operation name, and request/result contracts to generate paired caller and owned receiver adapters. Start with unary calls; streaming needs an explicit contract design. Receivers enter the existing application pipeline, with consistent identity, authorization, cancellation/deadline and result/error mapping.

A gRPC channel needs a destination host/port. Native gRPC method addressing follows `/fully.qualified.Service/Method`, using HTTP/2 POST; it does not reuse an arbitrary REST route such as `orders/{Id}`. Derive stable service/method names from versioned domain contracts, independently of HTTP paths. See the [gRPC protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md).

The existing HTTP metadata could later generate optional `google.api.http` mappings for JSON transcoding. Validate route/binding compatibility: ASP.NET route constraints and composite binding cannot be copied blindly into a transcoding rule. Keep ordinary HTTP controller generation independent until that adapter has equivalent behavior. See [ASP.NET Core gRPC JSON transcoding](https://learn.microsoft.com/en-us/aspnet/core/grpc/json-transcoding).

Before generating Protobuf messages, define stable field numbers, supported DTO/result types and versioning. Never recalculate field numbers from the current C# property order or reuse removed numbers. The choice of schema tooling remains future work; adding a gRPC route string alone is insufficient. See the [Protobuf field-number contract](https://protobuf.dev/programming-guides/proto3/#assigning-field-numbers).

## Runtime registration

`monica.AddRpcClient()` discovers concrete `RpcApi` subclasses and registers the dependency domains selected by `IRpcClientDomainInfoProvider.GetDependencyDomains()`. Supply that provider even when the selection is empty. Each client declares `[RpcClientDomain("DomainName")]` matching the dependency enum and implements exactly one interface extending `IRpcApi`. Missing selected clients or incompatible transport implementations fail composition. Add adapter assemblies to Monica type discovery; source generation is not runtime scanning.

```csharp
builder.AddMonica(monica =>
{
    monica.AddApplication();
    monica.AddRpcClient()
        .ConfigDomainInfoProvider(domainProvider)
        .UseLocalTransport();
});
```

`domainProvider` is the application's existing IRpcClientDomainInfoProvider. Local never needs its network destination. AddApplication supplies the operation boundary: independent outer calls have fresh scopes and trusted identity, nested Local calls share the active operation/UnitOfWork. A host deliberately using only RpcClient retains mediator dispatch in its caller-owned scope. UI composition and result/identity rules are detailed in [application invocation](../../monica-application-ui/references/application-invocation.md).

HTTP remains the default runtime transport. Configure `ConfigHttpClientRegisterProvider<TProvider>()` and `UseHttpTransport()`, or use Dapr's `UseDaprProvider()`. Providers implement IRpcHttpClientRegisterProvider and configure named-client destinations from GetDomain; destination discovery is not automatic. Retain existing domain-to-app-ID mapping and ConfigHttpClientBuilder policies. No ForDomains or provider API rename is introduced in this delivery.

HTTP calls use IRemoteCallClient, propagate cancellation and add no retries. CallTimeout defaults to 60 seconds including body reads; MaxResponseBodyBytes defaults to 16 MiB. A shorter borrowed-client timeout can win. Handle result status and required data separately; IsOk does not guarantee a payload. See [exception diagnostics](../../monica-infra-hosting/references/exception-diagnostics.md) for structured failure handling.

Source and checks: `Monica.Rpc/Modules/ModuleRpcClient.cs`, `Monica.Application/Invocation`, `Monica.Generators.AutoController`, `tests/Test.Monica.Application` and `tests/Test.Monica.Generators.AutoController/ApplicationApiGenerationTests.cs`. Existing HTTP transport behavior is covered by `tests/Test.Monica.WebApi/RpcClient/RemoteCallClientTests.cs`.
