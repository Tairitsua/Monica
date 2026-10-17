# Monica Theme CSS Guide

## Current Architecture

Monica's theme system is split between C# theme definitions and theme CSS.

### C# theme layer

- Theme definitions live under `Monica.UI/Theming/Definitions/`
- Each theme implements `IThemeDefinition`
- Each built-in theme is identified by `MonicaThemeKind`; theme definitions expose `Kind`, not a string name
- `ThemeCatalog` registers the available definitions
- `ThemeState` owns the active theme kind, dark-mode state, and generated CSS/data-theme identifiers
- `ModuleUIOption.DefaultTheme` and `ModuleUIOption.DefaultDarkMode` provide the first-visit browser defaults independently of the Shell; persisted browser preferences still win after the user changes theme settings
- `ThemeProviderHost` renders the wrapper that applies:
  - `class="mo-theme-{theme-token}-{mode}"`
  - `data-theme="{theme-token}-{mode}"`

The theme token is generated from `MonicaThemeKind.ToCssToken()`, for example:

- `MonicaThemeKind.MudBlazor` -> `mud-blazor`
- `MonicaThemeKind.MaterialDesign3` -> `material-design-3`
- `MonicaThemeKind.VibeUsageMatrix` -> `vibe-usage-matrix`

### CSS theme layer

- Theme CSS lives under `Monica.UI/wwwroot/css/themes/`
- `Monica.UI/wwwroot/css/mo-theme-main.css` imports the per-theme CSS files
- CSS should define theme tokens, surfaces, borders, typography accents, and shared component structure
- Color palettes, typography defaults, and MudBlazor tokens should still be rooted in the `MudTheme` returned by the C# definition

### Markdown and code highlighting

`ThemeProviderHost` uses `MudMarkdownThemeProvider` with the active `MudTheme` and
dark-mode state. Theme definitions return `MudMarkdownPaletteLight` and
`MudMarkdownPaletteDark` through `MudTheme.PaletteLight` and `PaletteDark`, and set
each palette's `CodeHighlight` from `MudCodeHighlightThemes.Light` or `.Dark`:

```csharp
PaletteLight = new MudMarkdownPaletteLight
{
    CodeHighlight = MudCodeHighlightThemes.Light.GitHub
},
PaletteDark = new MudMarkdownPaletteDark
{
    CodeHighlight = MudCodeHighlightThemes.Dark.GitHub
}
```

The provider emits native Markdown and code-highlight theme variables when the
theme or mode changes. Keep syntax colors in these palettes; put Monica-specific
Markdown layout and presentation in the owning CSS and import it through
`mo-theme-main.css`. Verify code blocks after switching themes and
light/dark mode, including content rendered after the switch.

### Shared typography roles

`Monica.UI/wwwroot/css/mo-fonts.css` owns shared local font faces and the public typography roles. `--mo-font-family-body` resolves from the active MudBlazor `--mud-typography-default-family`; `--mo-font-family-display` resolves from `--mud-typography-h1-family`. Their offline baseline stacks are fallbacks when MudBlazor has not supplied those variables. Theme definitions remain the source of the selected families; do not repeat a theme's font list in feature CSS or add per-theme public-role overrides. `--mo-font-family-mono` retains its purpose-specific diagnostic/code family.

Use the body role for interface text and the display role for deliberate page identity. The shared faces retain existing CSS aliases even when a licensed derivative has a different internal family name. Both `MoGroveSerifSC` and `Noto Serif SC` reference one complete variable CJK asset; `MoM3Text` and `MoM3Display` likewise share one Roboto asset. Keep each face's weight range consistent with its actual variation axes. See [offline font requirements](offline-requirements.md) for coverage, provenance and licensing.

During native theme switching, inspect the computed family on the element that paints the visible glyphs, then verify the realized font and local resource request. A correct ancestor family or successful `document.fonts.load()` does not prove which font painted a child or whether CJK silently used a fallback. Include both Latin and Chinese text, cold and warm loading, and both theme modes when the affected theme supports them. Complete source coverage still has limits: codepoints absent from that source use the declared fallback stack.

### Shared scrollbars

`ThemeProviderHost` sets `MudMarkdownThemeProvider.DefaultScrollbar="true"` to stop MudBlazor from injecting its fixed grey `html, body *` scrollbar rule. Monica's shared stylesheet supplies the theme treatment instead; do not fight that injected rule with stronger selectors or `!important`.

