# DataChannel

Use DataChannel for a named pipeline whose two ends and middleware are owned by one running host. The Web host must call `app.UseMonica()` and `app.MapMonica()` after building the app.

`monica.AddDataChannel().UseSetup<TSetup>()` needs an ASP.NET Core web host and a singleton `IDataChannelSetup`. The setup calls `IDataChannelRegistrar.Add` once per pipeline. Every pipeline has a unique ID and an outer endpoint; the inner endpoint defaults to `DefaultChannelEndpoint`. Registration closes after startup materialization. Runtime callers inject `IDataChannelManager`, fetch a host-owned channel, and call its send method.

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

The example needs `Monica.DataChannel.Providers.Kafka`, `Monica.DataChannel.Abstractions.Communication`, and `Confluent.Kafka`. The broker address and plaintext transport are illustrative for a local broker: `KafkaOptions` otherwise defaults to SASL plaintext with SCRAM-SHA-512, which requires credentials and broker support. `SendDataFromInnerAsync` enters the pipeline at its inner end and reaches the configured outer endpoint; `SendDataFromOuterAsync` travels the opposite direction. If no inner endpoint is set, it defaults to `DefaultChannelEndpoint`. Add DI-resolved middleware with `AddPipeMiddleware<T>()`, or use `groupId` on `IDataChannelRegistrar.Add(...)` and `IDataChannelManager.FetchGroup(...)` to organize related pipelines. After startup, inject `IDataChannelManager` and fetch an existing channel; the setup registrar is no longer mutable.

Duplicate IDs, late registration, or missing outer endpoints throw during setup/materialization. If sending fails, first check the channel ID and endpoint direction, then the provider's connectivity and the DataChannel exception diagnostics for that host. `ModuleDataChannelOption.RecentExceptionToKeep` defaults to 10 per channel. The package marks DataChannel as Labs; inspect the selected provider before promising durability or reconnection behavior.

Source and checks: `Monica.DataChannel/Modules/ModuleDataChannel.cs`, `Monica.DataChannel/Abstractions/IDataChannelRegistrar.cs` and `IDataChannelManager.cs`, `Monica.DataChannel/DataChannel.cs`, `Monica.DataChannel/Providers/Kafka/KafkaOptions.cs`, and `Monica.DataChannel/README.md`.
