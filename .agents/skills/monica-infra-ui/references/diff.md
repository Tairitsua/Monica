# Text comparison and embedded Diff viewers

Use `AddDiffHighlightUI()` inside the host's `builder.AddMonica(...)` callback to add the `/diff-highlight-debug` workspace. It requires the mixed DiffHighlight module, localization and shell through its module dependencies. Keep the host's normal `UseMonica()` and `MapMonica()` lifecycle and operational access policy. The UI module adds a debug-category navigation entry; it does not authorize otherwise restricted operational pages.

Use `AddDiffHighlight()` when only the facade/service or module-governed endpoints are needed. Minimal API exposure follows the host's existing module endpoint policy; registering the UI is not a reason to enable unrelated endpoints.

## Compare through the real service contract

`DiffHighlightFacade.HighlightAsync(original, updated, options)` returns a `Res<DiffHighlightResult>`. Check failure before reading its data. A successful result contains the full line models, side-specific numbers and character ranges, logical statistics, serialized output, options and processing duration. Statistics describe the complete comparison, including when a reader shows only one side.

| Setting | Built-in behavior |
| --- | --- |
| Module defaults | Line mode, HTML output, context request 3, character-detail maximum 1000, both ignore flags false. The debug workspace starts with Character mode. |
| Line | Separate added/deleted lines and matched unchanged lines. |
| Character / Mixed | Both currently use adjacent changed-line pairing with character details when eligible. Mixed does not imply a separate matching strategy. |
| Ignore whitespace | Trims leading and trailing whitespace for line equality; it does not remove or collapse internal whitespace. Returned source text remains raw. |
| Ignore case | Uses invariant case normalization for line equality. Returned text and character-range content remain raw. |
| Character limit | The configured maximum gates line pairing. The selected SimpleMyers algorithm additionally caps matching at 1000 UTF-16 code units per nonempty side. Raising the configured maximum does not lift that fixed cap. Longer changed lines retain line-level added/deleted evidence. |
| ContextLines | Retained in options but currently ignored by the built-in algorithms and renderers. All lines are available; do not promise context trimming. |
| Output | HTML, Markdown source and plain text. Serialized Markdown/plain text are strings, not a request to execute embedded markup. |

Deleted character ranges use UTF-16 positions in the original text; added ranges use positions in the updated text. A final inserted/deleted suffix is part of the returned ranges. Positions do not describe grapheme clusters, and repeated characters can admit more than one valid LCS alignment. Do not assume a particular prefix match when the original string occurs more than once.

The selected algorithm uses quadratic LCS matching. `HighlightAsync` schedules real work without a computation-cancellation contract. Do not advertise a Cancel action or progress percentage. The module's custom-renderer registration is not consumed by the current service renderer factory; do not rely on that option to replace built-in output.

## Embed a computing viewer

Register `AddDiffHighlightUI()` in the normal Monica UI host and use the existing component inputs:

```razor
@using Monica.UI.UIDiffHighlight.Components
@using Monica.UI.UIDiffHighlight.Models

<DiffViewer OriginText="@original"
            NewText="@updated"
            Options="@options"
            ViewMode="EDiffViewMode.Split" />

@code {
    private string original = "before";
    private string updated = "after";
    private readonly DiffHighlightOptions options = new() { Mode = EDiffHighlightMode.Line };
}
```

`Options = null` uses configured module defaults; `AutoRefresh` defaults to true. The viewer owns comparison work for its visit and borrows its scoped facade. Keep Configuration's prepared JSON, redaction and rollback direction in Configuration; the viewer compares the supplied strings rather than reconstructing those snapshots.

Changed draft text or settings and re-enabling `AutoRefresh` request comparison. With `AutoRefresh = false`, changes mark retained evidence as belonging to earlier supplied inputs; call and await `RefreshDiffAsync()` to compare deliberately. Each request captures text, options and styles by value. Actual work runs serially, with the latest pending draft replacing older pending requests. An obsolete completion cannot replace current evidence. A failed attempt retains the previous whole result and reports the failure separately. Both-empty inputs clear embedded evidence without invoking the facade.

`ViewMode` defaults to Unified. A genuinely changed parent value resets the reader layout; unrelated parent rerenders preserve the reader's local choice. HTML readers offer direct Unified/Split and original/updated display choices without recomputation. Side-by-side rows share logical row height and stack locally below the reader's own width threshold. Markdown and plain text remain encoded literal output. The shared reader has a 38rem reading bound; the standalone workspace uses continuous shell scrolling. The result-only `DiffResultPresentation` defaults to neutral localized provenance captions: "Content compared" for current inputs and "Inputs changed — showing the last comparison" for earlier inputs. Optional `CurrentInputsLabel` and `EarlierInputsLabel` parameters supply caller-owned contextual captions without changing currentness or comparison behavior; null preserves those defaults. The standalone workspace supplies its draft captions explicitly.

Disposal stops admission and drains already admitted noncancelable comparison work. The scoped facade remains borrowed. Page-only sample loading, draft editors and compact focus behavior belong to the workspace rather than the shared computing wrapper. The page compares both-empty drafts deliberately and distinguishes that accepted empty pair from an uncomputed state.

For authoring a comparison UI, use the UI development in `$monica-ui-development` and working-interface language in `$monica-ui-development` (`references/monica-workbench-design-language.md`) contracts. Inspect accepted source/options separately from current drafts, and verify complete encoded line text and paired-row geometry in real consumers.