`components/mo-scrollbar.css`, imported by `mo-theme-main.css`, owns scrolling chrome for the viewport, navigation drawers, overlays, and nested panels. Each theme defines `--mo-color-scrollbar-thumb`, `--mo-color-scrollbar-thumb-hover`, `--mo-color-scrollbar-thumb-active`, and `--mo-scrollbar-radius` on both its root and wrapper selectors. Derive the colors from the active Mud palette so light/dark switching also updates portals outside the wrapper. The shared track is transparent; normal pointers have an 8px target with a 4px painted core, while coarse pointers retain that core inside a 12px target. Forced-color mode retains native scrollbars.

Keep overflow, overscroll, and stable gutters with their component owners. Remove local scrollbar colors and `scrollbar-width: thin` overrides when adopting the shared renderer; preserve deliberate `scrollbar-width: none` on hidden navigation rails. Browsers supporting WebKit scrollbar pseudo-elements receive the shared geometry and thumb hover/active states. Other browsers use standard thin, theme-colored scrollbars, with emphasis when their scroll host is hovered or focused. Non-auto standard scrollbar properties suppress pseudo-element styling in Chromium; the shared renderer resets them in that branch. See the [scrollbar-color reference](https://developer.mozilla.org/en-US/docs/Web/CSS/Reference/Properties/scrollbar-color).

Verify native Appearance changes across every theme and mode, an open temporary drawer at a narrow viewport, vertical and horizontal nested scrolling, thumb dragging, and forced-color mode. Check the actual scroll host's computed properties and pseudo-element paint; an inherited token alone does not prove that a local standard scrollbar property has not overridden the renderer. MudBlazor's textarea thumb and size rules have component specificity; the shared renderer matches those selectors so multiline editors also receive theme paint and the coarse-pointer target.

## Theme Authoring Workflow

1. Add a new `MonicaThemeKind` enum value.
2. Add a new `IThemeDefinition` under `Monica.UI/Theming/Definitions/` and return that enum value from `Kind`.
3. Register it in `ThemeCatalog`.
4. Add localized name and description entries for theme selection UI under `Theme:Options:{EnumValue}:Name` and `Theme:Options:{EnumValue}:Description`.
5. Create `Monica.UI/wwwroot/css/themes/mo-theme-{theme-token}.css`.
6. Import the new CSS file from `Monica.UI/wwwroot/css/mo-theme-main.css`.
7. Set the light/dark Markdown palettes' `CodeHighlight` presets. If the theme needs additional Markdown layout or presentation, include it in the theme stylesheet imported by `mo-theme-main.css`.
8. Verify persisted theme switching through `ThemeState` and browser storage.

## CSS Structure Rules

### 1. Define tokens on both the wrapper class and the `data-theme` root

```css
:root[data-theme="example-light"],
.mo-theme-example-light {
    --mo-example-panel: rgba(255, 255, 255, 0.92);
}

:root[data-theme="example-dark"],
.mo-theme-example-dark {
    --mo-example-panel: rgba(12, 18, 22, 0.92);
}
```

This keeps the theme usable for:

- normal app runtime
- DOM inspection
- browser-level overrides
- tools that read only `data-theme`

### 2. Keep palette ownership in C#

MudBlazor `Color.Secondary` selects the Secondary accent. Use text roles for supporting prose, timestamps, and captions. Native `mud-*-text` utilities use `!important`, so selecting an accent can override a component's intended muted ink even when its isolated selector matches. Remove an unnecessary accent parameter and let the owning text-role style apply; verify the computed foreground on the actual surface, including selected and hovered content. Keep deliberate semantic accents and native disabled roles intact.

`ActionDefault` owns enabled native icon paint, including menu and icon-button controls. Inspect the installed component selectors and measure normal, hover and keyboard-focus states against their actual surfaces; meaningful enabled icons need at least 3:1 contrast. Preserve `ActionDisabled` for inactive controls. When a shared palette role causes the failure, correct its C# owner instead of repeating page-specific color overrides, then verify the affected native controls and theme modes.

Pair a solid accent with its actual `PrimaryContrastText` or corresponding semantic contrast role. White is not automatically readable on a bright accent, including in dark mode. Verify small text at 4.5:1 in both directions when selected controls invert the pair; adjust the owning contrast foreground while retaining the theme's accent color when that is sufficient. Translucent hover fills require their own measurement because they can weaken an otherwise readable pair.

Use the C# `MudTheme` definition for:

- palette colors
- typography defaults
- shadows
- layout tokens that MudBlazor consumes directly

Use CSS for:

- custom background treatments
- surface shells
- borders and radii
- component-specific visual language
- light/dark-only decorative behavior

### 3. Isolate theme previews from application wrapper styles

Do not attach global `.mo-theme-*` wrapper classes to individual preview icons or cards. Those classes can own viewport-sized `::before`/`::after` decoration and broad descendant surface rules; applying one to a small preview can repaint the surrounding dialog.

Use component-scoped `data-theme` selectors and explicit palette/brand variables for the preview subtree. `MonicaIcon` accepts `ThemeKind` and `IsDarkMode` for this purpose while retaining its own `mo-brand-icon` class. Its preview must not inherit application-wrapper decoration. Verify all Appearance cards in light and dark modes, including expressive themes as both previews and the active surrounding theme.

### 4. Prefer shared MudBlazor selectors over page-specific hooks

Theme CSS should target stable shared structures such as:

- `.mud-card`
- `.mud-dialog`
- `.mud-data-grid`
- `.mud-tabs`
- `.mud-input-outlined`

Do not rely on page-only classes when the intent is a reusable theme behavior. If a temporary page-specific hook is added during diagnosis, remove it after the shared selector is in place.

Native dialog title actions are rendered by the dialog provider outside the feature's isolated content wrapper. Style a shared title action through its stable native selector in shared theme CSS; keep feature content and layout in isolated CSS. An absolutely positioned native Close must anchor to the actual title or dialog rather than rely on an animation transform to establish its containing block. Verify its bounds in ordinary and reduced-motion settings, including themes that remove animation. Do not assume `ActionDefault` provides readable contrast for an enabled icon on every surface. Check the actual icon color against its normal, hovered and selected backgrounds across themes; preserve explicit semantic colors and disabled states.

Native floating field captions remain ordinary text while focused. MudBlazor 9's focus rule uses raw Primary; the shared `themes/mo-theme-components.css` rule instead uses TextPrimary for enabled, non-error captions. Keep theme scope and state exclusions in `:where()` so the selector retains the native focus rule's specificity, and use stylesheet order rather than `!important`. Preserve native disabled/error treatment, stronger component-owned caption roles, and field-local inverse text roles. Keep palette Primary, accent borders/rings, action/icon colors, typography, and geometry independent of this caption correction. Measure the visible focused glyphs against their composed background at the rendered scale; ordinary text needs 4.5:1. Verify native focus and popup return, intentional local roles, and every selectable theme in both modes.

### 5. Keep theme CSS component-oriented

A good theme file usually looks like:

1. theme tokens
2. shell/background
3. navigation/chrome
4. shared surfaces
5. forms
6. tables/data grids
7. dialogs, menus, snackbars, overlays

Avoid scattering one-off page fixes throughout the theme file.

### 6. Keep shared button geometry overridable

Global `.mud-button` rules must not force pill radii, padding, or minimum height with `!important`. Appearance preview cards and compact shell triggers own their geometry; broad forced button paint can turn cards into pills and make real trigger widths differ from the navigation measurement rail. Set modest theme defaults without overriding component-owned shape or spacing. Inspect compiled isolation selectors and live computed styles when a local rule loses, and verify Appearance cards, ordinary actions, and Topbar overflow together.

Pair filled action and identity-surface paint with the palette's corresponding contrast-text role. A theme's shared button rule can override that foreground even when the component selects the correct `Color`; inspect the actual computed pair. A darker palette shade can improve light foreground contrast but must also remain readable with themes that supply dark contrast text. Verify enabled actions at rest, hover and keyboard focus across light and dark modes, alongside the disabled native state. Keep a feature's deliberate paint override scoped to its owned component and preserve native activation, navigation, focus and geometry. Check muted identities and responsive field captions against selected-row backgrounds as well as plain surfaces; a secondary color that passes on the latter may fail on the former.

## High-Risk Component Areas

### Drawer positioning belongs to its layout owner

Theme surface rules may paint drawers, but must preserve their fixed or absolute positioning. A broad `position: relative` rule can bring even a closed temporary drawer into the shell grid, consume a content row, and collapse the page. Keep decorative positioning separate from shared paint and exclude `.mud-drawer` from every matching branch. Excluding only an explicit drawer selector is insufficient: a drawer also matches elevated `.mud-paper` selectors.

Verify open and closed temporary drawers, both shell layouts, and a narrow viewport. Inspect computed drawer positioning and the main content height on more than one page; a visible shell with an empty content area can be a layout regression rather than a page loading failure.

### Hidden measurement elements can enlarge the document

Keep navigation measurement rails rendered at their natural width so overflow decisions remain accurate, but position the whole rail outside the viewport horizontally as well as vertically. `visibility: hidden` and an offscreen `top` do not prevent a wide absolute element from expanding the document's horizontal scroll area. Verify document width with a collapsed Sidebar near the desktop breakpoint and a wide or monospace theme, then confirm Topbar still moves destinations into More as space decreases.

### Absolute page content belongs to the page scroll owner

The shell's `.mo-body-content` establishes a positioned containing block as well as owning page overflow. An absolutely positioned descendant, including a one-pixel screen-reader label, can enlarge the browser document if its containing block is outside that scroll host. A theme wrapper's `position: relative` can expose this even while the shell has a fixed viewport height and clips its own overflow. Keep this positioning with the shared scroll owner; component-owned absolute layers still need their nearest deliberate local anchor.

Verify a long populated page with hidden accessibility text, such as the OpenTelemetry sample ledger. Compare document and body client/scroll dimensions, the label's `offsetParent`, shell bounds, and main scrolling before and after moving to the end of the page. Repeat in Sidebar and Topbar, wide and narrow viewports, and every theme/mode. Native provider portals remain outside the page scroll owner and retain their own positioning contract.

### `MudTabs` with `ApplyEffectsToContainer="true"`

MudBlazor applies rounded, outlined, and elevation classes to the root `.mud-tabs` element in this mode.

If the theme needs a visible tab shell, inspect and style:

- the root `.mud-tabs`
- `.mud-tabs-tabbar`
- `.mud-tabs-panels`

Do not assume styling only the tabbar is enough.

Native tab scrolling measures each tab's bounding rectangle. In MudBlazor v9, `GetAllTabsSize` and `GetLengthOfPanelItems` sum those widths for horizontal tabs or heights for vertical tabs; they do not include margins or the wrapper's gaps. Do not add inline margins or horizontal gaps between horizontal tabs, or block margins or vertical gaps between vertical tabs. Otherwise, the scroll arrow can disable before the final caption is visible, and selecting that tab still leaves its text clipped. Use padding inside the measured tab for spacing along its scroll axis; cross-axis spacing must also fit its containing surface.

Verify the first, middle and last tabs through native clicks and scroll arrows at narrow widths, in both orientations when supported. Check the actual arrow state, the complete selected caption and the native strip's scroll position after layout settles. A partially exposed tab can remain a valid click target and center after selection; prove the complete selected caption rather than rejecting that native interaction or weakening final visibility checks. Keep `MinimumTabWidth` within the available strip when possible, and repair theme geometry instead of hiding arrows or scrollbars.

### `MudDataGrid` header controls

MudBlazor intentionally hides several header affordances until hover. If the theme causes the header to look blank, inspect:

- `.sort-direction-icon`
- `.column-options-icon`
- `.drag-icon-options`
- `.column-options .mud-menu .mud-icon-button-label`

When needed, provide a visible default state and a stronger hover state at the theme level.

### Tables flush with a surrounding card

Use `Square="true"` on a `MudTable` or `MudDataGrid` whose edges meet an enclosing card, such as a table immediately below the card's heading:

```razor
<MudTable T="JobOperationalSummary" Square="true" Elevation="0">
    ...
</MudTable>
```

The outer card owns the rounded outline and clipping. `components/mo-table.css`, imported by `mo-theme-main.css`, honors the generated `mud-table-square` class on both the table root and its direct `.mud-table-container`. The radius override is important because theme surface selectors can be more specific than MudBlazor's square rule. It changes only the shape; borders, header colors, and horizontal scrolling retain their existing behavior. Standalone tables keep their theme's default shape unless they explicitly request square corners.

Verify the root and scroll container both compute to zero radius in light and dark modes, while the enclosing card retains its radius. Include a rounded theme, a standalone table, and a narrow viewport with horizontal overflow in browser smoke checks.

### Resolved foreground and background pairs

Contrast preflight against C# palette values is only an initial check. Verify both the actual resolved foreground and its effective background in the rendered component after the complete CSS cascade. Inspect winning theme and isolated selectors, inherited color, opacity and transparent ancestor surfaces; a theme can override either side independently. For example, Comic Burst dark can override a native button's text color, so a palette pair that passes preflight does not prove that the rendered action passes.

Choose foreground and background together when deriving an action or tinted surface from theme tokens. Verify the final pair in normal, hover, focus and selected states, and preserve semantic and disabled behavior. Check both text and icons in light and dark modes rather than treating a correct background token or `Color.Primary` parameter as proof of readable content.

Measure the element that owns the visible glyphs. A child label can override an ancestor's color; `textContent` on a glyphless ancestor does not establish the foreground actually painted. Keep ancestor styles as cascade evidence, and bind contrast and realized-font samples to the visible text owner and its composed background. Exclude inactive, hidden or covered text from an active-surface claim, with an explicit qualification when glyph evidence is absent.

### Portaled native option labels

Native popup content can render outside a feature's isolated wrapper. When the feature owns an option label, a regular HTML span inside the native item gives that label its own isolation attribute. A selector such as `.mud-selected-item:not(.mud-list-item-disabled) .owned-option-label` scopes its rightmost label while retaining the native selected-state ancestor, so it can match portaled content without a global theme override. Inspect the emitted selector and actual glyph owner in the browser. Preserve the native item, selected background, keyboard behavior and disabled roles; unselected labels and the closed field should keep their existing inheritance. Check the installed component's closed-value presenter before changing its child content.

### Snackbars and overlays

If you replace the default surface/background treatment, also verify:

- message text contrast
- action button contrast
- icon contrast
- success/info/warning/error variants

Changing only the container background is often not enough.

## Verification Workflow

1. Apply the target theme through native Appearance controls. Await the chosen control's selection acknowledgement, then the actual applied palette, layout and settled transitions. Storage configuration can prepare a reproduction, but does not verify native Apply behavior.
2. Verify the actual runtime state by checking:
   - wrapper class
   - `data-theme`
   - computed styles
3. Inspect the live DOM before editing selectors.
4. If component behavior is unclear, inspect MudBlazor source before guessing.
5. Verify both light and dark modes.
6. Verify the same component type on more than one page when the component is reused across the app.

## Manual Theme Debugging Note

When using Playwright or browser DevTools, remember that Monica's `IBrowserStorage` prefixes keys with `mo:`.

Application code reads:

```csharp
await BrowserStorage.GetAsync<ThemeData?>("theme:data", null);
```

Manual browser storage debugging must therefore set:

```text
mo:theme:data
```

not just `theme:data`.

## Layout Hooks (`mo-theme-main.css`)

Some components need sizing, scrolling, or positioning that MudBlazor's built-in parameters cannot express. These are handled through **layout hooks**: lightweight CSS classes injected via component parameters like `PopoverClass` or `ListClass`.

Layout hooks live in `mo-theme-main.css`, not in theme files or component CSS. They own only sizing, positioning, and scroll behavior. They never define colors, shadows, borders, or other visual styling.

### Current layout hooks

| Class | Purpose | Used by |
|-------|---------|---------|
| `mo-nav-menu-popover` | min-width for category menus (240px) | NavBarDropdown |
| `mo-nav-menu-popover-more` | min-width for overflow menu (220px) | NavBarMore |
| `mo-nav-menu-popover-compact` | fixed width for compact/mobile mode | NavBarMore (compact) |
| `mo-nav-menu-popover-scrollable` | max-height with viewport offset, scroll behavior | NavBarMore, NavBarDropdown submenus |
| `mo-nav-menu-popover-submenu` | max-height variant for nested submenus | NavBarMore submenus |
| `mo-nav-menu-list` | white-space nowrap for menu item text | All NavBar menus |

### When to add layout hooks

1. MudBlazor component parameters (`MaxHeight`, `MinWidth`, etc.) cannot express the required constraint.
2. The constraint is shared across multiple components or themes (not page-specific).
3. The hook defines only layout properties (`min-width`, `max-height`, `overflow`, `width`).

If the need is visual rather than layout, it belongs in theme CSS targeting standard MudBlazor selectors.

### Naming convention

Prefix with `mo-` to indicate Monica infrastructure. Use descriptive segments: `mo-{area}-{purpose}`.

## Related References

- `references/theme-authoring-pitfalls.md`
- `references/browser-storage-guide.md`
