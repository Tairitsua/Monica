# UI Foundation and Application Hosting

| Registration | Responsibility |
| --- | --- |
| `AddUI()` | Themes, browser preferences, MudBlazor services, optional Markdown; no document, router, or navigation |
| `AddUIHost()` | Requires UI foundation; hosts the shared document/router, Interactive Server, static assets and route discovery |
| `AddUIShell()` | Requires UI host; adds default navigation, operational access and Shell state |
| `AddApplicationUI()` from Monica.Application.UI | Requires UI host and application execution; adds trusted circuit identity for generated Local API invocation |

Use a Web SDK host and a Razor presentation library. When the executable contains no `.razor` file, set `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` in its `.csproj`; otherwise the Blazor startup script is absent even though server prerendering works. This is an [ASP.NET Core host requirement](https://learn.microsoft.com/aspnet/core/blazor/project-structure?view=aspnetcore-10.0#location-of-the-blazor-script). Ordinary pages need no `Module{Name}UI` class. For a product UI:

```csharp
builder.AddMonica(monica =>
{
    monica.AddApplicationUI();
    monica.AddUI(options => options.DefaultTheme = MonicaThemeKind.Grove);
    monica.AddUIHost(options =>
    {
        options.DefaultLayout = typeof(ProductLayout);
        options.AddPageAssembly(typeof(ProductLayout).Assembly);
    });
    monica.AddLocalization(options => options.AddResource<ProductResource>());
});
```

Import `Monica.Modules`, the modularity extensions, `Monica.UI.Theming`, and the product types. Keep the existing `UseMonica()` / `MapMonica()` lifecycle. Configure the actual authentication, policies, persistence and application type discovery in that host. `AddApplicationUI` is an Interactive Server profile, not a WASM registration.

`AddPageAssembly` contributes routes to both the Blazor router and server endpoint mapping; direct refresh works without menu registration. Add `AddUIShell()` or a diagnostic UI module only when operational navigation is wanted. Host-configured `DefaultLayout` applies to product pages; Shell catalog destinations use its layout. Native `@layout` always wins. Catalog page policies are separate from handler authorization.

The default `AppRouter` renders `UIProviders` once above every layout. `MainLayout` no longer owns theme, dialog, popover or snackbar providers. Do not duplicate them in a product layout. `AddUIHost<TRoot>()` supports a host-owned document root; that root owns assets, interactive rendering and its router. If it uses `AppRouter`, providers are already included; otherwise render `UIProviders` once inside the interactive root. Add its page assemblies to the host options. Foundation-only hosts integrate Razor hosting themselves.

`ModuleUIOption` owns `DefaultTheme` (Default), `DefaultDarkMode` (false), and `EnableMarkdown` (false). Stored browser preferences take precedence. `ModuleUIHostOption` owns `EnableDebug` (false), page assemblies, and `DefaultLayout` (body-only without the Shell). Shell options retain navigation and operational-access configuration. Move existing theme/Markdown options from `AddUIShell` to `AddUI`, and detailed-error configuration to `AddUIHost`. Feature modules require `ModuleUI` for Markdown defaults. Route redirects can be configured on `AddUIHost()`; the Shell redirect convenience delegates to the same owner.

Foundation and hosting reuse the existing packaged theme/assets. They do not impose Shell dimensions. Product CSS owns its own geometry. See [application invocation](../../monica-application-ui/references/application-invocation.md) for scope, caller, validation, transactions and cancellation.
