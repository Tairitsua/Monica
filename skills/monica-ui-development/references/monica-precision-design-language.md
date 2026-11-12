# Monica Precision Default Design Language

`MonicaThemeKind.Default` implements the accepted Precision v3 direction: violet Primary and cyan Info over a neutral Porcelain frame in light mode, with a graphite frame and layered near-black surfaces in dark mode. This reference owns Default's theme, shared shell, and card contracts. The shared [working-interface language](monica-workbench-design-language.md) owns task-specific page composition, evidence hierarchy, density, responsive containment, and review. [Grove](monica-grove-design-language.md) is a separate optional theme with its own palette and typography. Treat an accepted prototype as an implementation contract for composition, density, typography, spacing, and surface roles.

## Preserve the shell contract

- The desktop Sidebar layout consists of a 64px category rail and a 232px contextual panel. Rail, contextual panel and header share one frame material without vertical dividers. Host identity occupies the first 56px; the category heading carries its authorized destination count and registered description. Collapsing the panel leaves the category rail available and opens a 248px temporary contextual flyout without changing the current page.
- The desktop Topbar layout uses one 56px navigation row. Keep brand, icon-and-label root categories, width-aware overflow, and configured host actions in that row. Category popups show their icon, localized heading, description and authorized count above width-aware group columns. Deeper categories expand within the same popup. Preserve native link navigation and focus behavior.
- Put Sidebar/Topbar selection in Appearance alongside theme and color mode. Keep it out of the shell's frequently used controls. Stage changes until Apply; Cancel or dismissal must preserve the existing configuration.
- At widths up to 929px, both layouts use a temporary drawer. Close temporary navigation after selecting a destination, support keyboard dismissal, and preserve a usable path back to its trigger.
- Build both layouts from the same registry hierarchy after permission evaluation. Keep localized labels separate from stable identities, retain every ancestor of an authorized leaf, prune empty branches, and select only the longest matching registered destination.
- Preserve application identity, version, language, authentication state, and configured search/notification actions. Avoid invented controls that have no host action.
- Keep routed content stable when changing layout. The shell owns the viewport and main scroll area; contextual navigation owns its local overflow. On desktop the content sheet has an 8px outer inset and 14px radius; at 929px and below it becomes edge-to-edge. Page roots take the full remaining width and available height. The sheet scroll owner exposes the `page` inline-size container for page reflow.
- Use the shared theme-aware Monica vector mark in the shell and Appearance previews. Theme selection changes its palette and treatment while preserving recognizable Monica identity; component code must not substitute a fixed favicon for this theme contract.

## Control shape and interaction

- Default's shared controls use the `6px`, `8px`, and `12px` radius scale: 6 for compact controls, 8 for standard controls and compact surfaces, and 12 for major panels or dialogs. A component may own deliberate geometry established by an accepted page design, such as the health workbench's 10px structural panels and 4px small badges. Keep that geometry consistent within its role rather than forcing every surface through a global theme selector. Named themes may define their own scale while preserving compact controls and component ownership.
- Use a full circle or pill only when the component's geometry or semantics requires it.
- Do not multiply a base radius, use `calc()` to derive larger radii, or let nested surfaces become progressively rounder.
- Give actionable controls and interactive rows or cards clear hover, focus, pressed, and selected feedback. Short token-based changes to color, border, and shadow are welcome; reserve a very small transform for pressed feedback when it reinforces the control's physical response.
- Informational grouped surfaces may use a subtle border, tonal, or low-shadow hover response to aid spatial focus. Do not translate, scale, change the cursor, or otherwise imply an action that does not exist.
- Keep motion purposeful and moderate. Prefer one short, coordinated transition over scattered effects; avoid large travel, bouncing, and repeated flourishes. Continuous motion must encode active progress or changing data, remain nonessential and pausable, and stop under reduced motion.
- Honor `prefers-reduced-motion`. Remove nonessential transforms, parallax, loops, and decorative animation; make necessary state changes immediate or near-immediate, preserve their visible meaning, and never use motion as the only cue.

## Reuse the shared card contract

- Standard `MudCard` instances receive the baseline theme-aware border/shadow hover automatically. Add `mo-card-surface` only when a genuine independent card or grouped evidence surface uses native markup; it provides the same response without changing the cursor or translating non-actionable content.
- Keep tables, rows, navigation items, visualizations, dialogs, overlays, structural panels, and nested facts outside this hook. Their interaction or layout semantics require dedicated states.
- Set `data-mo-card-tone` to `neutral`, `primary`, `secondary`, `info`, `success`, `warning`, or `error` when a semantic palette accent should drive the card's icon, value, badge, border, or subtle tonal fill. Let `--mo-card-accent` resolve from MudBlazor palette roles; the tone is useful without a rail.
- Treat the rail as an optional signature cue, not the default semantic-card treatment. Add `<span class="mo-card-surface__rail" aria-hidden="true"></span>` as a direct child only when one deliberately chosen compact status or KPI group benefits from repeated edge scanning, such as the System Info pulse strip. The shared rail is `3px` wide with a `12px` block inset.
- Do not spread the rail across unrelated cards or use it to overwrite an established page-specific composition. Preserve useful diversity: semantic outlines, icon fields, badges, progress encodings, tonal fills, and contiguous statistic strips may each carry the hierarchy without an edge rail. Do not stack the rail with a competing primary cue.
- Use the real rail child instead of `::before` or `::after`. Expressive themes may already own both pseudo-elements, and table cells use pseudo-elements for responsive labels.
- Themes may override the shared `--mo-card-hover-*` and `--mo-card-rail-*` variables when their visual identity requires it. Keep the default rail geometry in the few groups that intentionally opt in unless browser comparison demonstrates a specific need.
- Keep `mo-self-painted` separate: it protects component-owned MudCard or MudPaper paint from broad theme selectors and does not opt a surface into hover or an accent rail.

