# Monica UI audit categories

## Findings

Scan for these violations in order of severity:

### P0 — Unsafe async component lifetime or JS interop ownership

Indicators:
- `OnAfterRenderAsync`, an event callback, a navigation callback, timer, or background task can remain incomplete while `DisposeAsync` disposes an `IJSObjectReference`, `DotNetObjectReference`, cancellation source, or other resource that its continuation later uses
- A nullable `IJSObjectReference` field is treated as usable merely because it is non-null; disposal leaves the disposed reference published, or initialization can publish a newly imported reference after disposal
- An async continuation touches component state, requests a render, or invokes JavaScript after an `await` without a component-lifetime cancellation/disposal check when the awaited operation can outlive the component
- Cancellation can abandon a resource-producing operation, such as a JS module import, without observing and disposing a late-created result
- Fire-and-forget work is untracked, uncancelable, or allowed to report exceptions outside the renderer's observed lifecycle task
- A component-owned callback/render delegate, event subscription, timer, or cancellation source remains attached after disposal begins
- A disposable transient service is injected into a component at all, or a scoped disposable intended to match component lifetime is resolved from the longer-lived app/circuit scope; manual disposal adds a second conflicting owner
- Multiple render/event paths can initialize, invoke, replace, or dispose the same JS module without explicit single ownership, serialization, or idempotent teardown
- A suspended menu action reads replacement context/callbacks, closes a newer opening, or overlaps native open/close work; same-record/same-coordinate reopening has no explicit admission identity, or a coalesced close/reopen reuses children after the primitive clears their native registrations
- `DotNetObjectReference.Create(...)` is passed inline to JavaScript or otherwise not retained and deterministically disposed by its .NET owner; JavaScript retaining the reference does not transfer disposal ownership unless an explicit, verified ownership contract says so
- `DotNetObjectReference` is disposed before every JS observer, event listener, animation-frame callback, or other callback producer that can invoke it has been stopped
- Per-component JavaScript state is stored in module globals or located with document-global selectors, so overlapping old/new component instances can mutate or tear down each other's state; queued animation frames, timers, or observers survive the JS session's cleanup
- `DisposeAsync` invokes JavaScript to mutate or clean up DOM that the renderer may already have removed; DOM cleanup should be owned by client-side `MutationObserver` logic
- Server-side JS module calls or disposal ignore expected circuit loss (`JSDisconnectedException`), or code broadly swallows `ObjectDisposedException` instead of repairing the ownership race

Fix: give the component/state owner one explicit lifetime. Mark it disposed and cancel ordinary lifetime-bound work before teardown; stop scheduling work and detach callbacks/render delegates; recheck lifetime after incomplete awaits; prevent new resource acquisitions; serialize with or drain existing users; then atomically detach shared references for teardown. Always observe resource-producing operations and dispose late-created references instead of publishing them. Stop JS callback producers before disposing their `DotNetObjectReference`, and dispose every resource once. Do not register component-consumed disposable transients. Prefer a non-disposable factory that creates a component-owned resource. Use an explicit component-owned DI scope only after inspecting the full dependency graph and proving it does not require services bound to the existing Blazor circuit scope. Keep JS session state per component/root and cancel all queued work during session cleanup. Use client-side `MutationObserver` for DOM cleanup. Catch `JSDisconnectedException` where server-circuit loss is expected. Do not catch `ObjectDisposedException` from a locally owned interop reference; repair the ownership and teardown ordering.

For Knowledge/RAG management, verify page creation through the public state factories and asynchronous retirement of the returned state, private poller, awaited renderer notifications, and owned dialogs. Facades remain borrowed from the circuit container. Probe retirement during a tokenless inventory read and delayed renderer dispatch, and stale selection A → B → A. Commands must retain the originating base/document across confirmation. A compact catalog's native focus continuation must check retirement and the intended selection after the awaited command; inspect actual `document.activeElement`. Use the owning [lifecycle contract](../../monica-ui-development/references/blazor-best-practices.md#5-state-management), without adding a second state owner or poll loop for the visual refactor.

### P1 — Custom overlays replacing MudBlazor primitives or missing keyboard behavior

