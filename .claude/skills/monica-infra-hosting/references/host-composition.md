# Compose a Monica host

Monica composes one host-owned module graph before the .NET host is built. A module registration declares dependencies and features, so select a public `monica.Add...()` extension for each capability and let composition validate its graph. Web endpoints belong to a Web host; generic hosts keep non-Web services without endpoint mapping.

## Start with the host boundary

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options => options.AppName = "My application");
    monica.AddModuleSystem();
    monica.AddDependencyInjection();
    monica.AddHostedService();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

Call `AddMonica(...)` once per builder. Its callback is synchronous; registrations are sealed and the graph is validated when it returns, before `Build()`. On a Web host, call `UseMonica()` before `MapMonica()`, once each on that app, before starting. `UseMonica()` builds Monica middleware around routing and `MapMonica()` maps module endpoints. With `Host.CreateApplicationBuilder`, register through `AddMonica(...)`, then build and start without Web calls. A module that requires Web hosting fails composition there; a Web-capable module can still provide its ordinary services if its optional Web features are left off. Use the `MonicaStartup` overload only when application-startup timing observation is needed.

This small host is a composition example, not a requirement to install all three modules everywhere. `AddModuleSystem()` gives a graph and option-diagnostics facade; `AddDependencyInjection()` opts into conventional service registration; `AddHostedService()` adds Monica worker state and heartbeat observation. A module may require another module automatically, so diagnose the resulting graph through `ModuleDiagnosticsFacade` rather than assuming registrations are independent. Configure type discovery for assemblies containing application services, controllers, and other discovered types; a missing assembly can look like a missing module or registration. The reference application uses `ConfigureTypeDiscovery(options => options.Add("Domains.Ordering", "Platform.Protocol"))` for its application and protocol assemblies.

`ConfigureModuleSystem(...)` controls host-wide endpoint and diagnostic defaults. `EnableMinimalApiByDefault` is `false`, so a module's optional Minimal API route may be absent while its services work; module options can override it. `MonicaEndpointPort` can restrict Monica-owned endpoints to one local port. By default, setting it also adds an HTTP listener; set `AutoAddMonicaHttpListener = false` when deployment or Kestrel owns the binding. `AddModuleSystem()` exposes bounded option values through `ModuleDiagnosticsFacade`: ordinary values are visible by default, sensitive values are redacted. `ConfigureModuleOptionDiagnostics<TModule,TOptions>` adds host-owned sensitivity rules; `RevealSensitive` is accepted only in Development. The graph uses module `Type` identity; rendered module keys are diagnostic labels, not registration keys.

When composition fails, first inspect the module's required host kind, feature selection, and type-discovery scope. When a service resolves but a route does not, check Web lifecycle and the module's Minimal API option before adding another registration. Source: `Monica.Core/Modularity/Extensions/MonicaHostBuilderExtensions.cs`, `MonicaApplicationBuilderExtensions.cs`, `Monica.Core/MonicaModuleSystemOptions.cs`, and `Monica.Core/Modules/ModuleSystem.cs`; lifecycle checks are in `tests/Test.Monica.Core/Modularity/ModuleCompositionLifecycleTests.cs`. `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` demonstrates the configured host.
