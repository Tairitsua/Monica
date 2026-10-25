# Render icons and supply control glyphs

Use `Monica.UI.Icons.MoIcon` for a standalone SVG and `MoIcons` for the shared catalog. Both ship in `Monica.UI`; there is no icon-specific module registration, provider, JavaScript, icon font, or runtime download. A host using the shell still follows the normal [shell setup](shell-and-access.md) for its application lifecycle and styles.

```razor
@using Monica.UI.Icons

<MoIcon Icon="@MoIcons.Search" />
<MoIcon Icon="@MoIcons.Copy" Size="16px" />

<MudButton StartIcon="@MoIcons.Save">@L["Actions:Save"]</MudButton>
<MudIconButton Icon="@MoIcons.Close" aria-label="@L["Actions:Close"]" />
```

`L` in these examples is the component's module-owned localizer, with the shown action keys present in its resources. An icon inside a labeled control is decorative; the button or link owns its accessible name, keyboard behavior, focus, disabled state, click handling, hit area, and tooltip.

| MoIcon parameter | Contract |
| --- | --- |
| `Icon` | Trusted SVG body fragment; null, empty, or whitespace produces no output. |
| `Size` | Optional CSS length such as `16px` or `1em`. Omitted sizing uses `--mo-icon-size`, with a `20px` fallback. |
| `Label` | Optional localized accessible name for a meaningful standalone image. Blank values mean decorative. |
| `Class`, `Style` | Presentation extensions on the root SVG. Color inherits through `currentColor`. |
| `AdditionalAttributes` | Unmatched SVG attributes, for example `data-*` hooks. Component-owned class, style, view box, fill, focusability, role, and label/hidden semantics take priority. |

MoIcon uses a fixed `0 0 24 24` view box and `focusable="false"`. By default it emits `aria-hidden="true"`. A nonblank `Label` instead emits `role="img"` and an encoded `aria-label`. Use `Label` only when the graphic independently conveys information; localize it through the owning module's resources. Adding a click handler to an SVG does not provide button semantics.

`MoIcons.Search` and the other catalog properties return SVG body strings without an outer `<svg>`. Use native `MoIcon` for standalone graphics and pass catalog fragments into the existing MudBlazor control when it owns interaction. The fragment inherits that control's color and dimensions. A `MudIconButton` using `MoIcons.Close` is still a MudBlazor button; it does not need a nested `MoIcon`.

Navigation registrations also accept these values directly:

```csharp
using Monica.UI.Icons;

registry.RegisterCategory(
    "example.tools",
    "Tools",
    icon: MoIcons.Sliders);
```

This literal example belongs in an existing module's `ConfigureNavigation(...)` callback; use localized registrations for application-facing labels as described in [shell and access](shell-and-access.md). Icon selection does not register a route or change access policy.

## Preserve size and inherited color

Outline fragments carry their paint settings in a shared group: `currentColor`, rounded caps and joins, and an initial stroke width of 1.5 SVG units. A surrounding stylesheet can set `--mo-icon-stroke-width` for a denser context; the variable also applies when a Mud component renders the fragment. It scales with the SVG: a 1.5-unit line at 16px is approximately 1 CSS pixel.

Native `MoIcon` uses `1em` width and height, with `font-size:var(--mo-icon-size,20px)`. Its `Size` parameter sets the local size variable; ordinary CSS font-size rules on the root can preserve existing component sizing. Use `Size="1em"` when the icon should scale with its surrounding text. When styling a child `MoIcon` from a parent's isolated stylesheet, scope a real HTML wrapper and use `::deep .mo-icon`.

Color inherits from the enclosing text or control. Set status color on the owner with the existing `--mud-palette-*` or approved Monica semantic tokens. `MoIcon` has no `Color` parameter; an intentional standalone override can use `Style="color:var(--mud-palette-error)"`. For a Mud control, continue using its supported `Color`, `IconColor`, or adornment color parameter and let its SVG inherit the result. Preserve existing disabled-state inheritance.

## Configure Mud control slots

The following public slots are available in MudBlazor 9.11. Set the relevant slots explicitly for the control being authored; these are per-instance parameters, not global replacements.

