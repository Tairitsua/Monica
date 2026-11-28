# Reuse shared visual components

Select the existing component by meaning before reproducing a prototype's markup. These components ship in `Monica.UI`; they use the host's theme assets and require the normal [UI hosting setup](application-hosting.md). Callers supply localized text, admitted values, navigation targets, and business state. Components own their documented geometry and interaction semantics.

## Select a component

The visual instruments below live in `Monica.UI.Shared.Components.Visual`. Inspect that directory in the matching Monica source when an extension is needed; do not copy its CSS into a page.

| Need | Component | Caller responsibility and boundary |
| --- | --- | --- |
| Emphasize a number or subject inside a finding | `MoValueMark` | Choose a `MoVisualRole`; use `Href` only for navigation. This is not a status label, filter toggle, or command button. |
| Localized finding containing emphasized values or links | `MoRichFinding` | Supply one localized `Template` and ordered `Values` fragments. Keep language-specific word order in resources. |
| One feature value and its unit | `MoDisplayValue` | Supply formatted `Value`, optional `Unit`, and a complete `AccessibleLabel` when the visual fragments need one reading. Set `Compact` for a proportional muted unit. The surrounding surface can supply `--mo-vl-display-size`. |
| Page identity with context and actions | `MoPageMasthead` | Supply `Title`, optional `Icon` for the workspace seal, `SealAccent` for a soft primary seal, `Crumb`, `Meta`, and `Actions`; the inherited `--mo-vl-masthead-seal-size` defaults to 52px and can be set to 48px in a narrow container; the page owns action hierarchy and links. |
| Contiguous facts sharing a frame | `MoFactGrid` with `MoFact` | Supply localized labels and preformatted values; this is not a grid of independent cards. |
| State explanation with an icon tile | `MoSemanticCallout` | Supply `Severity`, `Title`, and evidence in `ChildContent`; use it for a contextual note, not a toast or modal alert. |
| State/category filter | `MoFilterChip` | Supply `Label`, `Selected`, `SelectedChanged`, optional `Count` and `ShowDot`; a real zero remains an operable filter. Use `Icon` for a category glyph or `mo-vl-chip--hatched` with `ShowDot` for an unreported state. `Pill=true` selects the compact capsule silhouette; the default remains rectangular. Use `mo-vl-chip--instrument` for a workbench filter rail or add `mo-vl-chip--diamond` when a diamond matches the plotted signal. |
| Copy an identifier or evidence | `MoCopyButton` | Pass the complete `Value` and a localized `Label`. Optional child content supplies the visible caption. Empty values disable the button. Clipboard success and failure feedback are localized. |
| Page through filtered records | `MoCompactPager` | Supply the bounded one-based `Page`, positive `PageSize`, filtered `Total`, and a `PageChanged` handler. The caller owns the URL or local index. Empty results hide the pager. Set `ShowSinglePage` to retain the range for one page. Set `ShowSinglePageControls="false"` to omit its disabled arrows and position. Use `PageSizeContent` for a caller-owned size selector. At a boundary the unavailable arrow ignores activation, skips subsequent Tab navigation and retains existing focus so the enclosing sheet still receives Escape. |
| Two measured contributions on one scale | `MoTwoRoleMeter` | Supply both values, `ScaleMaximum`, roles and `AccessibleDescription`; do not combine measurements with unrelated scales. |
| Ranked records with optional inspection and expansion | `MoRankedLedger` with `MoLedgerRow` | Supply stable keys, provenance and totals; set `OnRowSelected` only when inspection exists. It is not an execution-log table. |
| Compact ordered evidence | `MoSpine` with `MoSpineItem` | Supply ordered items and `AriaLabel`; nodes are static unless `OnItemSelected` is supplied. |
| Small populations represented one item per square | `MoDotDistribution` with `MoDotRow` | Supply real counts and roles; choose an aggregated chart for large populations. |

Other reusable owners have distinct contracts:

