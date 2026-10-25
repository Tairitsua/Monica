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

## Swagger descriptions

Register `AddSwagger()` and chain `AddSignalRSwagger(options => options.ScanAssembly(typeof(UpdatesHub).Assembly))` on the SignalR registration to describe hubs annotated with the package's `[SignalRHub]`. The package scans its configured assemblies and creates synthetic method paths; these descriptions do not map endpoints. With `AddApplicationModel()`, concrete `Hub` subclasses follow the [native Swagger document rules](../../monica-infra-web/references/api-documentation-and-visibility.md). The concrete hub assembly owns inherited methods, and a disabled role's scanned hub contributes no operations. Manual execution policy does not disable the selected role's endpoints.

Recognized package tags become native `ApiDescription.GroupName` overrides, and `DocumentNameResolver` retains its normal priority. A public `[SignalRHub(documentNames: ...)]` selector overrides default ownership routing through an explicit group for the selected document; the final host inclusion predicate can still exclude the operation. A null selector uses native ownership, while an explicit empty array preserves the package's all-documents override; neither permits a disabled hub. Package-wide `DisplayInDocument` or `DisplayInDocuments` only narrows candidate documents and intersects native routing. Host-owned documents configured directly through `SwaggerDoc(...)` keep their existing custom predicate behavior.

Standalone hosts preserve package document selection. Package-only annotated interfaces and non-hub types also retain package selection: they describe contracts without identifying a concrete mapped hub owner. The package's path uniqueness constraint remains: scanned descriptions must generate distinct paths within each candidate document, because it inserts paths before operation filters run. Limit scanned assemblies and use distinct hub paths; role filtering is not a duplicate-path repair mechanism.

## Browser debug workbench

Add `monica.AddSignalRUI()` in the same registration callback to expose `/signalr-debug` through the Monica shell. The UI requires the SignalR module, localization, and shell UI; it does not replace the typed SignalR registration or hub mapping. `ModuleSignalRUIOption.DefaultAccessToken` defaults to null. `EnableTestUserConnection` also defaults to null, allowing test-user token generation only in Development and when an `IAccessTokenIssuer` is available. An explicit true or false overrides the environment check, but the issuer is still required. Generated tokens are valid JWTs for random users; enable this capability only on trusted debugging hosts.

The workbench owns one browser connection for one rendered page. Its Methods, Messages, Connections, and Sends task tabs keep their panels alive and share that session. Changing tabs preserves listeners, inputs, and trace state. `SignalRDebugPageStateFactory.Create()` creates a fresh state and JavaScript session for the page; do not replace this with a scoped connection reused across routes. Leaving the page retires its session.

The route field is a draft for the next connection. `SelectedHubRoute` holds that draft, while `ConnectedHubRoute` identifies the established or pending connection target. `MethodCatalogHubRoute` uses the active target during connecting, connected, reconnecting, and disconnecting states, then follows the draft when disconnected. Editing the draft cannot relabel or retarget a live connection. Method catalogs stay separate by mapped route, so identically named methods on different hubs do not collide. Metadata refresh preserves each route's listener flags and receipt counts; changing catalog routes clears the selected method and its parameter bag.

Enable listeners before expecting browser receipt evidence. Explicit disconnect or a new connection detaches the old JavaScript listeners and clears their active flags; retained per-method counts remain historical. Automatic reconnection of the existing SignalR connection keeps its handlers. Connection generations reject queued status, identifier, receipt, and listener-registration acknowledgements from an explicitly replaced connection, including a replacement to the same route. Receipts use the exact method name and originating route rather than mutable display text.

Invocation validation and execution share `SignalRInvocationArgumentParser`. Empty fields retain its documented CLR defaults; they are not required-field errors. Hub metadata reflects public instance methods declared on each mapped hub, with parameter type names; it is not a complete schema or discovery of the typed client contract. The quick message action requires the hub method `ReceiveTestMessage(userName, message)`.

Page retirement closes operation admission, unsubscribes state notifications, retires the JavaScript session, and drains admitted operations and queued renders. Initialization, invocation, and disposal share the existing session gate, including imports that finish after retirement begins. JavaScript first stops callback producers, then drains tracked callbacks before acknowledging shutdown. The .NET callback reference is released only after a confirmed drain or circuit disconnection. A bounded wait for that acknowledgement can finish earlier while a continuation retains callback ownership; the bound does not apply to an unfinished import or invocation waiting ahead of disposal at the gate.

