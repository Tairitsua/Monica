# Compose the shell and protect feature pages

Use a Web host. Register selected feature UI modules in `AddMonica(...)`, then complete the same application's lifecycle with `UseMonica()` and `MapMonica()` so the shell can map static assets, antiforgery, Razor components, feature endpoints, and additional page assemblies. `AddUIShell()` installs interactive server services, MudBlazor, browser storage, theme state, and the startup page catalog; feature UI modules contribute their pages.

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
{
    monica.AddUIShell(options =>
    {
        options.OperationalPageAccess.AuthorizationPolicy = "OperationsViewer";
    });
    monica.AddModuleSystemUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

The host must register the named ASP.NET Core `OperationsViewer` policy and the authentication/authorization services it needs. Many feature UI modules, including `AddModuleSystemUI()`, declare their own shell dependency, so explicit `AddUIShell()` is needed here to set a host-wide option, not merely to satisfy that dependency. Select the underlying operational capability as well as its UI module; inspect each feature module's `Describe(...)` to avoid duplicating its transitive registrations. Built-in feature pages include module and system information, logging, DI, health, jobs, AI chat, knowledge, and RAG.

Operational pages are accessible by default in Development. Outside Development, a missing effective `OperationalPageAccess.AuthorizationPolicy` denies access unless an individual page supplies a policy override. `OperationalPageAccess.DebugMode` bypasses access checks in every environment; use it only in an explicitly trusted diagnostic host. `EnableDebug` is separate: it controls detailed Blazor/SignalR errors and defaults to true in DEBUG builds and false otherwise. Feature pages must evaluate their access policy before protected work; hiding a navigation link alone is not authorization.

UI modules contribute routes and navigation during `Describe(...)` through `ModuleShellUIOption.ConfigureNavigation(...)`. `INavigationRegistryBuilder.RegisterPage<TPage>` or `RegisterLocalizedPage<TPage,TResource>` records a route, optional stable category ID, label, icon, and access-policy type. Register a custom category before using it for `addToNav: true`; localized pages also need the module's resource marker registered with Localization. Contributions are startup-only and the registry seals during endpoint configuration. Duplicate routes, unknown navigation categories, or a late contribution fail rather than silently replacing a page. A route that works through in-app navigation but 404s on refresh points first to missing `MapMonica()` or an unregistered page assembly.

`ModuleShellUIOption.AppName`, `AppId`, and `AppVersion` override the host's application identity in the shell. Navigation search appears only when `NavBarSearchAction` is configured; `EnableNavBarSearch` defaults to true but does not create a search action. `MaxVisibleCategories` defaults to 10, with excess categories under More. Theme and language defaults are in [theme and localization](theme-and-localization.md).

Source and checks: `Monica.UI/Modules/ModuleShellUI.cs`, `OperationalPageAccessOption.cs`, `Monica.UI/Shell/Support/INavigationRegistryBuilder.cs`, `OperationalPageAccessPolicy.cs`, `tests/Test.Monica.UI/Shell/Support/PageRegistryTests.cs`, and `OperationalPageAccessEvaluatorTests.cs`.
