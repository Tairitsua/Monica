# UI Foundation and Application Hosting

| Registration | Responsibility |
| --- | --- |
| `AddUI()` | Themes, browser preferences, MudBlazor services, optional Markdown; no document, router, or navigation |
| `AddUIHost()` | Requires UI foundation; hosts the shared document/router, Interactive Server, static assets and route discovery |
| `AddUIShell()` | Requires UI host; adds default navigation, operational access and Shell state |
| `AddApplicationUI()` from Monica.Application.UI | Requires UI host and application execution; adds trusted circuit identity for generated Local API invocation |

Use a Web SDK host and a Razor presentation library. For a .NET 10 executable whose components all come from referenced Razor class libraries, including Monica's shared document, set `RequiresAspNetWebAssets` in the executable's `.csproj`. This includes the Blazor startup script during publish even when the executable has no `.razor` file. See the [ASP.NET Core host requirement](https://learn.microsoft.com/aspnet/core/blazor/project-structure?view=aspnetcore-10.0#location-of-the-blazor-script).

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Product.UI/Product.UI.csproj" />
  </ItemGroup>
</Project>
```

Here `Product.UI` is the referenced Razor presentation library containing the product components. A host using only Monica diagnostic pages applies the same property to its existing Web executable. Ordinary pages need no `Module{Name}UI` class. For a product UI:

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

Verify the published host includes its `_framework/blazor.web*.js` assets and corresponding entries in `<HostAssembly>.staticwebassets.endpoints.json`. In a deployed browser, check that the script URL rendered by `Assets["_framework/blazor.web.js"]` loads successfully, including its fingerprinted URL when present, and that a UI action works after the Interactive Server circuit connects. Server prerendering can display populated pages while a missing boot script leaves their controls inactive.

For a script 404, empty CSS, or inactive controls, follow [static-asset diagnosis](troubleshooting/static-assets.md). For a page that fails only on direct access or refresh, follow [route diagnosis](troubleshooting/routing-and-refresh.md).

`AddPageAssembly` contributes routes to both the Blazor router and server endpoint mapping; direct refresh works without menu registration. Add `AddUIShell()` or a diagnostic UI module only when operational navigation is wanted. Host-configured `DefaultLayout` applies to product pages; Shell catalog destinations use its layout. Native `@layout` always wins. Catalog page policies are separate from handler authorization.

The default `AppRouter` renders `UIProviders` once above every layout. `MainLayout` no longer owns theme, dialog, popover or snackbar providers. Do not duplicate them in a product layout. `AddUIHost<TRoot>()` supports a host-owned document root; that root owns assets, interactive rendering and its router. If it uses `AppRouter`, providers are already included; otherwise render `UIProviders` once inside the interactive root. Add its page assemblies to the host options. Foundation-only hosts integrate Razor hosting themselves.

`ModuleUIOption` owns `DefaultTheme` (Default), `DefaultDarkMode` (false), and `EnableMarkdown` (false). Stored browser preferences take precedence. `ModuleUIHostOption` owns `EnableDebug` (false), page assemblies, and `DefaultLayout` (body-only without the Shell). Shell options retain navigation and operational-access configuration. Move existing theme/Markdown options from `AddUIShell` to `AddUI`, and detailed-error configuration to `AddUIHost`. Feature modules require `ModuleUI` for Markdown defaults. Route redirects can be configured on `AddUIHost()`; the Shell redirect convenience delegates to the same owner.

Foundation and hosting reuse the existing packaged theme/assets. They do not impose Shell dimensions. Product CSS owns its own geometry. See application invocation in `$monica-application-ui` (`references/application-invocation.md`) for scope, caller, validation, transactions and cancellation.