- `Monica.UI.Shared.Components.Navigation.MoWorkbenchNavigation` groups peer route links. The caller supplies localized `Label`, links and exact `aria-current` state; Set `Compact` for a short rail of peer pages within a workbench.
- `MoContentState` owns [loading and empty content regions](content-states.md).
- `MoTimeFormatter` owns [timestamp display](time-display.md); precision and calendar semantics remain explicit.
- `MoJson` owns [JSON viewing and editing](json.md), and `MoIcon` owns [icon rendering](icons.md).
- Modal evidence inspectors use the [native side-sheet contract](#modal-evidence-side-sheets); navigation drawers retain their own layout owner.

Use `MoVisualRole.Third` for a third categorical identity such as a hub contract, observed send method, or target selector. It reads the approved `--mo-color-role-third` token independently of the theme's Secondary accent. `Secondary` continues to use the actual secondary palette color. Status and failure judgments keep their own Success, Warning, Error, Info or Neutral roles.

Use a `mo-workbench-container` around `mo-page-shell mo-page-shell--workbench` to measure breakpoints at the workspace’s available inline width before applying gutters. Chart rulers, tracks, and registry layouts can use the same `page` container. The workbench frame fills its owner on wide displays. Apply a reading width to the part containing prose when it needs a constraint.

Use `mo-vl-soft-action` and a visual role for a compact inline command. It supplies hover, press and focus paint for native buttons and links. Use `mo-vl-search-clear` for a clear action inside a compound search field and `mo-vl-search-clearable` on the field wrapper to suppress the browser's duplicate cancel control. The caller controls visibility and keyword state. Use `mo-vl-swatch--instrument` for a 10 px square and `mo-vl-swatch--label` for optical adjustment next to a text label. Data marks use their plot geometry.

## Modal evidence side sheets

Use `Monica.UI.Shared.Components.Dialogs.MoSideSheet` for modal evidence inspectors. It composes native `MudDialog` and requires the normal Monica UI providers and theme assets. Its provider portal stays outside page container-query and scroll bounds. The shared component owns focus, Escape/backdrop dismissal, the localized Close control, viewport geometry and one scrolling body. Navigation drawers and intentionally inline panels retain their native layout owners.

```razor
@using Monica.UI.Shared.Components.Dialogs

<MoSideSheet @bind-Open="_open" Title="@L["Inspector:Title"]">
    <ChildContent>@EvidenceContent</ChildContent>
</MoSideSheet>
```

The example's localizer, `_open` state and `EvidenceContent` belong to the caller. `Open` defaults to false; inline callers bind `Open`/`OpenChanged` or unmount on dismissal. Accept the false notification before reopening. The component drains opening and closes its inline portal on disposal, including a reference produced after retirement. Callers retain query selection, reads, refresh, mutations and any nested confirmation lifetime.

`TitleContent` replaces the text heading inside the shared padded identity region. Compose the subject, icon and status there. `HeaderContent` is a stationary toolbar region below the heading. Its default is edge to edge, so callers using that default supply their own inner padding. Set `InsetHeader="true"` for an unpadded toolbar when the actions and tab bar should share the heading and body gutters, as in `RepositoryContextEvidence`. Combine it with `Compact="true"` for workbench density.

`ChildContent` scrolls, and `FooterContent` holds stationary actions. Put related navigation in the evidence body when the workflow's primary decision belongs in the fixed footer. For tabbed evidence, supply native `MudTabPanel` children through `TabPanels` and bind `ActiveTabIndex`/`ActiveTabIndexChanged` (index defaults to zero). The shared `MudTabs` keeps the tab bar stationary and preserves tab associations and keyboard behavior. With `InsetHeader`, the active tab panel owns scrolling and starts at the top when selected.

Use `WrapTabs="true"` with `InsetHeader` for a short tab list that must stay visible at phone width, such as the four DataChannel evidence tabs. The tab bar can grow by a row while retaining native tab selection and keyboard behavior.

The title supplies the live accessible name. `Width` defaults to `480px` and is capped at the viewport. `CloseLabel` optionally supplies a contextual localized label. `Dismissible` defaults to true; false disables Close, Escape and backdrop dismissal while the caller completes admitted work. It does not block route retirement or cancel business work. Keep feature composition inside these slots; the shared component owns outer padding, body scrolling and reduced-motion styling. Do not add another overlay, focus trap, Escape handler or scrolling body.

Handle `DismissRequested` when the caller owns dismissal, such as confirming a draft discard or clearing a URL selection. It covers the Close control, Escape and backdrop clicks. Set `Open` to false when the inline owner accepts dismissal. Use `Expanded` with `ExpandedWidth` to toggle a wider layout while retaining the native focus trap and current content.

A component rooted in `MoSideSheet` can also be opened with `IDialogService.ShowAsync<T>(title, parameters)` without special dialog options. In that mode `Open` is ignored and the caller owns the returned native reference and retires it with its route. JobScheduler's `ExecutionDetailDialog` and Locker's `LeaseEvidence` demonstrate service-opened and inline ownership respectively. Use ordinary `MudDialog` for confirmations; keep its reference with the feature that owns the decision.

Set `FocusCloseOnOpen="true"` when a read-only evidence sheet starts at Close, as the Repository session sheet does. The native focus trap saves the active opener before the sheet moves focus to the enabled Close control in its stationary heading. Dismissal returns focus to a still-mounted opener. Direct detail URLs also start at Close. With the option omitted, the native dialog chooses initial focus.

Module System and Help, HealthCheck, Seeder, Kubernetes and Terminal's modal context use the same component. Verify affected callers after changing it: page scroll before opening, stationary heading/tabs/footer while the body scrolls, desktop and phone bounds, live evidence/title changes, Escape/backdrop/focus return, nested confirmation, disabled dismissal during work, and route/back/forward cleanup. `components/mo-side-sheet.css` owns the shared frame; never copy it into feature CSS.

## Compose a localized finding

Use the resource's unformatted `.Value` so the component can substitute fragments. For example, define `Summary:Finding` as `"{0} retained records include {1} failures."` in English and `"{0} 条保留记录中包含 {1} 次失败。"` in Chinese, then render:

```razor
@using Monica.UI.Shared.Components.Visual

<p>
    <MoRichFinding Template="@L["Summary:Finding"].Value"
                   Values="@([CountMark(Total, MoVisualRole.Primary),
                              CountMark(Failed, MoVisualRole.Error)])" />
</p>

@code {
    private RenderFragment CountMark(long count, MoVisualRole role) =>
        @<MoValueMark Role="@(count == 0 ? MoVisualRole.Neutral : role)">@count.ToString("N0")</MoValueMark>;
}
```

`L`, `Total`, and `Failed` belong to the consuming component. The example explicitly treats a zero failure count as neutral; the shared component does not infer business meaning from its text. Select Error, Warning, Success, Info, Primary, Secondary, or Neutral according to the page's evidence. A missing observation is not a zero.

`MoRichFinding` renders fragments at isolated ASCII decimal slots such as `{0}` and `{1}`; translation may reorder or repeat them. Invalid, out-of-range, formatted or aligned placeholders and doubled-brace forms stay literal. It neither implements .NET composite formatting nor unescapes braces. Surrounding text stays HTML encoded. Values must be trusted render fragments constructed by the caller, with data rendered through ordinary Razor encoding. Do not put HTML in resources, convert the sentence to `MarkupString`, or split a sentence into fixed prefix/suffix pieces around a link. Format numbers and timestamps before passing their fragments. When composing a finding in C#, call `MoRichFinding.Compose(template, firstValue, secondValue)` to render the same slots and escaping rules.

For a linked subject, use `<MoValueMark Role="MoVisualRole.Error" Href="@RecordUrl">@RecordName</MoValueMark>` as a slot. Keep the full subject reachable at narrow widths. An execution or deletion action still needs its normal button and confirmation contract.

## Extend the owner instead of restyling each caller

The shared visual stylesheet is `Monica.UI/wwwroot/css/components/mo-visual-language.css`. A page owns placement, surrounding line length and spacing; it must not recreate the mark's fill, ink, weight, padding or radius through a page-level descendant override. Use ordinary text or `strong` when no soft emphasis surface is intended.

When a genuinely shared capability is missing, extend its owner with the smallest semantic contract, migrate equivalent callers, and remove their retired CSS and markup. Keep scheduler timelines, execution state classification and policy evaluation in the scheduler module. A similar rectangle alone does not make two controls interchangeable. Follow the prototype replication workflow in `$monica-ui-development` (`references/prototype-replication.md`) for extraction and verification.
