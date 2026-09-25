# SignalR

Use SignalR when the application sends typed methods to currently connected Web clients. Registration, hub mapping, and the host endpoint lifecycle are all required; the module alone does not create a callable hub.

`monica.AddSignalR()` needs a web host and must be completed with `AddSignalR<TIHubOperator,THubOperator,TContract,TUser>()`, which registers SignalR services and satisfies the module's required feature. Map each hub with `MapSignalRHub<THub>("/path")`. `TContract` implements `ISignalRHubContract`, `TUser` implements `ICurrentUser`, and the operator implements `ISignalRHubOperator<TContract,TUser>`. Inject the operator to select users or connections and invoke typed client methods. When the Authentication module is present, mapped hub paths are added to its browser WebSocket query-token allowlist. `SignalRFacade` supplies hub, connection, and send diagnostics; pending send counters report Monica-observed tasks, not private SignalR transport queue depth.

Given application types `IUpdatesClient : ISignalRHubContract`, `UpdatesHub : SignalRHub<IUpdatesClient>`, and `UpdatesHubOperator : CurrentUserSignalRHubOperator<IUpdatesClient, UpdatesHub>`, register and map the typed hub inside the host's existing `builder.AddMonica(...)` callback:

```csharp
monica.AddSignalR()
    .AddSignalR<
        ISignalRHubOperator<IUpdatesClient, ICurrentUser>,
        UpdatesHubOperator,
        IUpdatesClient,
        ICurrentUser>()
    .MapSignalRHub<UpdatesHub>("/hubs/updates");
```

The application supplies the hub and operator constructors required by those base classes. After `builder.Build()`, the Web host calls `app.UseMonica()` then `app.MapMonica()` before starting; Monica maps the registered hub during that endpoint lifecycle. See [host composition](../../monica-infra-hosting/references/host-composition.md). Resolve `ISignalRHubOperator<IUpdatesClient, ICurrentUser>` through DI, then use its `Clients`, `User(...)`, `Users(...)`, or `Groups` surface to target a current connection. User targeting requires a stable `ICurrentUser.Id`; a user who disconnects is no longer a delivery target.

`ModuleSignalROption.EnableSendMetrics` is off by default. Enabling it measures send tasks observed through Monica's operator wrappers; it does not reveal SignalR's private transport queue. `IncludeSendDiagnosticTargetIdentifiers` also defaults to false and should be enabled only where connection, group, and user identifiers may be retained. `SignalRFacade` reports mapped hubs, connected users, and observed sends. If a browser cannot connect, check the mapped route and host endpoint lifecycle first, then authentication: when Monica's Authentication module is present, mapped hub routes are added to its WebSocket query-token allowlist.

Source and checks: `Monica.SignalR/Modules/ModuleSignalR.cs` owns the required feature, typed registration, and hub mapping; `Monica.SignalR/Abstractions/SignalRHub.cs` and `SignalRHubOperator.cs` define the example's base types. `tests/Test.Monica.SignalR/Modules/ModuleSignalRJsonProtocolTests.cs` composes the four-type registration with the operator interface as `TIHubOperator`.
