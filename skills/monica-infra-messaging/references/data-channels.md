# DataChannel

Use DataChannel for a named pipeline whose two ends and middleware are owned by one running host. The Web host must call `app.UseMonica()` and `app.MapMonica()` after building the app.

`monica.AddDataChannel().UseSetup<TSetup>()` needs an ASP.NET Core web host and one or more singleton `IDataChannelSetup` contributors. Chain `UseSetup<TSetup>()` calls to compose pipelines from several selected integrations. Each distinct setup type runs once, in registration order, during `app.UseMonica()`; registering the same type again has no additional effect. All setups finish before any pipeline is materialized or initialized. A host selecting role-owned connectors contributes only those roles' setup types through this same API; no combined wrapper setup is required.

Each setup calls `IDataChannelRegistrar.Add` once per pipeline. Pipeline IDs must be unique across all setups in the host; duplicates fail during setup before materialization. Every pipeline needs an outer endpoint; the inner endpoint defaults to `DefaultChannelEndpoint`. Registration closes after startup materialization. Runtime callers inject `IDataChannelManager`, fetch a host-owned channel, and call its send method.

```csharp
public sealed class OrderChannelSetup : IDataChannelSetup
{
    public void Setup(IDataChannelRegistrar channels) => channels.Add(
        "orders", pipeline => pipeline.SetOuterEndpoint(new KafkaOptions(ConnectionDirection.Output)
        {
            BootstrapServers = "localhost:9092",
            Topic = "orders",
            SecurityProtocol = SecurityProtocol.Plaintext
        }));
}

builder.AddMonica(monica => monica.AddDataChannel().UseSetup<OrderChannelSetup>());

var channel = channels.Fetch("orders")
    ?? throw new InvalidOperationException("orders channel is not registered");
await channel.SendDataFromInnerAsync(message);
```

The example needs `Monica.DataChannel.Providers.Kafka`, `Monica.DataChannel.Abstractions.Communication`, and `Confluent.Kafka`. The broker address and plaintext transport are illustrative for a local broker: `KafkaOptions` otherwise defaults to SASL plaintext with SCRAM-SHA-512, which requires credentials and broker support. `SendDataFromInnerAsync` enters the pipeline at its inner end and reaches the configured outer endpoint; `SendDataFromOuterAsync` travels the opposite direction. Transform middleware runs in the same configured order in both directions. Endpoint configuration middleware belongs to a separate stage outside that message transform chain. If no inner endpoint is set, it defaults to `DefaultChannelEndpoint`. Add DI-resolved middleware with `AddPipeMiddleware<T>()`, or use `groupId` on `IDataChannelRegistrar.Add(...)` and `IDataChannelManager.FetchGroup(...)` to organize related pipelines. After startup, inject `IDataChannelManager` and fetch an existing channel; the setup registrar is no longer mutable.

Duplicate IDs, late registration, or missing outer endpoints throw during setup/materialization. If sending fails, first check the channel ID and endpoint direction, then the provider's connectivity and the DataChannel exception diagnostics for that host. The package marks DataChannel as Labs; inspect the selected provider before promising durability or reconnection behavior.

## Initialization and management evidence

`DataChannel.ReInitialize(cancellationToken)` runs the real communication endpoint initialization. Only one attempt can be active per pipeline; concurrent calls throw `InvalidOperationException` rather than reporting early success. An admitted attempt clears `IsInitialized` while `IsInitializing` is true. Success retires the attempt as initialized and clears `IsNotAvailable`; failure or cancellation retires it as uninitialized and unavailable. Caller cancellation propagates after rollback stops the outer endpoint before releasing the inner endpoint, without reusing the cancelled initialization token. Cleanup failures are recorded while the original initialization failure remains the caller's result. A failed transient endpoint initialization can be retried, because the proxy marks initialization complete only after success and resets its disposal latch after recovery.

Endpoints remain owned by the host. Initialization and host shutdown share one lifecycle boundary: shutdown waits for the admitted attempt, stops ingress at the outer endpoint, then releases the inner endpoint even if another endpoint fails to stop. Repeated shutdown calls observe the same cleanup completion and its original aggregated endpoint failures; they do not repeat endpoint teardown or hide a failed shutdown. Shutdown retires the pipeline permanently; subsequent initialization throws `ObjectDisposedException`. Failed initialization rollback is retryable and does not retire the pipeline. TCP, UDP, and serial endpoints close their previous resources before reinitializing and await their owned receivers during shutdown. An initialized pipeline is not a claim of remote transport health or successful message delivery.