| Control | Public icon slots |
| --- | --- |
| `MudButton` | `StartIcon`, `EndIcon`. |
| `MudIconButton`, `MudMenu`, `MudMenuItem` | `Icon`. |
| Text/numeric inputs | `AdornmentIcon`, `ClearIcon`, where the control supports the corresponding adornment or clear action. |
| `MudSelect<T>` | `OpenIcon`, `CloseIcon`, `ClearIcon`; `CheckedIcon`, `UncheckedIcon`, `IndeterminateIcon` for selection; an explicit `AdornmentIcon` overrides the state-dependent arrow. |
| `MudAutocomplete<T>` | `OpenIcon`, `CloseIcon`, `ClearIcon`, and the inherited adornment slot. |
| `MudCheckBox<T>`, `MudRadio<T>` | `CheckedIcon`, `UncheckedIcon`, `IndeterminateIcon`. Use visibly different selection glyphs. |
| `MudAlert` | `Icon`, `CloseIcon`. An explicit `Icon` replaces automatic severity selection, so bind a matching glyph when `Severity` changes dynamically. |
| `MudExpansionPanel`, `MudNavGroup` | `Icon`; navigation groups also expose `ExpandIcon`. |
| `MudList<T>`, `MudListItem<T>` | List `CheckedIcon`/`UncheckedIcon`; item `Icon`, `ExpandLessIcon`, `ExpandMoreIcon`. |
| `MudDatePicker`, `MudDateRangePicker` | `AdornmentIcon`, `ClearIcon`, `PreviousIcon`, `NextIcon`; range pickers additionally expose `SeparatorIcon`. |
| `MudTimePicker` | `AdornmentIcon`, `ClearIcon`. |
| `MudPagination`, `MudTablePager` | `FirstIcon`, `BeforeIcon`, `NextIcon`, `LastIcon`. |
| `MudTableSortLabel<T>`, `MudTable<T>` | Sort-label `SortIcon`; table `CommitEditIcon`, `CancelEditIcon`. |
| `MudDataGrid<T>` | `SortIcon`, `DragIndicatorIcon`, `ColumnOptionsIcon`, `FilterIconEmpty`, `FilterIconFilled`, `FilterIconClear`. Columns also expose optional `SortIcon`. |
| `HierarchyColumn<T>` | `ClosedIcon`, `OpenIcon`. |
| `MudTreeView<T>`, `MudTreeViewItem<T>` | Tree `CheckedIcon`/`UncheckedIcon`/`IndeterminateIcon`; item `ExpandButtonIcon`, `LoadingIcon`, `Icon`, `IconExpanded`, `EndIcon`. |

The Shell configures snackbar severity glyphs centrally through `AddMudServices` and `SnackbarConfiguration.NormalIcon`, `InfoIcon`, `SuccessIcon`, `WarningIcon`, and `ErrorIcon`, using the shared catalog. Existing `ISnackbar` calls inherit those defaults. Hosts that replace the Shell's Mud service configuration must preserve their intended snackbar configuration themselves.

For example, preserve all checkbox states and use the shared catalog for pager navigation:

```razor
<MudCheckBox T="bool?" TriState="true"
             CheckedIcon="@MoIcons.SquareCheck"
             UncheckedIcon="@MoIcons.Square"
             IndeterminateIcon="@MoIcons.SquareMinus" />

<MudTablePager FirstIcon="@MoIcons.FirstPage"
               BeforeIcon="@MoIcons.ChevronLeft"
               NextIcon="@MoIcons.ChevronRight"
               LastIcon="@MoIcons.LastPage" />
```

The pager example belongs inside the table's `PagerContent`. Keep its existing paging settings and localized labels. Checkbox values and interaction callbacks belong to the consuming application.

## Private upstream glyphs

Some MudBlazor 9.11 glyphs cannot be overridden through the enclosing control's public parameters:

- `MudTablePager` exposes its navigation icons but internally creates a rows-per-page `MudSelect<int>` without forwarding arrow icon parameters.
- `MudDataGridPager<T>` hardcodes its navigation glyphs and internally creates its page-size selector; it exposes no corresponding icon slots.
- DataGrid filter panels, column-management popovers, and private menus contain hardcoded action glyphs beyond the public grid slots. Its generated selection checkboxes also do not forward custom selection icons.
- Table-generated selection checkboxes and row edit-start buttons use upstream glyphs; the table's public commit/cancel icon settings do not replace those private buttons.
- The snackbar provider does not forward a configurable close glyph to its internally created snackbar element; its central severity icon settings do not change that close button.
- Nested menu arrows and numeric-input spinner buttons also use private upstream glyphs without enclosing-control icon slots.

Passing `MoIcons` to supported slots therefore does not replace every icon rendered by MudBlazor. Keep this boundary visible when assessing a migration. Use an existing public content/template API when the application already owns that content; do not fork the vendor library, use reflection, or rewrite its rendered SVG paths to change private glyphs.

## Catalog sources and trusted input

The public catalog contains selected Lucide assets and Monica-authored glyphs, including the Shell drawings. Applications do not need npm or access to Lucide's servers. `MonicaIcon` remains the separately owned theme-aware brand mark.

Only pass reviewed developer assets to `Icon`. The component renders the fragment as markup; it does not sanitize uploaded SVG or arbitrary service responses. Custom application fragments must follow the same 24-unit body contract, including self-contained paint and no scripts, external references, or event handlers. For catalog maintenance, source imports, and visual review, follow MoIcon authoring in `$monica-ui-development` (`references/moicon-authoring.md`).
