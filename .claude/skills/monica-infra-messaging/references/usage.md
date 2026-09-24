# Messaging integration

## EventBus

`monica.AddEventBus()` registers a scoped `ILocalEventBus` gateway, handler discovery, subscription registry, and receive dispatcher. Implement `ILocalEventHandler<TEvent>` or `IDistributedEventHandler<TEvent>` for discoverable handlers; `DisableAutoDiscovery` disables that scan. `IEventBus.PublishAsync<TEvent>` matches subscriptions by the exact event type and topic, so a base-type subscription is not a catch-all for derived events. The optional topic name overrides event-name metadata. Pass cancellation into handlers' I/O.

```csharp
builder.AddMonica(monica =>
{
    monica.AddEventBus();
});

await localEventBus.PublishAsync(new OrderApproved(orderId), cancellationToken: cancellationToken);
```

Use `IDistributedEventBus` only after selecting a provider. `UseDistributedEventBus<TProvider>()` requires `TProvider : DistributedEventBusBase` and `IEventTransport`; `UseNoOpDistributedEventBus()` satisfies composition but sends nothing externally. `monica.AddEventBusKafka().UseKafkaProvider(cluster)` selects the native Kafka provider and starts its subscription worker. `AddConfiguredCluster(cluster)` only exposes a cluster to the console; `UseDaprKafkaIntegration(...)` describes an existing Dapr-backed Kafka component and does not select an EventBus provider. `AddKeyedEventBus(key, useDistributed: true)` needs the default distributed provider; `AddKeyedLocalEventBus(key)` creates an isolated keyed local bus. Check provider registration, service key, topic, discovered handler, and subscription startup when nothing arrives. For `[Outbox]` event semantics and `[Inbox]` handler deduplication, read `$monica-infra-persistence`.

## RPC client

`monica.AddRpcClient()` discovers concrete `RpcApi` subclasses and registers clients for the dependency domains returned by the configured `IRpcClientDomainInfoProvider`. The domain provider is required even if no contract is ultimately selected. HTTP is the default transport; `UseLocalTransport()` selects in-process implementations. `UseGrpcTransport()` currently fails composition validation. For HTTP, configure a named-client registration provider when the selected RPC contracts require it, and set `ModuleRpcClientOption.CallTimeout` for the complete call, including body reading (60 seconds by default); `MaxResponseBodyBytes` defaults to 16 MiB. The call path uses `IRemoteCallClient` and propagates caller cancellation without adding retries. A shorter borrowed `HttpClient` send timeout can still win. Do not treat an `IsOk` result as proof of a non-null payload; remote callers must validate the payload their contract requires.

```csharp
builder.AddMonica(monica =>
{
    monica.AddRpcClient()
        .ConfigDomainInfoProvider(domainProvider)
        .UseHttpTransport();
});
```

Request-owned RPC contracts and published endpoints are application concerns; follow `$monica-application-project-unit-development` for their placement. Inspect `Monica.WebApi/Modules/ModuleRpcClient.cs` and `Monica.WebApi/RpcClient/Abstractions/IRemoteCallClient.cs` when implementing a custom transport or response classifier.

## Dapr actor exception diagnostics

`monica.AddDaprClient()` registers the Dapr SDK client and an `IRemoteExceptionDiagnosticsExtractor` adapter. The adapter recognizes `DaprApiException` messages carrying Dapr's actor-service error marker and a valid Monica failure envelope; it validates the reported HTTP error status against the envelope's `status` or historical `code`. It accepts diagnostic metadata or a standard safe `metadata.error` with a nonempty code and trace identifier, so a production downstream response can still be represented structurally without disclosing its exception details. The SDK exposes this actor response through its exception message, so the adapter recovers it during the receiving service's diagnostic projection. It does not interpret arbitrary JSON in an application exception as a remote response.

