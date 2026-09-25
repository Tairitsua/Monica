# Compose a Monica Web API

The Web layer turns a composed module graph into middleware and endpoints. `AddWebApi()` is the standard bundle for MVC/AutoControllers, AutoModel, conventional DI, Swagger, JWT authentication, mediator, object mapping, repository, and exception handling. It does not itself add ProjectUnits, an EventBus transport, permission-bit authorization, or CORS. Choose individual modules when the bundle brings infrastructure the host does not need.

## Compose one Web host

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
{
    monica.ConfigureTypeDiscovery(options =>
        options.Add("Domains.Ordering", "Platform.Protocol"));
    monica.AddWebApi();
    monica.AddAuthentication(options =>
    {
        options.Secret = builder.Configuration["Auth:Secret"]
            ?? throw new InvalidOperationException("Auth:Secret is required.");
        options.Issuer = "my-app";
        options.Audience = "my-app";
    });
    monica.AddEventBus().UseNoOpDistributedEventBus(); // Local example only.
    monica.AddProjectUnits();
});
var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

`AddProjectUnits()` is separate from the bundle and requires EventBus; the no-op distributed transport above is appropriate only for a local example. Choose a real provider when events must cross processes. Include domain and protocol assemblies in type discovery so controllers, application services, and ProjectUnits are present in the host catalog. The application owns the request contracts; [endpoints and execution](endpoints-and-execution.md) explains how request metadata, generated controllers, and the typed execution pipeline fit together.

The Web host calls `UseMonica()` then `MapMonica()` on the built app as shown; [host composition](../../monica-infra-hosting/references/host-composition.md) owns the graph lifecycle and generic-host rules.

The reference application's `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` demonstrates the bundle with explicit ProjectUnits, EventBus, type discovery, and Swagger options. Bundle membership is in `Monica.WebApi/Modules/ModuleWebApi.cs`; ProjectUnits requirements are in `Monica.ProjectUnits/Modules/ModuleProjectUnits.cs`; Web lifecycle is in `Monica.Core/Modularity/Extensions/MonicaApplicationBuilderExtensions.cs`.
