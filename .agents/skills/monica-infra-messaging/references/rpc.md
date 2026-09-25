# Call a published request contract

`monica.AddRpcClient()` discovers concrete `RpcApi` subclasses and registers only the dependency domains selected by `IRpcClientDomainInfoProvider.GetDependencyDomains()`. Provide that domain resolver even if no client is eventually selected. Each discovered client needs `[RpcClientDomain("DomainName")]` matching the dependency enum and exactly one interface extending `IRpcApi`. Generated RPC clients come from request-owned published contracts; an attributed request must be in the matching `*.PublishedLanguages.Domain{DomainName}.Requests` namespace. Place the request and its `[ApiEndpoint]` metadata with the application ProjectUnit, following `$monica-application-project-unit-development`.

HTTP is the default transport. For HTTP, composition **always** requires a provider implementing `IRpcHttpClientRegisterProvider`, selected with `ConfigHttpClientRegisterProvider<TProvider>()`; that provider configures the named clients for the domains. `UseLocalTransport()` selects in-process implementations. `UseGrpcTransport()` is reserved and fails validation. The domain provider also supplies each dependent domain's logical name and private destination via `GetDomain(...)`.

```csharp
builder.AddMonica(monica =>
{
    monica.AddRpcClient(options => options.CallTimeout = TimeSpan.FromSeconds(60))
        .ConfigDomainInfoProvider(domainProvider)
        .ConfigHttpClientRegisterProvider<HostRpcHttpClientRegisterProvider>()
        .UseHttpTransport();
});
```

Here `domainProvider` is the host's `IRpcClientDomainInfoProvider` and `HostRpcHttpClientRegisterProvider` is its `IRpcHttpClientRegisterProvider`; both must be supplied by the application. Inspect their domain flags and named-client destination when a client is missing or calls the wrong service. A locally authored client must inherit the transport base (`HttpRpcApi` or `LocalRpcApi`) and carry the domain attribute to participate in automatic registration.

The call path uses `IRemoteCallClient` and propagates caller cancellation without adding retries. `ModuleRpcClientOption.CallTimeout` covers the whole call including body reading and defaults to 60 seconds; `MaxResponseBodyBytes` defaults to 16 MiB. A shorter timeout on the borrowed `HttpClient` can still win. Handle the returned result envelope according to its status and validate a required payload even when status is `IsOk`; success does not prove `Data` is non-null. For structured remote failure documents, see the hosting skill's [exception diagnostics](../../monica-infra-hosting/references/exception-diagnostics.md).

Source and checks: `Monica.WebApi/Modules/ModuleRpcClient.cs`, `Monica.WebApi/RpcClient/Abstractions/IRpcClientDomainInfoProvider.cs`, `IRpcHttpClientRegisterProvider.cs`, `HttpRpcApi.cs`, and `IRemoteCallClient.cs`, plus `tests/Test.Monica.WebApi/RpcClient/RemoteCallClientTests.cs`.
