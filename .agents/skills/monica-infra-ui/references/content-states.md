# Centered content states

Use `Monica.UI.Shared.Components.States.MoContentState` for a loading, empty, or unavailable block that replaces a table, card, or results region. It owns a real layout box, horizontal and vertical centering, wrapping, and consistent spacing. It stays within its containing surface and does not create a scroll owner or use viewport height.

```razor
@using Monica.UI.Shared.Components.States

<MoContentState Loading="true">
    <MudProgressCircular Indeterminate="true" Color="Color.Primary" />
    <p>@L["Common:Loading"]</p>
</MoContentState>
```

The caller supplies all localized content, including the spinner when needed, and retains data loading, cancellation, selection, and recovery callbacks. `Loading` defaults to `false`; enabling it adds busy and polite status semantics, without generating a spinner or initiating work. `Compact` defaults to `false`: the standard results region reserves 180px; compact nested cards or table states reserve 96px. These are minimum content-region sizes, not fixed heights. Long content grows naturally. Native attributes can supply an explicit role, accessible label, or test identity. `Class` is an optional identification hook; the shared component owns alignment and spacing.

Keep a surrounding native surface when it owns a border, paint, or scroll lifetime. Remove its retired state-only padding and alignment to avoid double insets. Do not expect a page's isolated class on this child component to style its root; use its parameters or a real wrapper with a narrowly scoped `::deep` selector. Headings, paragraphs, and native typography get consistent margins. An embedded `MudAlert` is centered as a block while its explanatory text retains reading alignment.

For a pane with a bounded remaining-height body, such as a document tree between its search field and footer, let that body use a column flex layout only while empty and give its `MoContentState` root `flex: 1`. Center against the whole available body, rather than a short content-sized block at its top. Keep the populated body's existing scrolling layout and avoid viewport-height calculations; use `Compact` when the pane can become short.

In a `NoRecordsContent` or `LoadingContent` slot, the native empty row must span the full body width. Responsive label/value grid rules, percentage child widths, and `data-label` pseudo-elements belong to actual data cells. Exclude the empty row from those rules, or reset its owning cell locally. When the table or body becomes a block layout, reset the native empty row too: an unclassed `tr` left as `table-row` can shrink into an anonymous table box even when its cell has `width: 100%`. Keep these resets local and empty-only. Measure the state against the painted table body at desktop and stacked breakpoints; centering within a narrow label column is still incorrect.

Keep inline progress, retained-data refresh bars, ordinary records, reading content, contextual alerts, and chart overlays with their actual owners. Do not replace an already centered bespoke state merely to adopt this component.

Verify loading, no-match, empty, unavailable and populated transitions in the browser. Check theme modes, long localized copy, keyboard recovery actions, and phone/intermediate/desktop widths without adding UI rendering tests for layout.