## Use color and effects deliberately

- Define Default's canvas, surface, primary, identity, and semantic-state colors in its C# palette, never in component markup or local CSS. Precision v3 uses DrawerBackground for its continuous frame, Background for the inset page sheet, and Surface for evidence and selected navigation. The shared `--mo-color-shell-frame`, `--mo-color-shell-frame-text`, `--mo-color-shell-frame-muted`, and `--mo-color-shell-frame-hover` tokens map those roles for every selectable theme.
- Choose palette-token surfaces according to their role in the page's visual plan. Identity and feature regions may use inverse colors, prominent primary accents, or other deliberate contrast; evidence regions still need readable text and distinguishable states. Do not reduce every section to an identical neutral panel merely to satisfy a minimal aesthetic.
- Apply the shared [visual language](monica-visual-language.md) on every page: a signature region, a multi-role color map, and scale tiers. Default supplies violet Primary, cyan Info, muted slate `--mo-color-role-third` for artifact/build evidence, neutral host/baseline lines, and soft semantic tokens. Its dark palette preserves these roles on layered neutral surfaces. The third role falls back to Secondary in other themes. A theme-owned pattern or illustration needs a clear role, preserved legibility, and review alongside real content.
- Gradients follow the visual language's [gradient policy](monica-visual-language.md#gradient-policy): a single-hue tonal wash behind a feature region, continuous-scale visualizations with a documented mapping, and theme-owned brand marks.
- Keep named expressive-theme illustration inside theme-owned selectors; first-party component CSS must preserve the surface-role contract.
- Coordinate accent stripes, rings, borders, shadows, textures, and graphic detail into a coherent hierarchy rather than stacking unrelated effects. Repetition may establish a purposeful visual system, but it must not make every region compete for primary attention.
- Use semantic state colors to communicate state accurately. Use identity, primary, and contextual palette roles for expressive composition; do not turn a decorative accent into a false severity signal.

## Preserve typography and assets

- Default uses locally served variable Geist (`MoPrecision`) for interface and display text, and Geist Mono for diagnostic/code evidence. Its explicit CJK fallback stack starts with Microsoft YaHei UI and Microsoft YaHei, followed by PingFang SC and Noto Sans SC. The variable WOFF2 and its OFL license ship in Monica.UI. Other themes retain their own font families; Grove's optional editorial roles belong to its own [theme contract](monica-grove-design-language.md).
- Define distinct roles for interface text, hierarchy/display text when the design needs it, and diagnostic/code text. Keep monospace focused on technical evidence instead of using it as a general UI affectation.
- Vendor the prototype's declared font as local WOFF2 files and define local `@font-face` rules. Declare an intentional offline-capable fallback stack, including CJK coverage where localized content requires it; do not substitute a system font silently.
- Verify the loaded font and fallback path in browser computed styles and the network panel with external network access unavailable. If the exact font cannot be distributed, state the fallback and treat the fidelity loss as unresolved.
- Tune typography before adding decoration; wrong metrics alter wrapping, density, alignment, and the perceived quality of every surface.

## Preserve palette meaning

- Default's scrollbar thumbs use translucent `TextSecondary` for quiet neutral chrome on both light and Zinc surfaces. Rest, hover, and drag progressively increase opacity while retaining the same hue and shared geometry.
- Assign explicit roles for canvas, surface, border, primary text, secondary text, identity, focus, runtime, success, warning, error, and information.
- Keep dark mode readable and layered rather than uniformly near-black. Adjacent structural surfaces must remain distinguishable without relying on shadows or glow.
- Give semantic states visibly different token treatments when users must scan or compare them. Reinforce color with text, icon, shape, or position instead of making color the sole signal.
- Preserve the selectable themes' identities in both modes. Shared shell dimensions and usable controls apply to Grove, MudBlazor, Material Design 3, Fluent Design, Fresh, Macaron Sweetheart, Hermes Teal, Vibe Usage Matrix, Ink Landscape, Zen Ink, and Comic Burst; their palette, type, icon treatment, and theme-owned illustration may differ from Default. Avoid global paint rules that erase those identities or spread expressive decorations into dense operational content.

## Verify theme and shell fidelity

Use the shared [page acceptance guidance](monica-workbench-design-language.md#accept-each-page-on-real-evidence) for staged page redesigns. For changes to this theme or shell contract:

- Capture the prototype and implementation side by side at viewport widths `1440`, `929`, and `390` in both light and dark modes, including additional component breakpoint boundaries where needed.
- Verify expanded and collapsed Sidebar navigation, the one-row 56px Topbar, nested menu selection, responsive overflow, and the temporary mobile drawer. Check Appearance Apply/Cancel and reload persistence without losing the current route or page state.
- Inspect a representative operational page and the Appearance previews in every selectable theme and both color modes. Confirm the Monica mark adapts, text and selected navigation remain readable, and runtime font requests stay local.
- Compare type metrics, surface roles, radii, visual hierarchy, and responsive reflow—not only colors. Record each deliberate feature treatment and every continuous-data gradient.
- Re-run representative page comparisons after global theme changes. Reject unresolved local fonts, collapsed palette roles or semantic states, overridden component geometry, missing or excessive interaction feedback, broken reduced motion, or effects that obscure the task or flatten the hierarchy.