Indicators:
- Custom dropdown/flyout/overlay markup with manual `@onmouseenter` / `@onmouseleave` / `@onclick` state machines
- Manual `_isOpen`, `_isHovered`, delayed-close `CancellationTokenSource` patterns
- Custom `<div class="dropdown-menu">` / `<div class="flyout-menu">` / `<div class="popover-panel">` instead of `MudMenu`, `MudPopover`, `MudDialog`, `MudDrawer`
- A temporary details overlay has no accessible identity, leaks keyboard focus, cannot close with Escape, or fails to restore focus; choosing `MudDrawer` alone does not establish dialog behavior
- Clicking a disabled native menu leaf moves focus to the document and strands Escape, or a custom label overrides its disabled appearance; verify inert activation and dismissal after the actual pointer click

Fix: use MudBlazor overlay primitives and inspect their exact source and runtime keyboard behavior. Add the missing focus/dismissal contract at the owning component where required, with trapping mounted only while open. Simple native buttons, links, and status-filter controls are valid; this category concerns overlay behavior, not all native interactive markup. Reference implementation: `NavBarDropdown.razor`, `NavBarMore.razor` in `Monica.UI/Shell/Components/Layout/`. See the shared [interaction guidance](../../monica-ui-development/references/monica-workbench-design-language.md#preserve-interaction-and-runtime-state).

For a controlled native input with a separate reset action, verify both displayed text and the filtered model. Clear must empty the actual input, restore the results, and allow that same query to be entered again. Check pending debounce as well as settled input; row counts alone do not prove correct native control ownership. See [component communication](../../monica-ui-development/references/blazor-best-practices.md#13-component-communication) for the native reset path.

Inspect rendered native ARIA state values as well as the visual result. A direct C# boolean binding can minimize a true attribute to an empty value and omit false, leaving controls without a valid pressed/expanded/selected state and breaking their selected CSS. Emit explicit lowercase string values and verify keyboard activation and exactly the intended selected control; see [ARIA state serialization](../../monica-ui-development/references/blazor-best-practices.md#84-serialize-aria-states-as-strings).

Inspect string-valued component parameters in the rendered interface. A Razor attribute such as `SelectedModelKey="PageState.CurrentEmbeddingModelKey"` is a literal string; use `SelectedModelKey="@PageState.CurrentEmbeddingModelKey"` for the intended expression. Verify the actual model, loading stage, and error text rather than accepting a successful build as proof of binding. In browser checks, wait for a native selection's resulting state before the next dependent action; a click returning does not mean the Blazor server has applied it.

For an icon-only `MudMenu`, use its `AriaLabel` parameter to name the native activator. A lower-case `aria-label` user attribute labels the menu's outer wrapper instead. Inspect the actual button's accessible name and the live menu item semantics rather than assuming that every native item has a `menuitem` role.

### P2 — Broken layout containment, sizing, or scroll ownership

Indicators:
- The outer page has no horizontal overflow, but nested schema names, media types or source endings clip, or native response/parameter columns leave working evidence too narrow; inspect each immediate text/control owner rather than accepting the document width alone
- An operational dashboard, workbench, administration page, or diagnostics page uses an arbitrary page-shell `width` or `max-width`, leaving usable shell width empty
- A `FullWidth` dialog, drawer, tab panel, or preview has a descendant `width`, `max-width`, grid track, or intrinsic `inline`/`fit-content` size that leaves usable space empty
- A flex/grid child that must shrink omits `min-width: 0`, or a grid uses `1fr` where `minmax(0, 1fr)` is required
- A component-isolated selector targets a MudBlazor render root it cannot reach; use a scoped native wrapper or a reachable `::deep` descendant selector
- Long hashes, identifiers, JSON, tables, or code stretch an ancestor instead of wrapping or scrolling inside the intended local surface
- The page or dialog becomes the horizontal scroll owner when only a table, diff, or code surface should scroll
- Document or body scroll dimensions diverge from the intended owner after resize or native tooltip/popover open-close, although the page root itself remains contained
- A cursor menu clips near the middle of a compact viewport because neither side has enough space; native flipping alone does not prove containment, and a contained root does not establish that its child menus fit
- A native menu row keeps `width: 100%` while adding horizontal margins, extending its border box and focus outline past the clipped popover; verify every outline edge and the actual row bounds, not just the outer menu
- An unconditional table `min-width` or column-width sum forces a scrollbar on empty/sparse results or on content that could fit with adaptive columns or native responsive rows
- An empty table has no overflow but a visible header collapses, clips its label or leaves the declared columns short of the table's available width
- A wrapper and native table container duplicate the same scroll ownership, or a table adds an unnecessary vertical bar inside an already scrolling dialog
- Scrollbar paint or `overflow: hidden` masks a sizing defect by making required records, native controls or keyboard focus inaccessible
- An ostensibly horizontal filter/tab strip has incidental vertical overflow from a negative child margin, clipped focus styling, or mismatched child/container height
- Responsive cell CSS assumes an enum name is a media-query threshold, so a MudBlazor table stacks earlier than the page's local spacing rules
- Stacked cells retain a desktop `colgroup`, fixed table formatting or column width, squeezing readable values into a fraction of a full-width body without producing overflow
- A fixed/minimum height leaves unexplained blank space instead of using content-driven height with a viewport-aware cap
- Nested form columns fit the viewport rule but clip floating labels or entered values inside a narrow desktop card; inspect the actual owner width in both shell layouts
- A compact provenance caption becomes an implicit grid item beside its field, squeezing the value column or moving a native reset action onto another row
- Text, controls, table cells or empty-state messages touch an ordinary panel edge because the wrapper supplies a background but no content inset; a dialog's padded outer shell is mistaken for an inset within each nested painted tab body
- Native stacked table `DataLabel` pseudo-labels touch the phone surface edge although value wrappers are inset; ineffective row padding is mistaken for actual cell padding, or the empty row inherits a two-column label layout
- Selection or row-action glyphs and focus bounds touch the surface edge despite a declared column width; native checkbox negative margins or compressed inner padding undo the intended inset, or selection replaces the focused input
- A full-width table's header, last row, hover/selected paint or pager covers its owner's rounded corners; only the outer border is rounded while descendant backgrounds remain square
- A flush table has competing outer/inner radii, does not declare whether its owner clips painted corners or the table is inset, or broad overflow clipping hides focus indicators, required local scrolling or overlay content
- A rounded notice, callout or inner card meets an enclosing surface edge while retaining its own radius or border, creating competing corners; a selected segment's border or shadow makes its apparent top/bottom inset unequal
- Distinct tools with independent tasks and lifetimes remain hidden in one local tab route, preventing direct links, browser history and meaningful shell search entries without a deliberate navigation reason
- A master/detail inventory grows the shell page through many entries while its selected dossier remains offscreen; selecting the last item requires manually returning to the details
- A sticky catalog and its search/filter controls cannot fit a short scrollport, or a compact picker remains expanded after selection instead of revealing the selected dossier
- At an intermediate owner, a compact catalog's stacked filters bury its first entry despite passing overflow checks; last-entry selection moves focus without visibly revealing the selected dossier
- Opening a compact picker exposes filters but no reachable choice, or an obsolete detail-focus continuation overrides the reopened picker; verify selected/first-entry reveal, the empty-result search fallback, and the matching heading band below the app bar
- Selecting another entry in a wide catalog preserves a shell position that leaves its matching dossier heading behind the app bar; scroll the dossier first, then select a visible catalog entry and verify minimal header reveal with the inventory's local scroll unchanged and native row focus retained. Preserve shell position when the heading band already fits; refresh and filter changes must not trigger selection focus or reveal

Fix: trace the layout from the dialog/page surface to the failing descendant and assign width, shrink, wrap, and overflow responsibility explicitly. Make operational page roots consume the owner's full available width; apply readable line-length limits to local text regions instead of the page shell. Remove contradictory internal width caps, use `width: 100%`, `min-width: 0`, `minmax(0, 1fr)`, `overflow-wrap`, or a local scroll surface only where each property expresses the intended contract. Measure both scroll dimensions at the smallest owner and inspect extending children rather than hiding a scrollbar. Measure text and controls against the nearest painted surface, opening every dialog tab and inspecting its first heading, body, fields, empty state, and final content; do not infer local insets from the page gutter or outer dialog. For tables, choose an inset table or a flush table with one corner-owning paint wrapper and a square inner native grid; inspect first/last rows, hover/selection, empty/loading state and pager at the top and bottom corners. The concrete inset, width, and corner contracts live in the shared [composition guidance](../../monica-ui-development/references/monica-workbench-design-language.md#make-hierarchy-through-composition-type-and-spacing) and [surface guidance](../../monica-ui-development/references/monica-workbench-design-language.md#give-surfaces-distinct-roles-and-character). Keep local scroll and keyboard focus usable; do not add global overflow hiding as a repair. Verify the installed component's source breakpoint and live stacked DOM. Keep long identifiers fully accessible for inspection and copying. An operational root width cap needs an explicitly authorized interaction or viewport requirement; a local reading measure is not a reason to cap the whole page. Justify fixed/minimum height with its concrete viewport or interaction requirement. For independent tools, consider dedicated registered routes with shared contextual navigation, preserving existing entry URLs, authorization and disposal while verifying reload, back/forward, direct links and exact active-route state.

For table scrollbars, apply the owning [content-driven scrolling contract](../../monica-ui-development/references/adaptive-mudblazor-list-table-pattern.md#scroll-only-when-the-content-requires-it). Inspect native and wrapper `clientWidth`/`scrollWidth` and both height dimensions together with the actual visible bars in empty, sparse and populated states at phone, intermediate and desktop widths. Also compare painted row, cell and value widths with the available body width after padding, labels and actions: no scroll does not establish readable content. When redefining cell layout, reset desktop `colgroup`, fixed formatting and width contracts at the same installed native breakpoint; use the owning [responsive-threshold guidance](../../monica-ui-development/references/adaptive-mudblazor-list-table-pattern.md#verify-the-actual-responsive-threshold). Verify 959px/960px/961px for v9 `Sm`, including the narrower actual parent in both shell layouts. Judge header visibility by its row's painted height and visibility; a zero-height `thead` retaining table-header-group display is not a visible header. Check every dialog tab and title/Close/focus availability; page-level overflow checks alone do not prove correct local ownership.

For portal containment, measure `document.documentElement` and `document.body` client/scroll width and height before resize, after resize, while a native tooltip or popover is open, after close, and after reopening. Inspect the actual portal candidates, their bounding rectangles, computed positioning, visibility and open classes; a closed, transparent element can retain coordinates outside the resized viewport. Distinguish intended content scrolling from stale portal geometry, repair the shared owner, and verify open/reopened native positioning. Do not hide required page or local scrollbars to make the dimensions agree. The shared [closed-tooltip contract](../../monica-infra-ui/references/theme-and-localization.md) describes Monica's native tooltip layout behavior.

### P3 — Theme bypass, visual-language drift, or prototype-fidelity loss

Indicators:
- Inline `Style=` / `style=` attributes for layout or visuals
- Hardcoded color, shadow paint, or background values instead of MudBlazor parameters, `var(--mud-palette-*)`, or approved `var(--mo-color-*)` tokens; deliberate component-owned spacing and shape measurements are valid
- Semantically distinct statuses, severities, categories, or progress states collapse into visually identical neutral surfaces when scanability requires differentiation
- Multiple hierarchy levels and semantic states rely on the same neutral surface plus one accent, producing a flat or monotonous page
- The page has no task-specific information hierarchy, or copies an accepted page's regions without a corresponding decision, evidence, or action
- Readable descriptions and record names shrink into 10–11px metadata, or excessive bold text makes all hierarchy levels compete
- A filtered list silently changes the meaning of a whole-scope summary, or empty/missing coverage is presented as success without evidence
- Knowledge/RAG source preview, indexing, or chunk inspection uses a global document-path match instead of the recorded source kind/group; duplicate Markdown paths or an uploaded ID collision can silently borrow another source
- Retrieval results show the edited controls as their submitted scope, hybrid ranking as similarity percentage, or representative vector validation as exhaustive coverage; stale selected-base aggregates disagree with the latest persisted metadata after queue finalization
- Light or dark mode is excessively dark, low-contrast, or monotone, so canvas, structural surfaces, borders, text hierarchy, or semantic states collapse together
- Supporting prose or captions select MudBlazor's Secondary accent, whose native important color utility overrides the intended text role; inspect the actual computed cascade using the owning [theme guidance](../../monica-ui-development/references/theme-css-guide.md#2-keep-palette-ownership-in-c)
- Success, warning, error, information, selection, and runtime states are not meaningfully differentiated when the task requires rapid scanning
- Every selected status filter is repainted Primary, losing the distinct semantic meanings visible before selection
- A row status, badge, or filter derives paint from record presence, registration count, or another state axis instead of the actual displayed status
- Default, selected, or zero-count status labels, counts, icons, or necessary control boundaries fail contrast; opacity makes an available zero-count option unreadable, or its zero is styled as an active alert
- Repeated facts, properties, metrics, or records are each wrapped in cards instead of rows, lists, definition groups, or tables
- A control, filter, row, dialog, or text uses a gradient, or a feature wash uses more than one hue, outside the [gradient policy](../../monica-ui-development/references/monica-visual-language.md#gradient-policy)
- The page has no signature region, uses a single accent where its data has several categories, or applies category colors inconsistently between its signature, ledger, and inspector; see the [visual language](../../monica-ui-development/references/monica-visual-language.md)
- Unrelated effects compete as the page's signature treatment, or a glow/pattern has no clear role in the page's intentional visual plan
- Color, depth, or illustration obscures evidence, lowers contrast, or makes semantic states hard to distinguish; a decorative chart or graphic implies unsupported data, history, or coverage
- A rate, score, trend, or percentile has no domain definition, population, or window, or a judgment such as "overrunning" has no written rule; see the [design method](../../monica-ui-design/references/design-method.md#3-define-every-judgment)
- A uniformly neutral composition erases useful identity and hierarchy even though its geometry and token usage are correct
- Actionable controls, rows, or cards lack visible hover, focus, pressed, or selected feedback
- Informational surfaces translate, scale, change the cursor, or use strong lift on hover, falsely implying an unavailable action; subtle border, tonal, or low-shadow spatial-focus feedback is valid
- Motion uses large travel, bouncing, repeated flourishes, continuous ambient effects, or ignores `prefers-reduced-motion`
- A surface stacks unrelated decorative cues, or repeated strong treatments give every region equal prominence without serving the page's hierarchy
- The same accent rail is repeated across unrelated card groups, replaces a clearer page-specific outline/icon/badge treatment, or appears where it encodes no additional status or category meaning
- Border radii drift from the theme baseline without an intentional component contract, grow across nested surfaces, or global theme selectors override component-owned geometry
- The implementation loses the prototype's typography, density, spacing rhythm, composition, surface roles, width utilization, or responsive reflow
- Prototype acceptance checks only wide and phone layouts or lack matched owner widths; intermediate native breakpoints silently change the layout, or small repeated markers, icon tiles, badges and selected segments differ without rendered measurements
- Interface, hierarchy, and diagnostic text have no deliberate font-role separation, or monospace is used broadly as decoration
- A font declared by the prototype/source is missing from local WOFF2 assets, lacks `@font-face`, fails offline, has no explicit fallback stack/CJK coverage, or is silently replaced by a fallback
- Component CSS repainting global MudBlazor behavior that should be consistent across modules
- Each new operational page family introduces another contextual route-tab perimeter, category palette, underline, or phone-tile layout instead of reusing the shared workbench navigation language
- Peer destinations are split between a family route rail and a second full-width tab rail, creating unnecessary navigation depth
- A tab frame or structural panel borrows translucent hover paint without an intentional opaque surface, producing mismatched bar/body backgrounds in a selectable theme
- Component CSS painting route `.active`, `:hover`, or `:focus` states for menu items, nav links, or list items that are themed globally
- Dark-mode overrides inside component CSS (`[data-theme*="dark"]` in `.razor.css`)

Fix: use the shared [working-interface language](../../monica-ui-development/references/monica-workbench-design-language.md) to rebuild the page around its actual decision, evidence, and actions. Restore readable text, honest summary scope, intentional surface roles, and appropriate list/fact structures. Apply the selected theme's palette and font roles while preserving accepted component geometry and useful interaction feedback. A signature region, a multi-role color map, and scale contrast strengthen identity and hierarchy; remove off-policy gradients and unsupported decorative evidence. Follow the owning [semantic-filter contract](../../monica-ui-development/references/monica-workbench-design-language.md#preserve-semantic-color-in-status-filters), then measure actual default, selected, zero-count, hover, and focus states in light and dark modes. Verify simultaneous semantic selections remain distinguishable rather than merely checking a single Primary option. Audit visible normal-sized text throughout expanded details, validation messages, and both diff panes, including line numbers; a small sample of headings or selected element types can miss low-contrast labels. For native filled actions, inspect the actual foreground/background pair at rest, hover, focus, and press: a passing base palette pair does not prove the rendered interaction passes. Include dynamic ripple descendants, state layers and pseudo-elements in held-state paint; computed button/ancestor backgrounds alone can miss an opaque label over a low-contrast composite. Retain unchanged native screenshots and inspect solid interior/label pixels separately from the base-color sample; see the [held ripple contract](../../monica-ui-development/references/theme-authoring-pitfalls.md#13-a-held-ripple-changes-the-rendered-action-background). Verify compiled selector specificity when a local correction competes with a child component or theme rule, and retain native disabled and focus behavior. Use short purposeful motion, test reduced motion, verify local fonts and fallbacks, and compare the prototype with real browser states. Keep component-owned layout and token-based presentation in component CSS; move only shared MudBlazor behavior to theme CSS.

### P4 — Private component classes that force theme coupling

Indicators:
- Custom CSS classes on menu surfaces, row items, or overlay panels (e.g. `.my-dropdown`, `.custom-flyout-item`) that are also targeted in theme files
- Theme files containing selectors for these private component classes
- Grep theme files for the private class name to confirm coupling
- Pages reproduce a shared value mark, filter, or other visual primitive with local CSS, or consume the shared component and override its internal geometry or paint through broad descendant selectors
- An extraction adds a shared wrapper while leaving duplicate render helpers and retired styles in callers

Fix: remove theme coupling. If the class is only used by the owning component, keep it in the component's `.razor.css`. Promote a hook to shared CSS only when multiple components intentionally share the same layout contract. Consult the [shared component selection guide](../../monica-infra-ui/references/visual-components.md) and verify that equivalent callers use the owner without restyling its internals. Complete extraction by removing replaced implementations; follow the [consolidation workflow](../../monica-ui-development/references/prototype-replication.md) and verify representative callers when a shared default changes.

### P5 — Duplicate utility or raw CLR type-display logic

Indicators:
- Text-resolution helpers like `GetItemText(NavigationItem)` duplicated across components instead of using `NavigationItem.ResolveDisplayText(localizer)`
- Manual route-matching wrappers instead of `NavigationRouteMatcher.GetActiveClass()`
- Repeated localization key lookup patterns that could use shared model methods
- Ordinary UI labels, tooltips, table cells, or drawer details receive type names from `Type.FullName`, `Type.AssemblyQualifiedName`, or `Type.ToString()`, exposing CLR generic backticks, `[[...]]` assembly-qualified arguments, version, culture, or public-key-token metadata
- Razor components format `System.Type` locally instead of receiving one normalized display value from the producing diagnostics, facade, view-model, or state boundary

Fix: use the shared utilities in `Monica.UI/Shell/Support/` and methods on `Monica.UI/Shell/Models/NavigationItem`. Format user-visible CLR type identities with `Monica.Tool.Extensions.GenericTypeExtensions.GetCleanFullName()` at the producing boundary, then pass the clean string to Razor. Include generic array parameters in the review: vectors, rectangular arrays and jagged arrays must retain their shape without assembly serialization syntax. Shared formatter behavior belongs in non-rendering tests of its owner, not duplicated Razor formatting. Do not expose assembly-qualified type identity in ordinary UI. Showing an assembly name or identity remains valid when assembly loading, binding, inventory, or module provenance is the explicit subject of the view.

Captured source text has its own fidelity contract. In a stack trace, raw exception capture or code reader, preserve complete original identifiers, punctuation, whitespace and unknown syntax as encoded evidence. Type-display normalization and best-effort parser reconstruction must not rewrite that source. Check visible final text and exact admitted copy payload, and inspect native Copy focus and truthful failure in the reader's real calling dialog. Parsed row counts or flat inner-marker indices do not establish complete frame counts or exception causality.