Add the companion UI with `monica.AddDataChannelUI()` alongside the configured `AddDataChannel().UseSetup<TSetup>()` registration. Its directory is `/datachannel-manage`; `/datachannel-manage/exceptions` focuses on retained exception evidence. Host/UI operations use `DataChannelFacade` directly. The DataChannel module requires the result-envelope module for its Minimal API responses, so the smallest configured DataChannel host needs no unrelated module to execute `GetResponse()`.

```csharp
var result = await facade.GetManagementSnapshotAsync(cancellationToken);
if (result.IsFailed(out var error, out var capture))
{
    // Preserve the previous capture and present the refresh failure.
    return;
}

var channelEvidence = capture!.Channels.Single(channel => channel.Id == "orders");
var exceptionEvidence = capture.ExceptionSummary.ChannelSummaries.Single(channel => channel.ChannelId == "orders");
```

`facade` is a scoped, DI-resolved `DataChannelFacade`. Directory and exception summary share one tracker capture per channel, with UTC capture times and exact group IDs. Tracker counters and retained entries are consistent within that read; different channels, component metadata and externally mutable values are independent reads. A failed management read does not return partial success. Existing status, exception-detail and exception-summary methods also return detached evidence and use the lifetime exception counter consistently.

`ModuleDataChannelOption.RecentExceptionToKeep` defaults to 10 and bounds each channel's **shared observable history**, including ordinary records and exceptions. Current exception counts describe the retained subset; lifetime totals count exception occurrences and survive both trimming and clearing. `GetChannelExceptionsAsync(id, count)` separates the requested recent detail subset from the complete retained count. Its source fields are empty when provenance was not recorded; an exception's CLR type does not identify the failing component. Complete exception messages and stack evidence remain available, including a capture error when a custom exception cannot render its evidence.

The native exception inspector keeps record selection and its Evidence / Stack trace tabs. Stack text uses the shared [diagnostic-text viewer](../../monica-infra-ui/references/stack-trace.md), with complete encoded source, wrapping and exact-payload Copy; the inspector owns vertical scrolling. This presentation does not expand the retained subset, invent component provenance, or change the captured counters.

`ClearChannelExceptionsAsync(id)` calls the tracker's actual `ClearHistory()`: it deletes all retained observable records for that channel, including ordinary records, while preserving lifetime counters and endpoint state. It does not acknowledge, resolve or recover a channel failure.

## Component identity and debugger ownership

`PipelineComponentInfo` freezes component metadata and information values into `DataChannelValueSnapshot(TypeName, Json, CaptureError)`. JSON is complete, readable Unicode evidence with enum names and preserved nulls; getters or serializers that fail produce local capture-error evidence. No component, configuration object or nested mutable value is retained by the capture. Normalized CLR display names and explicit assembly-qualified identities describe the declared concrete type, including for transient proxies.

Use `Stage` and zero-based `Ordinal` to present actual pipeline membership and processing order. Component classification alone does not identify its stage. Use its opaque `ComponentKey` with `GetMiddlewareByKeyAsync<T>(channelId, componentKey)` for a real host-owned action. Repeated or same-short-name components have distinct keys; `GetMiddlewareAsync<T>(channelId, name)` rejects ambiguous names. Replacing or removing a component retires its old slot key, and subsequent captures issue keys for current components. Callers own synchronization when mutating public endpoint references or middleware lists. Action resolution never invents a persistent instance for transient components. `CanDebugMessages` includes derived persistent `MessageDebuggerMiddleware` registrations; transient debugger-shaped components have no persistent host queue to inspect.

The debugger's Start/Stop, keyword filter, queue capacity and Clear controls operate on its real host-owned capture. `GetDebugMessageSnapshots()` returns immutable payload and metadata evidence frozen at middleware capture time, in queue order, with a monotonic sequence that survives Clear. A selected capture remains valid after trimming or clearing. The legacy `GetDebugMessages()` view exposes live raw/nested values and is not frozen or export-safe evidence. Closing the UI inspector retires its polling, while host capture keeps its existing state; another inspector must not stop that shared capture accidentally. The debugger observes messages before target endpoint dispatch, so a captured payload does not prove downstream receipt.

