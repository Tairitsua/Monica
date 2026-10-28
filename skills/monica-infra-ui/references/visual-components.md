# Reuse shared visual components

Select the existing component by meaning before reproducing a prototype's markup. These components ship in `Monica.UI`; they use the host's theme assets and require the normal [UI hosting setup](application-hosting.md). Callers supply localized text, admitted values, navigation targets, and business state. Components own their documented geometry and interaction semantics.

## Select a component

The visual instruments below live in `Monica.UI.Shared.Components.Visual`. Inspect that directory in the matching Monica source when an extension is needed; do not copy its CSS into a page.

| Need | Component | Caller responsibility and boundary |
| --- | --- | --- |
| Emphasize a number or subject inside a finding | `MoValueMark` | Choose a `MoVisualRole`; use `Href` only for navigation. This is not a status label, filter toggle, or command button. |
| Localized finding containing emphasized values or links | `MoRichFinding` | Supply one localized `Template` and ordered `Values` fragments. Keep language-specific word order in resources. |
| One feature value and its unit | `MoDisplayValue` | Supply formatted `Value`, optional `Unit`, and a complete `AccessibleLabel` when the visual fragments need one reading. |
| Page identity with context and actions | `MoPageMasthead` | Supply `Title`, optional `Crumb`, `Meta`, and `Actions`; the page owns action hierarchy and links. |
| Contiguous facts sharing a frame | `MoFactGrid` with `MoFact` | Supply localized labels and preformatted values; this is not a grid of independent cards. |
| State explanation with an icon tile | `MoSemanticCallout` | Supply `Severity`, `Title`, and evidence in `ChildContent`; use it for a contextual note, not a toast or modal alert. |
| State/category filter | `MoFilterChip` | Supply `Label`, `Selected`, `SelectedChanged`, optional `Count` and `ShowDot`; a real zero remains an operable filter. |
| Two measured contributions on one scale | `MoTwoRoleMeter` | Supply both values, `ScaleMaximum`, roles and `AccessibleDescription`; do not combine measurements with unrelated scales. |
| Ranked records with optional inspection and expansion | `MoRankedLedger` with `MoLedgerRow` | Supply stable keys, provenance and totals; set `OnRowSelected` only when inspection exists. It is not an execution-log table. |
| Compact ordered evidence | `MoSpine` with `MoSpineItem` | Supply ordered items and `AriaLabel`; nodes are static unless `OnItemSelected` is supplied. |
| Small populations represented one item per square | `MoDotDistribution` with `MoDotRow` | Supply real counts and roles; choose an aggregated chart for large populations. |

Other reusable owners have distinct contracts:

- `Monica.UI.Shared.Components.Navigation.MoWorkbenchNavigation` groups peer route links. The caller supplies localized `Label`, links and exact `aria-current` state; use local tabs for subordinate views instead.
- `MoContentState` owns [loading and empty content regions](content-states.md).
- `MoTimeFormatter` owns [timestamp display](time-display.md); precision and calendar semantics remain explicit.
- `MoJson` owns [JSON viewing and editing](json.md), and `MoIcon` owns [icon rendering](icons.md).
- Modal evidence inspectors use the [native side-sheet contract](#modal-evidence-side-sheets); navigation drawers retain their own layout owner.

## Modal evidence side sheets

Use `Monica.UI.Shared.Components.Dialogs.MoSideSheet` for modal evidence inspectors. It composes native `MudDialog` and requires the normal Monica UI providers and theme assets. Its provider portal stays outside page container-query and scroll bounds. The shared component owns focus, Escape/backdrop dismissal, the localized Close control, viewport geometry and one scrolling body. Navigation drawers and intentionally inline panels retain their native layout owners.

```razor
@using Monica.UI.Shared.Components.Dialogs

<MoSideSheet @bind-Open="_open" Title="@L["Inspector:Title"]">
    <ChildContent>@EvidenceContent</ChildContent>
</MoSideSheet>
```

The example's localizer, `_open` state and `EvidenceContent` belong to the caller. `Open` defaults to false; inline callers bind `Open`/`OpenChanged` or unmount on dismissal. Accept the false notification before reopening. The component drains opening and closes its inline portal on disposal, including a reference produced after retirement. Callers retain query selection, reads, refresh, mutations and any nested confirmation lifetime.

`TitleContent` replaces the text heading; `HeaderContent` holds a stationary toolbar; `ChildContent` scrolls; `FooterContent` holds stationary actions. For tabbed evidence, supply native `MudTabPanel` children through `TabPanels` instead of `ChildContent`, and bind `ActiveTabIndex`/`ActiveTabIndexChanged` (index defaults to zero). The shared `MudTabs` keeps the tab bar stationary and scrolls the panels without breaking native tab associations or keyboard behavior. Do not split native tabs from their content.

The title supplies the live accessible name. `Width` defaults to `480px` and is capped at the viewport. `CloseLabel` optionally supplies a contextual localized label. `Dismissible` defaults to true; false disables Close, Escape and backdrop dismissal while the caller completes admitted work. It does not block route retirement or cancel business work. Keep feature composition inside these slots; the shared component owns outer padding, body scrolling and reduced-motion styling. Do not add another overlay, focus trap, Escape handler or scrolling body.

A component rooted in `MoSideSheet` can also be opened with `IDialogService.ShowAsync<T>(title, parameters)` without special dialog options. In that mode `Open` is ignored and the caller owns the returned native reference and retires it with its route. JobScheduler's `ExecutionDetailDialog` and Locker's `LeaseEvidence` demonstrate service-opened and inline ownership respectively. Use ordinary `MudDialog` for confirmations; keep its reference with the feature that owns the decision.

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

`MoRichFinding` renders fragments at isolated ASCII decimal slots such as `{0}` and `{1}`; translation may reorder or repeat them. Invalid, out-of-range, formatted or aligned placeholders and doubled-brace forms stay literal. It neither implements .NET composite formatting nor unescapes braces. Surrounding text stays HTML encoded. Values must be trusted render fragments constructed by the caller, with data rendered through ordinary Razor encoding. Do not put HTML in resources, convert the sentence to `MarkupString`, or split a sentence into fixed prefix/suffix pieces around a link. Format numbers and timestamps before passing their fragments.

For a linked subject, use `<MoValueMark Role="MoVisualRole.Error" Href="@RecordUrl">@RecordName</MoValueMark>` as a slot. Keep the full subject reachable at narrow widths. An execution or deletion action still needs its normal button and confirmation contract.

## Extend the owner instead of restyling each caller

The shared visual stylesheet is `Monica.UI/wwwroot/css/components/mo-visual-language.css`. A page owns placement, surrounding line length and spacing; it must not recreate the mark's fill, ink, weight, padding or radius through a page-level descendant override. Use ordinary text or `strong` when no soft emphasis surface is intended.

When a genuinely shared capability is missing, extend its owner with the smallest semantic contract, migrate equivalent callers, and remove their retired CSS and markup. Keep scheduler timelines, execution state classification and policy evaluation in the scheduler module. A similar rectangle alone does not make two controls interchangeable. Follow the prototype replication workflow in `$monica-ui-development` (`references/prototype-replication.md`) for extraction and verification.
