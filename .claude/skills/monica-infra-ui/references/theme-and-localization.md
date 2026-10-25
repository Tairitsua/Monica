# Theme and language in the shell

Configure shell defaults inside the Web host's `builder.AddMonica(...)` callback. `DefaultTheme` is `MonicaThemeKind.Default`, which retains Monica's original Precision palette and theme identity. `DefaultDarkMode` is false, `DefaultNavigationLayout` is `ShellNavigationLayout.Sidebar`, and `ShowLanguageSwitcher` is true. Defaults apply only until a browser stores its own preference; changing a host default will not override a user's saved choice. The host still completes the UI lifecycle with `app.UseMonica()` and `app.MapMonica()`.

```csharp
using Monica.UI.Shell.Models;
using Monica.UI.Theming;

builder.AddMonica(monica =>
{
    monica.AddUI(options =>
    {
        options.DefaultTheme = MonicaThemeKind.Default;
        options.DefaultDarkMode = false;
    });
    monica.AddUIShell(options =>
    {
        options.DefaultNavigationLayout = ShellNavigationLayout.Sidebar;
        options.ShowLanguageSwitcher = true;
    });
    monica.AddLocalization(options => options.DefaultCulture = "en-US");
});
```

The Appearance dialog combines theme selection, light/dark mode, and desktop navigation placement. Its theme cards preview both the palette and theme-aware Monica mark. Selections are staged until Apply; Cancel, Escape, and dismissal discard pending selections. Applying updates the active theme and navigation layout without navigating away from the current page. Sidebar collapse remains an independent shell preference. `ThemeProviderHost` persists the selected theme and color mode under `mo:theme:data`; `ShellState` persists layout and collapse under `mo:shell:preferences`. Both use injected `IBrowserStorage` after interactive rendering, with the runtime `mo:` prefix supplied by the storage service.

Appearance keeps its native title and actions available while one content region scrolls through the full theme catalog. Repeated opening requests share one dialog. Once Apply accepts a draft, its selection controls stop accepting further changes; dismissing that application does not undo an already accepted theme or layout. Theme startup serializes restoration with explicit changes so a late saved preference cannot overwrite a new choice. Browser writes remain best effort; dialog closure alone does not prove durable storage when the browser disallows it.

Grove is a separate optional theme identified by `MonicaThemeKind.Grove` and the `grove` CSS token. Choose it in Appearance or configure `options.DefaultTheme = MonicaThemeKind.Grove` for first-visit browsers. It uses warm stone surfaces, forest accents, IBM Plex Sans interface text, editorial hierarchy derived from Playfair Display, a CJK serif fallback for display text, and diagnostic monospace, with locally served WOFF2 assets. `MonicaThemeKind.Default` keeps its existing identity and persisted value; selecting the new shell layout does not change its palette.

The shared `--mo-font-family-body` and `--mo-font-family-display` roles follow the active theme's Mud typography Default and H1 families; `--mo-font-family-mono` remains a separate diagnostic stack. Built-in font assets load from the application rather than a CDN. Grove and the CJK serif themes share one complete Noto Serif SC variable asset, approximately 10.5 MiB, requested when that face is used and reusable across weights. Coverage is limited to the bundled family and its declared browser fallbacks, not every Unicode character. Font provenance and original licenses ship under `Monica.UI/wwwroot/fonts/`; use the offline font contract in `$monica-ui-development` (`references/offline-requirements.md`) when customizing assets or checking a deployment's actual rendered fonts.

The built-in alternatives remain selectable in both light and dark modes: Grove, MudBlazor, Hermes Teal, Vibe Usage Matrix, Material Design 3, Fluent Design, Fresh, Macaron Sweetheart, Ink Landscape, Zen Ink, and Comic Burst. They share the shell's dimensions, navigation behavior, readable surface roles, and interaction contract while retaining their own palette, typography, and expressive treatment.

Built-in themes also supply quiet scrollbar colors and shapes for page content, navigation drawers, dialogs, and nested panels. Tracks are transparent and the thumb becomes clearer during interaction. Theme and light/dark changes apply immediately without host configuration; forced-color mode retains native scrollbars. Browsers with WebKit scrollbar styling use an inset thumb, while other browsers use their standard thin scrollbar with the theme's colors. Custom theme authors use the shared scrollbar contract in `$monica-ui-development` (`references/theme-css-guide.md#shared-scrollbars`) instead of adding component-specific scrollbar overrides.

The shell's `MudMarkdownThemeProvider` emits code-highlight colors from the active theme palette. Custom theme definitions supply `MudMarkdownPaletteLight` and `MudMarkdownPaletteDark` with `CodeHighlight` set to a `MudCodeHighlightThemes` preset or a custom `MudMarkdownTheme.CodeHighlight` record. These palette types retain the normal MudBlazor color properties. Code colors now belong to the palettes returned by `IThemeDefinition.CreateTheme()`; the obsolete `LightCodeBlockTheme`/`DarkCodeBlockTheme` properties and per-renderer `MarkdownThemeSync` component have been removed. Basic `PaletteLight`/`PaletteDark` instances retain Markdown's stylesheet defaults instead of emitting code-highlight variables. Enable shell Markdown assets and services through `EnableMarkdown = true` when rendering Markdown directly.