## Raw transport pipelines

TCP, UDP, and serial providers accept `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, or a UTF-8 `string`. They preserve byte payloads and reject other payload types. Their input is a raw stream chunk or UDP datagram: framing, circuit routing, persistence, retries, and protocol acknowledgements belong to the application. A successful send means the local transport write completed; it does not prove that the remote application received the message. Failed or canceled writes may have transmitted some bytes, so retry policy must account for duplicates. Disconnected TCP sends and TCP server sends with no matching client fail instead of reporting success. The framework never recursively retries an application send.

| Provider options | Required configuration and behavior |
| --- | --- |
| `TcpClientOptions` | Set `IsClient = true`, a unique `ClientAddress.Key`, and `ConnectedExtend.Address` containing host and port. `IsMainConnected = true` enables input from a primary connection. Each client reconnects its own configured peer with a two-second delay; one circuit disconnecting does not change another circuit's receive role. The application handles durable delivery retry. |
| `TcpServerOptions` | Set `IsServer = true` and `ServerAddress` containing a unique listener name plus local IP address and port. `LocalEndpoint` exposes the bound address, including an ephemeral port. A send targets only this listener's accepted connections; optional `ConnectionName` context metadata narrows it to one connection. Existing primary/standby selection groups accepted peers by network prefix. |
| `UdpOptions` | `Address` and `Port` are the local IP bind address and port. Output directions additionally require `RemoteAddress` and `RemotePort`. `LocalEndpoint` exposes the bound address. One pipeline owns each listening socket; dispatch multiple logical circuits after receipt. |
| `SerialPortOptions` | `PortName` selects hardware. Defaults are 9600 baud, 8 data bits, no parity, one stop bit, no handshake, and DTR/RTS disabled. Set hardware properties explicitly for the device. A port failure remains observable until reinitialization. |

Pass cancellation with `await channel.SendDataFromInnerAsync(bytes, cancellationToken)`. The token supplied at pipeline entry owns the entire delivery: transform middleware can replace the context, but cannot replace its delivery cancellation. The pipeline restores the original token on every transformed context and checks it between transforms and before endpoint delivery. Custom inner endpoints override `Task ReceiveDataAsync(ChannelDataContext data)` and pass `data.CancellationToken` to their asynchronous work. Do not use `async void ReceiveData`: that returns before delivery completes and loses pipeline failure propagation. Raw transport receivers await each pipeline callback, making failures observable before handling the next chunk. Shutdown stops the outer receiver before disposing the inner endpoint, so awaited inner work can finish through its canceled receive token. Implement `ITransientChannelComponent` on a stateless inner endpoint when its collaborators need a per-delivery DI scope; keep stream-framing state in a host-owned service keyed by connection. TCP input metadata includes a `ConnectionEpoch` GUID that changes with every socket connection; reset any incomplete frame when it changes so a new stream cannot complete a previous session's partial frame.

Serial setup uses `Monica.DataChannel.Providers.Serial`:

```csharp
channels.Add("messages", pipeline => pipeline
    .SetOuterEndpoint(new SerialPortOptions { PortName = "COM1", BaudRate = 9600 })
    .SetInnerEndpoint<MessageReceiveEndpoint>());
```

These transports do not provide a durable queue or a receipt protocol.

Source and behavioral checks: `Monica.DataChannel/Modules/ModuleDataChannel.cs`, `Monica.DataChannel/Abstractions/IDataChannelSetup.cs`, `IDataChannelRegistrar.cs` and `IDataChannelManager.cs`, `Monica.DataChannel/DataChannel.cs`, `Monica.DataChannel/Pipeline/ChannelPipeline.cs`, `Monica.DataChannel/Pipeline/TransientProxy.cs`, `Monica.DataChannel/Facades/DataChannelFacade.cs`, `Monica.DataChannel/Middlewares/MessageDebuggerMiddleware.cs`, `Monica.DataChannel/Providers/Kafka/KafkaOptions.cs`, the `Providers/TCP`, `Providers/UDP`, and `Providers/Serial` implementations, and the real-host scenarios under `tests/Test.Monica.DataChannel/`, including `Modules/ModuleDataChannelTests.cs`, `Pipeline/ChannelPipelineTests.cs` and `Providers/TransportEndpointTests.cs`.
