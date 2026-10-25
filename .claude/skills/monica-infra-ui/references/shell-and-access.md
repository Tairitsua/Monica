# Compose the shell and protect feature pages

The default Shell is optional. For foundation-only or business application composition, use [application hosting](application-hosting.md). Providers now live above the router; layout components own navigation and geometry only. Theme and Markdown options belong to `AddUI`, and detailed circuit errors belong to `AddUIHost`.


For a custom page's native modal, `Monica.UI.Shared.Components.Dialogs.MoDialogHeading` supplies a visible localized heading and names the enclosing dialog when placed in `TitleContent`. Pass the page's localized `Text`; the shell's normal dialog provider retains focus and dismissal ownership. See native dialog heading authoring in `$monica-ui-development` (`references/blazor-best-practices.md#14-native-dialog-headings`) for component and local-asset lifetime requirements rather than copying feature-specific interop into the host.

For related operational routes within a page family, `Monica.UI.Shared.Components.Navigation.MoWorkbenchNavigation` supplies the shared contextual navigation treatment. Pass a localized accessible `Label` and native anchors as `ChildContent`; the feature owns each destination, localized text, and exact `aria-current="page"` selection. It does not register routes, evaluate authorization, or turn them into local tab state. Register the actual destinations with the shell and apply their access policy separately. Follow the contextual navigation contract in `$monica-ui-development` (`references/monica-workbench-design-language.md#keep-contextual-navigation-consistent`) when authoring their contents.

The shell owns native navigation observers through route and circuit retirement. It drains browser users and observer callbacks before releasing their .NET target and module references. Retirement alone does not establish why a browser call was canceled. Search preserves cancellation from its actual import, observer factory, selection or observer teardown while continuing independent cleanup; verified disconnection retains its existing handling. Hosts do not need a search-specific exception filter. Custom shell integrations should follow the interop lifetime contract in `$monica-ui-development` (`references/blazor-best-practices.md#22-bound-async-work-to-the-component-lifetime`) rather than suppressing every JavaScript cancellation.