The Default dark theme retains its original Indigo accent and Zinc surfaces. Its `PrimaryContrastText` is black so small labels on the bright primary accent remain readable; dark mode does not imply white text on every accent. Hosts do not need an Analytics-specific color override. Custom theme authors should check each foreground/background pair, including inverted primary surfaces and hover states, against the palette contrast contract in `$monica-ui-development` (`references/theme-css-guide.md#2-keep-palette-ownership-in-c`) while preserving the selected theme's identity.

Shared native dialog titles anchor their Close action independently of animation, including reduced-motion settings. Enabled title actions use readable text roles rather than assuming an accent-valued `ActionDefault` works on every surface. Hosts do not need an extra dialog CSS override for this behavior. For custom theme or component styling, follow the theme CSS contract in `$monica-ui-development` (`references/theme-css-guide.md`) and verify the native title and content together.

Native tab spacing remains inside each measured tab so responsive arrows and selected captions agree with the actual rendered width. Built-in themes retain their pill or underline treatment through measured spacing; hosts do not need an inspector-specific margin or gap override. When customizing tabs, use the same theme CSS contract in `$monica-ui-development` (`references/theme-css-guide.md`) and verify complete selected captions through the native arrows in both orientations.

Shared theme CSS removes a closed native tooltip from layout only while its `.mud-popover` lacks `.mud-popover-open`. MudBlazor can retain an invisible tooltip's positioned coordinates after a resize; opacity alone does not prevent that portal from enlarging the document. Open and reopened tooltips retain native positioning and visibility. Hosts do not need a page overflow override for this behavior; keep required content scrolling available and use the portal containment audit in `$monica-ui-audit` (`references/audit-categories.md#p2--broken-layout-containment-sizing-or-scroll-ownership`) when diagnosing document-level overflow.

The shell uses the shared `MonicaIcon` vector component rather than a fixed favicon image. Its colors, silhouette treatment, and surface geometry follow the selected theme and color mode; the same component appears in theme previews. Keep the application's configured name, ID, and version separate from this theme identity. Use the theme contract for component paint so a page remains readable under every selectable theme.

Built-in palettes distinguish enabled native icon paint through `ActionDefault` from inactive controls through `ActionDisabled`. Fresh light supplies a readable shared enabled-action role for native menus and icon buttons, so hosts do not need page-specific icon overrides. Custom palette authors should preserve this distinction and follow the native action-role contrast contract in `$monica-ui-development` (`references/theme-css-guide.md#2-keep-palette-ownership-in-c`) across actual surfaces and interaction states.

Localization defaults to `zh-CN` and supports `zh-CN` and `en-US`. `ModuleLocalizationOption.SupportedCultures` and `DefaultCulture` define the host profile; Web request culture persists through the localization cookie. The switcher affects whether users see a language control, not which resources are registered. Built-in UI modules contribute their own resource markers through a Localization dependency. For a custom UI module, contribute its marker in `Describe(...)` and use `ConfigureNavigation(...)` with `RegisterLocalizedPage<TPage,TResource>(...)`; keep route and category IDs stable while translating display labels. See [shell and access](shell-and-access.md) for when that page becomes routable and protected, and `$monica-ui-localization` for resource layout and validation.

The language control has a localized accessible button name. Escape closes a mouse-opened language menu and returns focus to its native button. Selecting a culture saves the browser preference and culture cookie, then reloads the current destination.

For first-party styling, prefer `--mud-palette-*` variables and the small `--mo-color-*` contract in `Monica.UI/wwwroot/css/mo-theme-main.css`. The page-feature roles default to a primary-tinted surface, normal text, and a primary action; themes with a distinct feature treatment override the background/text pair together. This keeps large working surfaces readable without requiring the theme's full-strength action color to fill them. These roles do not change the configured palette or the colors of ordinary primary actions. Component construction, MudBlazor v9 APIs, CSS isolation, and browser checks belong to `$monica-ui-development`.

Source: `Monica.UI/Modules/ModuleUI.cs`, `Monica.Core/Modules/ModuleLocalization.cs`, `Monica.UI/Theming/Definitions/DefaultTheme.cs`, `GroveTheme.cs`, `FreshTheme.cs`, `Monica.UI/Shell/Components/Layout/ThemeDialog.razor`, `ThemeProviderHost.razor`, `CultureSelector.razor`, `Monica.UI/Shell/State/ShellState.cs`, `Monica.UI/Shared/Components/Branding/MonicaIcon.razor`, and `Monica.UI/wwwroot/css/themes/mo-theme-components.css`.
