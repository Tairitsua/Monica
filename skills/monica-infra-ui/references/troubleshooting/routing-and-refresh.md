# Diagnose UI route and refresh 404s

Use this reference when a Blazor page opens through in-app navigation but its URL returns 404 in a new tab or after refresh. A 404 for the startup script or a stylesheet belongs to [static assets](static-assets.md).

## Separate document routing from navigation

Capture the exact page URL, response status, redirects, and the process serving it. Check that the component has the expected `@page` route. Compare a direct request to that host with the externally served URL when a proxy or application path prefix is involved. Authentication redirects and denied access require the [shell and access](../shell-and-access.md) contract rather than a new route alias.

The shared `AppRouter` and the server's Razor component endpoints need the same page assemblies. A custom Router that knows a library only through its own `AdditionalAssemblies` can navigate to pages that the server has never mapped. Register each product page assembly through `AddUIHost(options => options.AddPageAssembly(...))` in the existing Monica callback; follow [application hosting](../application-hosting.md) for the complete registration. This feeds both routing paths and does not require a menu item or a business UI module.

`AddUI()` supplies foundation services. `AddUIHost()` owns the document, router, route discovery, and server mapping; `AddUIShell()` adds operational navigation on top. Menu visibility is independent of route existence. A navigation link alone does not declare an HTTP endpoint.

## Check the serving host and custom root

Confirm that the actual Web host completes `AddMonica(...)`, `UseMonica()`, and `MapMonica()` on the same application. Monica rejects an incomplete or incorrectly ordered Web lifecycle at startup, so resolve any startup failure before diagnosing a running page. Check the listening process when an older host appears to keep serving content.

For `AddUIHost<TRoot>()`, the root assembly is included automatically; additional page libraries still belong in the host's page-assembly selection. The custom root owns its router and must use the same assembly set. Reuse `AppRouter` when its behavior fits. Keep a custom root's interactive rendering and providers aligned with the [hosting contract](../application-hosting.md).

If the direct host route works but the public URL fails, compare the proxy's forwarded path and application base path with the component route. Preserve real HTTP/API failures while repairing the specific routing boundary; a catch-all success page can hide the original 404 without mapping the component.

## Verify the repaired boundary

Open the page URL in a new tab, refresh it, and navigate to it from another page. Confirm the expected component renders in all three cases, then perform an interactive action. Repeat through the public path when deployment routing was involved. A working menu click alone does not validate server endpoint discovery.

Source evidence: `Monica.UI/Modules/ModuleUIHost.cs` (`AddPageAssembly`, `ConfigureApplicationBuilder`, `ConfigureEndpoints`), `Monica.UI/Shell/Components/AppRouter.razor`, and `Monica.UI/Shell/Support/PageRegistry.cs`. `tests/Test.Monica.UI/Shell/Support/PageRegistryTests.cs` covers the registry's contribution and sealed-snapshot behavior; browser checks establish direct-navigation and refresh behavior for the consuming host.
