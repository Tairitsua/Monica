# Monica.DataChannel

[![NuGet](https://img.shields.io/nuget/v/Monica.DataChannel.svg)](https://www.nuget.org/packages/Monica.DataChannel/)
[![License](https://img.shields.io/github/license/Tairitsua/Monica.svg)](../LICENSE.txt)

> Maturity: **Labs**. DataChannel is intentionally outside Monica's Stable 1.0 contract and may change as its provider and lifecycle model is refined.

`Monica.DataChannel` composes bidirectional data pipelines from an inner endpoint, an outer endpoint, and optional transform or monitoring middleware. Pipeline declarations and runtime state belong to the current Monica host; two hosts in one process do not share registrations, TCP connections, or channel diagnostics.

## Install

```bash
dotnet add package Monica.DataChannel --prerelease
```

## Minimal setup

Implement `IDataChannelSetup` and add each pipeline through the host-provided registrar. Every pipeline requires an outer endpoint; the inner endpoint defaults to `DefaultChannelEndpoint` when omitted.

```csharp
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.Providers.Kafka;

public sealed class OrderingChannelSetup : IDataChannelSetup
{
    public void Setup(IDataChannelRegistrar channels)
    {
        channels.Add(
            id: "ordering.events",
            configure: pipeline => pipeline
                .SetOuterEndpoint(new KafkaOptions(ConnectionDirection.Output)
                {
                    BootstrapServers = "localhost:9092",
                    Topic = "ordering-events"
                })
                .AddPipeMiddleware<MessageCounterMiddleware>(),
            groupId: "ordering");
    }
}
```

Register the required setup inside the host-bound Monica callback:

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);

builder.AddMonica(monica =>
{
    monica.AddDataChannel(options =>
        {
            options.RecentExceptionToKeep = 20;
            options.InitThreadCount = 4;
        })
        .UseSetup<OrderingChannelSetup>();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

`UseSetup<TSetup>()` is required. Monica invokes the singleton setup once for that host, materializes its pipelines before endpoint mapping, and initializes them through `DataChannelInitializerService` during host startup.

## Send data

Inject `IDataChannelManager` at runtime. The manager can access only channels materialized for its host.

```csharp
using Monica.DataChannel.Abstractions;

public sealed class OrderEventPublisher(IDataChannelManager channels)
{
    public async Task PublishAsync(object message)
    {
        var channel = channels.Fetch("ordering.events")
            ?? throw new InvalidOperationException("The ordering event channel is not registered.");

        await channel.SendDataFromInnerAsync(message);
    }
}
```

## Public composition model

| Surface | Purpose |
|---|---|
| `IDataChannelSetup` | Declares all pipelines required by one host. |
| `IDataChannelRegistrar.Add(...)` | Registers a unique pipeline ID, optional group, endpoints, and middleware during startup. |
| `ChannelPipelineBuilder` | Configures inner/outer endpoints and direct or DI-resolved middleware. |
| `IDataChannelManager` | Fetches one channel, a group, or the current host's full channel snapshot at runtime. |
| `DataChannel` | Sends data from either side and exposes the materialized pipeline. |
| `DataChannelFacade` | Provides result-envelope management operations used by Minimal APIs and the optional UI. |

Registration closes after the setup returns. Duplicate IDs, late registrations, and pipelines without an outer endpoint fail with an explicit exception.

## Providers

The Labs package currently contains:

- Kafka
- ActiveMQ
- TCP client and listener endpoints
- UDP
- the default in-process endpoint

Provider options derive from `CommunicationOptions` and declare a `ConnectionDirection`. External brokers, addresses, credentials, and durability remain deployment responsibilities.

Dapr input/output bindings are an explicit companion package, `Monica.DataChannel.Dapr`. Reference it and select `AddDataChannel().UseDaprBindings().UseSetup<TSetup>()`; the core package has no Dapr SDK dependency. See the [binding registration and namespace migration contract](../skills/monica-infra-messaging/references/data-channels.md#optional-dapr-binding-integration).

## Middleware

Use `AddPipeMiddleware<TMiddleware>()` for middleware resolved from the host's dependency injection container. Use `AddPipeMiddleware(instance)` only when the setup intentionally owns that instance.

Built-in middleware includes transform helpers, special-character filtering, logging/debugging helpers, and `MessageCounterMiddleware`. Information-display middleware exposes a host-local snapshot to the DataChannel operational UI.

## Operational endpoints and UI

When Minimal APIs are enabled, `ModuleDataChannel` can expose channel status, exception history, exception summaries, clearing, and reinitialization endpoints. These are operational controls; protect them with the host's authentication and network policy.

`monica.AddDataChannelUI()` adds the optional Monica UI page. It is part of this Labs package and composes the shared Monica UI shell through module dependencies.

## Lifecycle and boundaries

- Channel declarations, materialized pipelines, TCP state, and diagnostics are host-owned.
- Channels initialize with bounded concurrency (`InitThreadCount`, default `10`).
- Each channel retains a bounded recent exception history (`RecentExceptionToKeep`, default `10`).
- Pipeline endpoints are disposed during graceful host shutdown.
- In-process and provider-local state is not a substitute for durable delivery or distributed coordination.

See [DataChannel.Framework.md](DataChannel.Framework.md) for the component and lifecycle model. The published Chinese module pack lives in `../Monica.Docs/docs/zh-CN/modules/data-channel/`.