Use a Web host. Register selected feature UI modules in `AddMonica(...)`, then complete the same application's lifecycle with `UseMonica()` and `MapMonica()` so the shell can map static assets, antiforgery, Razor components, feature endpoints, and additional page assemblies. `AddUIShell()` installs interactive server services, MudBlazor, browser storage, theme and navigation preference state, and the startup page catalog; feature UI modules contribute their pages.

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.UI.Shell.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
{
    monica.AddUIShell(options =>
    {
        options.OperationalPageAccess.AuthorizationPolicy = "OperationsViewer";
        options.DefaultNavigationLayout = ShellNavigationLayout.Sidebar;
    });
    monica.AddModuleSystemUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();
```

The host must register the named ASP.NET Core `OperationsViewer` policy and the authentication/authorization services it needs. Many feature UI modules, including `AddModuleSystemUI()`, declare their own shell dependency, so explicit `AddUIShell()` is needed here to set a host-wide option, not merely to satisfy that dependency. Select the underlying operational capability as well as its UI module; inspect each feature module's `Describe(...)` to avoid duplicating its transitive registrations. Built-in feature pages include module and system information, logging, DI, health, jobs, AI chat, knowledge, and RAG.

The shell owns the viewport and page scrolling. Its main content region also anchors absolute page descendants, including hidden accessibility labels, so long feature pages remain inside that region rather than adding browser-document scrolling. Custom page layers should retain a deliberate local positioning owner; native dialogs and popovers remain with their providers. Use the scroll-owner containment checks in `$monica-ui-development` (`references/theme-css-guide.md#absolute-page-content-belongs-to-the-page-scroll-owner`) when diagnosing an extra outer scrollbar.

Operational pages are accessible by default in Development. Outside Development, a missing effective `OperationalPageAccess.AuthorizationPolicy` denies access unless an individual page supplies a policy override. `OperationalPageAccess.DebugMode` bypasses access checks in every environment; use it only in an explicitly trusted diagnostic host. `EnableDebug` is separate: it controls detailed Blazor/SignalR errors and defaults to true in DEBUG builds and false otherwise. Feature pages must evaluate their access policy before protected work; hiding a navigation link alone is not authorization.

`PageRouteView` publishes a changing `RouteData` cascading value only after the selected destination's access check allows rendering. A shared page owning several exact routes can consume this through `[CascadingParameter]` to receive parameter notifications when navigation changes the destination while route parameters and query hints remain unchanged. Custom route views serving such pages must publish the same context after their own access gate.

Checking does not initialize the protected destination. Denied means the effective access gate did not grant access, including a missing policy registration, an invalid policy implementation, or a policy failure; it does not establish that signing in will resolve the denial. A completed denial may focus its rendered heading when no persistent control or modal owns focus. When focus remains with a control, the shell announces the denial without moving focus. Checking and theme or layout changes within the same visit do not take focus. Changing the locale reloads the document and starts a new visit.

Denied, NotFound, and Error offer the existing host-owned `/` destination; the host determines what home means. Error captures request evidence at initialization from `Activity.Current?.Id ?? HttpContext?.TraceIdentifier` and renders it only when the value is nonempty. The identifier describes the available request context, is not fabricated, and may be absent in an interactive render. Local diagnostic guidance appears only when the actual host environment is Development, independently of Release/DEBUG compilation or shell debug settings.

The circuit error notification retains browser-native Reload and Dismiss controls that work after a circuit failure. Dismiss hides the notification without establishing recovery. Reload starts a fresh page; it does not replay an application operation or promise rollback or safe retry.

`PageRouteView` implements `IAsyncDisposable.DisposeAsync()` instead of `IDisposable.Dispose()` so it can retire owned heading interop. The Blazor renderer owns the normal component lifecycle. Custom callers that own disposal must await `DisposeAsync()`; there is no synchronous disposal shim. Follow the existing interop lifetime contract in `$monica-ui-development` (`references/blazor-best-practices.md#22-bound-async-work-to-the-component-lifetime`) for custom route views.

UI modules contribute routes and navigation during `Describe(...)` through `ModuleShellUIOption.ConfigureNavigation(...)`. `INavigationRegistryBuilder.RegisterPage<TPage>` or `RegisterLocalizedPage<TPage,TResource>` records a route, optional stable category ID, label, icon, and access-policy type. Localized pages and categories need the module's resource marker registered with Localization. Contributions are startup-only and the registry seals during endpoint configuration. Duplicate routes, unknown navigation categories, missing category parents, category cycles, or a late contribution fail rather than silently replacing a page. For a route that works through in-app navigation but 404s on direct access or refresh, follow [route diagnosis](troubleshooting/routing-and-refresh.md).

Categories support multiple levels through the optional `parentId` argument on `RegisterCategory(...)` and `RegisterLocalizedCategory<TResource>(...)`. A missing parent argument creates a root category; `icon` optionally supplies that category's shell icon. Parent contributions may arrive later in startup, but every reference must resolve before the registry is sealed. Use stable category IDs independently of translated labels. The catalog publishes a flat immutable list of definitions with parent metadata; the shell evaluates each page's access policy before constructing its navigation tree and prunes branches with no authorized destinations.

Categories and pages accept an optional `description` on literal registrations or `descriptionKey` on localized registrations. Explain category capabilities and page purpose with module-owned text; localized descriptions use the same registered `TResource` as the label. Omitted descriptions remain null, supplied text or keys must be nonblank, and a conflicting category description rejects duplicate registration. `NavigationCategoryDefinition` and `PageDefinition` retain immutable `UIRegistryText? Description` metadata and resolve it through `ResolveDescription(ILocalizationCatalog)` for the current host. The authorized tree exposes resolved `NavigationGroup.Description` only for retained branches; descriptions do not create destinations or bypass access policies.

For example, a feature module can add a localized branch beneath the shell's Module category in its navigation callback:

```csharp
module.Require<ModuleLocalization, ModuleLocalizationOption>(
    static options => options.AddResource<ExampleResource>());
module.Require<ModuleShellUI, ModuleShellUIOption>(static options =>
    options.ConfigureNavigation(static registry =>
    {
        var diagnostics = registry.RegisterLocalizedCategory<ExampleResource>(
            "example.diagnostics",
            "Navigation:Diagnostics",
            order: 20,
            parentId: BuiltInNavigationCategoryIds.Module,
            icon: MoIcons.Cube,
            descriptionKey: "Navigation:DiagnosticsDescription");
        registry.RegisterLocalizedPage<ExampleDiagnosticsPage, ExampleResource>(
            "/example-diagnostics",
            "Navigation:Overview",
            icon: MoIcons.Server,
            categoryId: diagnostics,
            addToNav: true,
            navOrder: 10,
            accessPolicyType: typeof(ExampleDiagnosticsAccessPolicy),
            descriptionKey: "Navigation:OverviewDescription");
    }));
```

This example belongs in a UI module's `Describe(ModuleDescriptor module)` method and imports `Monica.UI.Icons` for `MoIcons`. `ExampleResource` must implement `ILocalizationResource`, `ExampleDiagnosticsPage` must declare the matching Blazor route, and the host must register `ExampleDiagnosticsAccessPolicy` as an `IPageAccessPolicy`. A deeper branch uses the returned `diagnostics` identity as its parent. Register actual routable pages rather than deriving menu entries from URL segments. The built-in module diagnostics branch exposes Overview, Performance, Modules, Dependencies, and Discovery under the existing `/module-system-dashboard` routes; each entry retains the module's operational access policy. When destinations overlap, the shell selects the longest matching route so only one leaf is active.

`DefaultNavigationLayout` defaults to `ShellNavigationLayout.Sidebar`: a 64px category rail and collapsible 226px contextual sidebar. Built-in category icons use the shared [MoIcons SVG catalog](icons.md); custom registered fragments remain supported under the same trusted 24-unit body contract. Category icons come from registry metadata rather than translated labels, while leaf destinations use quiet dots. `ShellNavigationLayout.Topbar` uses one 56px row, shows category icons alongside their labels, and expands nested categories inside a popup with category identity and description above its destinations. A fine, hover-capable pointer opens Topbar categories and More without a click or a focus change; pointer travel into the popup keeps it open. Click, touch, and ArrowDown open with focus, Escape dismisses and restores the trigger, and native Tab can leave the menu. The Appearance dialog owns the infrequent layout switch; navigation placement and theme selection are independent preferences. At widths up to 929px, both preferences use temporary drawer navigation; selecting a desktop layout does not reserve a sidebar on a narrow screen. The shell owns viewport height and scrolling, while operational pages consume its available content width. Changing layout or collapsing the sidebar preserves the current route and routed page instance.

Selecting a category on the collapsed desktop rail immediately displays a temporary, full-height 226px context panel beside the 64px rail, without a slide or flyout animation. The page keeps its collapsed-layout width, and the rail remains available to switch categories. Opening or dismissing this panel does not persist an expanded sidebar. Destination selection, Escape, and outside dismissal close the panel; dismissal returns focus to its category trigger, while route navigation allows the destination heading to receive focus. Crossing into the mobile viewport closes the desktop context instead of retaining hidden open state; focus remains on visible navigation rather than returning to a hidden rail trigger. Preserve native keyboard access to category controls, nested group disclosure, and links. Verify both expanded and collapsed navigation in short viewports so the category rail scrolls and its controls remain reachable.

`ShellState` restores browser preferences after the first interactive render through `IBrowserStorage`. Layout and sidebar collapse persist under `mo:shell:preferences`; saved browser preferences override the configured default. Components consuming this circuit-scoped state must not initialize browser storage during prerendering or dispose the injected storage service.

`ModuleShellUIOption.AppName`, `AppId`, and `AppVersion` override the host's application identity in the shell. `EnableNavBarSearch` defaults to true and supplies a toolbar control and Ctrl/Cmd+K shortcut for the built-in registered-page search palette. Set it to false to remove both entry points, or configure `NavBarSearchAction` to replace the palette with a host-owned callback for both entry points. `MaxVisibleCategories` defaults to 10 and limits visible Topbar root categories; available width can move additional categories into More. Sidebar and temporary drawer navigation retain the complete authorized hierarchy. Theme and language defaults are in [theme and localization](theme-and-localization.md).

Search evaluates the current user's page policies before resolving navigation metadata, excludes hidden navigation pages and route templates with parameters, and refreshes its snapshot when authentication changes. Each result shows its localized title, description, and root category; nested branches also show their category path. A page description takes precedence over its immediate category description and then its root category description. Every whitespace-separated query token must match the title, description, category path, or route; exact and prefix title matches rank first using the current culture's case and accent rules. The input keeps focus while arrows select results, Enter rechecks access and opens the selected destination, and Escape or outside dismissal closes the palette. IME composition is left to the input, and Ctrl/Cmd+K does not interrupt another open dialog. This search is a navigation aid, not a replacement for route or API authorization.

The built-in `NavigationSearch.RequestSearchAsync()` retains its opening acknowledgement: it completes after the native reference and component have been captured, rather than waiting for the palette to close. Repeated opening requests share that acknowledgement; an already owned dialog or child-retirement chain prevents another opening. The launcher separately retains the actual native result and captured component work through retirement, remains natively focusable for dismissal, closes current or late references and rejects navigation after retirement. Native result completion alone does not establish component retirement. A configured `NavBarSearchAction` instead contributes its actual returned task: repeated requests share it, and shell retirement observes it to completion. The host owns any custom dialog, child resources, routing and cancellation, receives no automatic cancellation token, and must finish the returned task rather than waiting for the shell to dispose host-owned UI.

## Use shared cursor menus

`MoContextMenu<TItem>` uses MudBlazor's menu and popover primitives for cursor placement, nested menus, dismissal, and action callbacks. Supply the mouse event's page coordinates through `InitialX` and `InitialY`, retain the selected `ContextItem`, and clear `Visible` in the `OnClose` callback. Its native focus trap exists only while the menu is actually open, admits keyboard input at the container, and restores prior focus on dismissal. ArrowDown enters the native item sequence; pointer admission does not preselect an item independently of the menu's internal navigation state. Keep a keyboard-accessible alternative for important actions; a right-click menu alone does not make them discoverable or keyboard accessible.

Increment `OpeningRevision` for every accepted right click, including the same record at the same coordinates. Its default `0` preserves automatic opening detection for visible transitions and changed context/item-list inputs, but cannot distinguish identical admissions coalesced into one visible render. Ordinary rendering, refresh and dismissal do not increment the revision. Graph callbacks must supply the actual event's `pageX/pageY`; Blazor mouse callbacks use `PageX/PageY`, including in a document-scrolling host.

An ordinary parameter render also adopts in-place top-level list membership/order changes. With an explicit unchanged opening revision this refreshes the native presentation without retargeting an already admitted action. The retained item sequence is a shallow snapshot, not a deep freeze of mutable item objects or custom context objects.

The selected action retains its admitted context and callbacks. Success runs the leaf action, then `OnItemClick`, then `OnClose`; a newer opening or retirement skips the old UI completion tail while the admitted action remains observed. An action that hides its own menu without opening another does not invalidate its successful tail. Faults propagate and do not imply successful dismissal. The wrapper serializes native transitions and retires only its own admitted native work; caller-owned dialogs and operations retain their existing lifetime. `MoContextMenuItems<TItem>` keeps its direct-use `OnClick(ContextItem)` then `OnItemSelected` behavior unless `OnItemRequested` explicitly takes ownership of the selection pipeline.

The shared global stylesheet owns a non-interactive, one-pixel menu anchor. Preserve its measurable rectangle: MudBlazor checks the anchor's viewport visibility before applying cursor coordinates, so `display: contents` can leave the popup offscreen inside a scrolled owner. A feature's isolated CSS cannot style the portaled popup through its local wrapper. Verify actual anchor and popup geometry after scrolling, near viewport edges, and when reopening at another cursor position; leave positioning and dismissal with the primitive rather than adding a second overlay implementation.

At viewport widths up to 600px, the cursor root uses 10px horizontal viewport insets. This covers the case where neither side of a central cursor has room for the menu; native vertical placement and child-menu anchoring remain active. Check both Copy and Navigate child surfaces separately at compact widths, since root containment does not establish nested-menu containment. Native focus wrappers can change direct-child selector matching: stack branch wrappers as distinct rows while retaining their intrinsic anchor widths, then verify that an open child leaves its sibling branch visibly reachable.

Disabled leaves retain native disabled activation guards and the disabled palette role. Their pointer target remains within the menu so a click does not strand keyboard focus on the document; verify that the action stays inert and Escape still dismisses afterward. Shortcut text uses the readable primary-text role with smaller monospace typography; it is a display hint, not a keyboard binding. Verify actual enabled text and hint contrast against each theme's composited menu surface rather than assuming a secondary-text token is readable.

## Read the module workbench

`AddModuleSystemUI()` supplies the five `/module-system-dashboard` sections and their inspectors; it requires Module System, localization, and the shell. The operational page policy covers the root and section aliases, and each diagnostics read rechecks access. A page-owned session retains selection, filters, and an imported baseline while navigating between its sections. Leaving the page disposes that session. Refresh reads the host-owned snapshot; it does not rerun module callbacks, resolve application services, or rescan assemblies.

Overview distinguishes tracked application startup from Monica composition. Module contribution totals combine callback and startup-work costs, which can overlap in wall-clock time; they are not mutually exclusive shares of application startup. The causal ribbon preserves every recorded occurrence; the bounded waterfall discloses omitted spans and has a table alternative. Dependency autocomplete and the edge table complement the graphical explorer. Discovery match counts describe type-query observations and may exceed enumerated type counts; its assembly facets may overlap.

Opening a module through its portable `?module=` ID shows the detail drawer; closing it removes that query without clearing synchronized selection. Module and Help inspectors support modal keyboard focus and Escape dismissal. Configuration is a lazy, bounded, read-only projection with the host's sensitivity policy. Export produces a sanitized portable baseline without option values, assembly paths, raw failures, or stack traces. Import compares a compatible JSON file of at most 10 MiB in the page session; it does not mutate host composition. Remove baseline clears that comparison.

## Read Service Discovery evidence

`AddServiceDiscoveryUI()` requires Service Discovery, localization, and the shell. Configure the underlying role and storage through the hosting skill's [Service Discovery contract](../../monica-infra-hosting/references/service-discovery.md). The UI contributes five separate Monitor destinations:

| Route | Workspace |
| --- | --- |
| `/service-discovery-manage` | Registry: searchable service identity, a paged service ledger, and instance inspection. |
| `/service-discovery-manage/service-map` | Service map: graphical membership and a native service roster. |
| `/service-discovery-manage/dependencies` | Dependencies: declared subdomain relationships and a native relationship ledger. |
| `/service-discovery-manage/subdomains` | Subdomains: catalog identity, owned services, and external dependants. |
| `/service-discovery-manage/current-host` | Current host: local identity, registration, configuration, and election evidence. |

The subdomain directory accepts an escaped `?domain=` catalog name, matched without regard to case. An absent catalog entry is distinct from a failed catalog read. The graphical views retain native lists so service inspection and dependency navigation do not require pointer access to a graph.

Opening a service creates a detached, frozen native inspector. Its originating capture time and the time it was frozen are separate facts; periodic page refresh does not rewrite the open inspector. Instance registrations, retained UI-observed disappearances, complete metadata, and release evidence remain inspectable. Configuration and local election state are explicitly scoped to the querying host, including when the selected service is remote. For membership, failure, lease, retention, and force-delete semantics, read [captured diagnostics and provider reads](../../monica-infra-hosting/references/service-discovery.md#captured-diagnostics-and-provider-reads).

Registry and current-host pages own their cancellable refresh loops; graph and directory captures refresh on demand. Failed reads retain the previous successful capture with a failure notice rather than displaying a successful empty registry. The host owns access to these diagnostics and their underlying APIs; navigation visibility does not replace authorization.

For embedded workspaces migrating from the former tabbed page, navigate through the dedicated routes instead of `ServiceDiscoveryPageState.SelectedDomain` or `ActiveTabIndex`. The scoped registry/current-host state no longer implements `IDisposable` or exposes timer/callback setup (`Initialize`, `ConfigureAutoRefresh`, or `UpdateAutoRefresh`). The consuming component owns and drains its cancellable polling loop before retirement; it must not dispose borrowed state. Set the registry's filter properties and call `ApplyFilters()`, or use `SetSelectedDomains(...)` for deliberate domain selection. Empty status or domain selections mean no matches. `SelectAllDomains()` follows domain membership arriving in subsequent successful captures; an explicitly cleared selection remains empty after recovery.

Source and checks: `Monica.UI/Shell/Components/PageRouteView.razor`, `Monica.UI/Pages/ErrorPage.razor`, `Monica.UI/Pages/NotFoundPage.razor`, `Monica.UI/Shell/Components/Layout/MainLayout.razor`, `Monica.UI/wwwroot/js/shell-page-focus.js`, `Monica.UI/Modules/ModuleShellUI.cs`, `ModuleSystemUI.cs`, `Monica.UI/Shell/Models/NavigationCategoryDefinition.cs`, `Monica.UI/Shell/Support/INavigationRegistryBuilder.cs`, `NavigationTreeBuilder.cs`, `ShellIcons.cs`, `OperationalPageAccessPolicy.cs`, `Monica.UI/Shell/Components/Layout/NavBar.razor`, its code-behind, `NavigationPopover.razor`, `NavigationPopoverInteropSession.cs`, `tests/Test.Monica.UI/Shell/Components/Layout/NavigationPopoverInteropSessionTests.cs`, `Monica.UI/Shell/State/ShellState.cs`, `Monica.UI/Pages/ModuleSystemPage.razor`, `Monica.UI/UIModuleSystem/State/ModuleSystemWorkbenchSession.cs`, `Monica.ServiceDiscovery/Modules/ModuleServiceDiscoveryUI.cs`, `Monica.ServiceDiscovery/Pages/UIServiceDiscoveryPage.razor`, its section pages, `Monica.ServiceDiscovery/UIServiceDiscovery/Components/DiscoveryInspectorHost.razor`, and `tests/Test.Monica.UI/Shell/Support/PageRegistryTests.cs`.
