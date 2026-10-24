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

Call `AddMonica(...)` once per builder. Its callback is synchronous. After it returns, Monica prepares the application authority and executes its domain/role registrations before sealing and validating the final graph; `AddMonica` itself completes all registration before `Build()`. On a Web host, call `UseMonica()` before `MapMonica()`, once each on that app, before starting. `UseMonica()` builds Monica middleware around routing and `MapMonica()` maps module endpoints. With `Host.CreateApplicationBuilder`, register through `AddMonica(...)`, then build and start without Web calls. A module that requires Web hosting fails composition there; a Web-capable module can still provide its ordinary services if its optional Web features are left off. Use the `MonicaStartup` overload only when application-startup timing observation is needed.

`Application:AppId` and `Application:DeploymentScope` are canonical scalar startup inputs. Code may supply their fallback values through `ConfigureApplication` when those inputs are absent; conflicting values fail composition. ApplicationModel supplies the current deployment's AppName before other modules validate options. Identity and discovery are frozen before domain/role callbacks, including through retained option references. Those callbacks may add modules and services, but cannot change identity, topology, discovery or source selection. Standalone hosts retain their code-configured identity and ProjectName fallback.

This small host is a composition example, not a requirement to install all three modules everywhere. `AddModuleSystem()` gives a graph and option-diagnostics facade; `AddDependencyInjection()` opts into conventional service registration; `AddHostedService()` adds Monica worker state and heartbeat observation. A module may require another module automatically, so diagnose the resulting graph through `ModuleDiagnosticsFacade` rather than assuming registrations are independent. Configure type discovery for assemblies containing application services, controllers, and other discovered types; a missing assembly can look like a missing module or registration. A unified business host keeps every core and role implementation included in its artifact in discovery, then uses the [application model](application-model.md) to select autonomous entries. Do not filter discovery down to active roles: ordinary Local calls may need included implementations owned by inactive roles.

When a discovered handler uses a closed generic event or request, ProjectUnits derives its structural shape from the generic definition already present in the host's discovery scope. Include the contract assembly as well as the handler assembly; a missing or excluded definition remains a diagnostic. This catalog relationship does not register services or expand other modules' discovery matches. Module authors can reuse the host-owned definition's cached `BusinessTypeShape.MakeGenericType(...)`; see the [type-discovery contract](../../monica-development/references/registration-runtime.md).

`ConfigureModuleSystem(...)` controls host-wide endpoint and diagnostic defaults. `EnableMinimalApiByDefault` is `false`, so a module's optional Minimal API route may be absent while its services work; module options can override it. `MonicaEndpointPort` can restrict Monica-owned endpoints to one local port. By default, setting it also adds an HTTP listener; set `AutoAddMonicaHttpListener = false` when deployment or Kestrel owns the binding. `AddModuleSystem()` exposes bounded option values through `ModuleDiagnosticsFacade`: ordinary values are visible by default, sensitive values are redacted. `ConfigureModuleOptionDiagnostics<TModule,TOptions>` adds host-owned sensitivity rules; `RevealSensitive` is accepted only in Development. The graph uses module `Type` identity; rendered module keys are diagnostic labels, not registration keys.

When composition fails, first inspect the module's required host kind, feature selection, and type-discovery scope. When a service resolves but a route does not, check Web lifecycle and the module's Minimal API option before adding another registration. Source: `Monica.Core/Modularity/Extensions/MonicaHostBuilderExtensions.cs`, `MonicaApplicationBuilderExtensions.cs`, `Monica.Core/MonicaModuleSystemOptions.cs`, and `Monica.Core/Modules/ModuleSystem.cs`; lifecycle checks are in `tests/Test.Monica.Core/Modularity/ModuleCompositionLifecycleTests.cs`. `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` demonstrates the configured host.

## Select startup inputs

Supply ordinary host configuration sources before calling `AddMonica`. Monica captures the startup source snapshot before invoking its callback. A typed `[Configuration("Section", Kind = ConfigurationKind.Startup)]` value read with `monica.GetStartupOptions<T>()` uses that snapshot, validates immediately, and remains fixed until restart. Include `using Monica.Configuration.Bootstrap;` for the reader. Use this for provider selection, connections and module composition; managed settings belong to runtime services. See [configuration input ownership](../../monica-infra-configuration/references/bootstrap-and-stores.md) for the store boundary and [options validation](../../monica-infra-configuration/references/options-and-validation.md) for the typed contract.

Set `Application:EnvironmentFiles` to an ordered list of private JSON paths when selecting the environment that a process connects to. Paths resolve against the host content root, and every selected file must exist. With the standard .NET host configuration, priority from low to high is application JSON, environment-specific application JSON, the selected files, Development user secrets, environment variables, and command-line arguments. Later selected files override earlier ones. The host environment name still controls Development features independently of those connection inputs.

The selected file list comes entirely from its highest-priority contributing provider; a one-file launch override replaces a lower three-file list. Explicit empty or invalid values do not cause fallback. Selected files cannot include another `Application:EnvironmentFiles` list or values owned by a managed definition. Invalid file reloads keep the last accepted file contents. Accepted reloads may update live configuration consumers, while Startup options retain the running value and expose changed candidates in configuration diagnostics. Selecting a different list requires restart. For a custom pipeline without an identifiable appsettings boundary, call `builder.SetMonicaEnvironmentFileInsertionIndex(index)` after supplying ordinary sources and before `AddMonica` (`Monica.Core.Configuration`). The index identifies the source before which files are inserted; the source count appends them. An unidentifiable boundary fails composition instead of guessing. `MonicaApplication.Composition.EnvironmentFileDiagnostics` records accepted revisions and rejected reloads without configuration values.

## Contribute object mappings

`monica.AddObjectMapping()` owns one Mapster configuration per host. It discovers concrete `IRegister` profiles through business-type discovery; use `.AddProfile<TProfile>()` for a profile outside that inventory. Contribute rules through the supplied profile configuration rather than `TypeAdapterConfig.GlobalSettings`. The module compiles an isolated candidate in startup work and publishes it only after successful compilation; mapping services retain the host-owned lazy configuration while compilation runs.

Mapster's current record mapping creates a new record even when a destination was supplied, so use the mapper's returned value. Records select the largest public constructor, and classes without a default constructor can also use automatic constructor mapping. Declare `ConstructUsing` when the application requires a specific construction rule. When upgrading Mapster, verify record results, constructor selection, projections and scoped service resolution in application tests; a successful build alone does not establish mapping compatibility. Keep direct Mapster package overrides aligned with Monica's package metadata. The [mapper UI](../../monica-infra-ui/references/mapper.md) inspects the registered mappings.