## Read diagnostic evidence

Hub metadata, tracked connections, and server sends are independent captures. Their initial load and manual refresh expose the following states; connected-user captures also refresh after stable browser connection transitions.

| Capture state | Meaning |
| --- | --- |
| Not loaded | No successful capture exists; an empty list alone is not evidence. |
| Loading | A capture is in progress; the previous successful evidence can remain visible. |
| Successful and empty | The source returned an empty capture successfully. |
| Failed | The latest attempt failed; the last successful evidence and its UTC capture time remain available. |
| Send diagnostics disabled | A successful snapshot reports `IsEnabled == false`; this is distinct from failure or no capture. |

Hub and connection state record UTC timestamps of the last success. Server sends use `SignalRSendDiagnosticsSnapshot.SnapshotTimeUtc`. These times describe captures, not continuous monitoring. The Connected Users list contains connection records from the default process-local registry, not distinct users or every connection in a deployment. `SignalRHub<TContract>` registers authenticated connections; mapped hubs using other base classes, anonymous connections, and connections on other hosts can be absent. The browser's absence from the latest successful registry capture means only that it was not observed there. It does not establish its authentication status, and failed or loading captures cannot support that warning.

`ModuleSignalROption.EnableSendMetrics` is off by default. Enabling it measures server-to-client send tasks observed through Monica's operator wrappers. Pending counts describe those in-flight tasks; completed and failed counts describe their outcomes since process start. A completed server send is not proof that this browser received or processed a callback. The browser trace and listener counts provide separate evidence for enabled listeners in this page's session; the trace retains at most 1,000 entries.

Send summaries group by hub and client method. `TargetMetricCount` counts diagnostic rows, and `TargetCount` counts supplied target or exclusion identifiers, not online recipients; an `All` target has zero explicit identifiers. `IncludeSendDiagnosticTargetIdentifiers` defaults to false and should be enabled only where connection, group, and user identifiers may be retained. Retained display names are resolved from the connection registry when target details are captured and may be empty. These captures do not expose SignalR's private transport queue or recipient acknowledgements.

If a browser cannot connect, check the mapped route and host endpoint lifecycle first, then authentication: when Monica's Authentication module is present, mapped hub routes are added to its WebSocket query-token allowlist. Use `SignalRFacade` for mapped hubs, tracked connections, and observed sends; keep the scope of each capture visible when interpreting it.

## Source and behavioral checks

`Monica.SignalR/Modules/ModuleSignalR.cs` owns the required feature, typed registration, and hub mapping; `Monica.SignalR/Abstractions/SignalRHub.cs` and `SignalRHubOperator.cs` define the example's base types. `tests/Test.Monica.SignalR/Modules/ModuleSignalRJsonProtocolTests.cs` composes the four-type registration with the operator interface as `TIHubOperator`.

`Services/Support/SignalRSwaggerDocumentRoutingFilter.cs` supplies concrete-hub metadata to native Swagger routing. `tests/Test.Monica.SignalR/Modules/SignalRSwaggerDocumentTests.cs` uses the real Swagger provider to cover role isolation, inherited hub methods, inactive and unavailable roles, explicit selectors and groups, host exclusions, package candidate selection, and standalone/interface-only behavior.

`Monica.SignalR/Modules/ModuleSignalRUI.cs`, `UISignalR/State/SignalRDebugPageStateFactory.cs`, `UISignalR/State/SignalRDebugPageState.cs`, `UISignalR/Support/SignalRDebugJsClient.cs`, and `wwwroot/UISignalR/signalr-debug.js` own workbench registration, captures, route catalogs, generations, and session retirement. `tests/Test.Monica.SignalR/UISignalR/Support/SignalRDebugSessionTests.cs` protects route separation, listener reconciliation, stale generations, retained captures, registry absence, and late imports. `Monica.SignalR/tests/signalr-debug-session.test.mjs` protects browser callback production and shutdown drainage. Send task and target scope are defined by `Monica.SignalR/Models/SignalRSendDiagnosticsSnapshot.cs` and `Monica.SignalR/Metrics/`.
