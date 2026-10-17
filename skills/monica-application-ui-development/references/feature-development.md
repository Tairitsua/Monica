# Application UI Feature Development

Place a user task under `Presentation/{Product}.UI/Features/{Feature}`. Start with a page and add components, state or form models only when they own meaningful behavior. A page owns view-local state; a circuit-scoped service is appropriate only for deliberately shared state. Do not retain backend operation services in either owner.

Inject the generated domain interface, such as `IOrderingCommandApi`, and submit a Protocol request through its named operation:

```csharp
var result = await commands.ApproveOrder(
    new CommandApproveOrder(orderId), cancellationToken);
if (result.IsOk())
{
    selectedOrder = result.Data;
}
else
{
    message = result.Message;
}
```

Import `Monica.Core.Results` for result extensions. Show structured field errors from the result contract when present. Never render reserved diagnostics or raw exceptions to users. A failed result must not silently clear the form or show a success notification.

Use UI form models for partial input, selections, draft values and localized validation. Map them to stable requests only at submission. Keep business rules in backend use cases; page-level checks improve feedback without replacing server authorization or validation.

Disable duplicate submissions while an owned write is pending. Do not write during initialization, prerendering or reconnect. Initialize reads deliberately, and account for prerender/interactive initialization. Retire owned loads when navigating away: cancel, observe completion, and avoid updating disposed UI. When requests can overlap, use cancellation/version ownership so stale reads cannot overwrite current selection. Cancellation cannot undo an already committed command; do not automatically replay it.

Use native Blazor `@page` routes. Register the presentation assembly through `AddUIHost`; a route does not require a Shell menu entry or a module. Route-level presentation gating is independent of authoritative handler authorization. Hosts using attribute-based page authorization should compose `AuthorizeRouteView` in their own router; `PageRouteView` enforces Monica catalog policies, not arbitrary page `[Authorize]` attributes.

Product layouts own viewport/scroll geometry and navigation. Shared providers render once above the router through `UIProviders` (included by `AppRouter`). Reuse theme palette/tokens and local assets. Component CSS belongs in `.razor.css`; no implicit sidebar width or app-bar offset is assumed for product pages.

Resources live at the presentation project root `Localization/`. The host registers them with `AddLocalization(options => options.AddResource<ProductResource>())`; no business UI module is necessary. A page can inject its resource directly without registering a navigation label. Shared mechanics and resource validation remain in the existing UI skills.

Verify refresh of an unlisted route, theme/overlay behavior after switching layouts, narrow screens, failed submits, and navigation during a pending read. Application behavior tests use real host composition and operation scopes; component rendering tests remain opt-in.