When the host exposes diagnostics, the local exception catalog entry keeps a short transport message and its local stack. The recovered response appears in that entry's `remote` object with `transport: "dapr-actor"`, its reported status, service and trace information, and its independent exception catalog and chain. Only known diagnostic metadata is captured; business `data` is not copied into diagnostics. Parsing has payload and nesting limits, and malformed or unrecognized payloads retain the original local failure instead of causing a secondary diagnostic error. Escapes are decoded by the JSON parser once, preserving literal backslashes and strings such as `\\u0060`.

The Core `IRemoteExceptionDiagnosticsExtractor` contract lets other transports supply the same structured boundary without a Dapr dependency in Core. This adapter only enriches diagnostics; it does not change exception mapping, retry policy, or transport success/failure classification. Use `$monica-infra-hosting` for `ExposeDiagnosticDetails`, legacy-envelope normalization, and response ID scopes, and `$monica-infra-observability` to attach returned remote results to local call-chain nodes.

## DataChannel

`monica.AddDataChannel().UseSetup<TSetup>()` needs an ASP.NET Core web host and a singleton `IDataChannelSetup`. The setup calls `IDataChannelRegistrar.Add` once per pipeline. Every pipeline has a unique ID and an outer endpoint; the inner endpoint defaults to `DefaultChannelEndpoint`. Registration closes after startup materialization. Runtime callers inject `IDataChannelManager`, fetch a host-owned channel, and call its send method.

```csharp
public sealed class OrderChannelSetup : IDataChannelSetup
{
    public void Setup(IDataChannelRegistrar channels) => channels.Add(
        "orders", pipeline => pipeline.SetOuterEndpoint(new KafkaOptions(ConnectionDirection.Output)
        {
            BootstrapServers = "localhost:9092",
            Topic = "orders"
        }));
}

builder.AddMonica(monica => monica.AddDataChannel().UseSetup<OrderChannelSetup>());

var channel = channels.Fetch("orders")
    ?? throw new InvalidOperationException("orders channel is not registered");
await channel.SendDataFromInnerAsync(message);
```

The example needs `Monica.DataChannel.Providers.Kafka` and `Monica.DataChannel.Abstractions.Communication`; the Kafka broker address is illustrative. Add DI-resolved middleware with `AddPipeMiddleware<T>()`. Duplicate IDs, late registration, or missing outer endpoints throw. The package marks DataChannel as Labs; check provider-specific endpoint behavior in the checkout before relying on durability or reconnection semantics.

## SignalR

`monica.AddSignalR()` needs a web host and must be completed with `AddSignalR<TIHubOperator,THubOperator,TContract,TUser>()`, which registers SignalR services and satisfies the module's required feature. Map each hub with `MapSignalRHub<THub>("/path")`. `TContract` implements `ISignalRHubContract`, `TUser` implements `ICurrentUser`, and the operator implements `ISignalRHubOperator<TContract,TUser>`. Inject the operator to select users or connections and invoke typed client methods. When the Authentication module is present, mapped hub paths are added to its browser WebSocket query-token allowlist. `SignalRFacade` supplies hub, connection, and send diagnostics; pending send counters report Monica-observed tasks, not private SignalR transport queue depth.

## Source and checks

Composition contracts: `Monica.EventBus/Modules/ModuleEventBus.cs`, `Monica.WebApi/Modules/ModuleRpcClient.cs`, `Monica.Dapr/Modules/ModuleDaprClient.cs`, `Monica.DataChannel/Modules/ModuleDataChannel.cs`, `Monica.SignalR/Modules/ModuleSignalR.cs`. Behavioral evidence: `tests/Test.Monica.EventBus/Modules/ModuleEventBusCompositionTests.cs`, `tests/Test.Monica.WebApi/RpcClient/RemoteCallClientTests.cs`, `Tests/Test.Monica.Dapr/Providers/DaprRemoteExceptionDiagnosticsExtractorTests.cs`, `Monica.DataChannel/README.md`, and `tests/Test.Monica.SignalR/Modules/ModuleSignalRJsonProtocolTests.cs`. Run the touched module's non-UI tests, then the repository's standard build and non-UI test gate for implementation changes. Browser smoke testing is the appropriate check for hub and DataChannel UI behavior; run UI tests only when requested.
