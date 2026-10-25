# Embedded diagnostic text

Use `StackTraceViewer` to read and copy captured diagnostic text inside an existing Monica UI. Register its module in the host's normal composition callback:

```csharp
builder.AddMonica(monica =>
{
    monica.AddUIShell();
    monica.AddStackTraceUI();
});
```

The Web host keeps its normal `app.UseMonica()` and `app.MapMonica()` lifecycle. `AddStackTraceUI()` registers the stateless `StackTraceParser` once per host and contributes its own localization prerequisite and `SharedResource`. It does not add a page or require the entire shell. The example includes the shell because it supplies the ordinary rendering, theme and static-asset setup. A custom rendering host separately owns MudBlazor services/providers, the local theme/font assets, and `_content/Monica.UI/js/mo-clipboard.js`; the viewer borrows the host's `IJSRuntime` and never disposes it.

```razor
@using Monica.UI.UIStackTrace.Components

<StackTraceViewer Message="@capturedText" />

@code {
    private string? capturedText;
}
```

`Message` accepts stack-only text, a complete `Exception.ToString()` capture, or ordinary diagnostic prose. The caller owns capture, redaction, authorization and provenance. Passing text does not establish that it is safe to disclose, that an exception is current, or that its source was captured successfully.

## Read the complete source

The displayed evidence is one encoded stream of the original string. Highlighting is conservative: recognized exception, method and source-location substrings receive emphasis, while unmatched text, parameters, punctuation, blank lines, indentation, unknown suffixes and original newline delimiters stay in order. Input never becomes HTML. Null or empty input is a neutral absence state; whitespace-only input remains retained and copyable with an explanatory message.

The existing parser remains a best-effort recognizer. Its flat inner-marker indices, omitted blank parse rows and limited frame grammar do not describe a reliable cause tree or a complete frame count. The viewer does not reconstruct diagnostic text from those rows or invent inner-exception labels. A failed highlight attempt retains the complete source as ordinary text.

Wrapping starts enabled. The local **Wrap lines** control changes reading layout without changing source or copy payload. A changed ordinal `Message` resets wrapping to enabled; an unrelated rerender with the same source preserves the reader's choice. The viewer has one rounded, inset surface and no vertical height cap; its page or dialog owns vertical scrolling. Turning wrapping off intentionally gives the code pane horizontal scrolling, with the toolbar outside that scroller. Give an embedded owner `min-width: 0` and keep its available width; do not add another capped, padded, bordered trace wrapper or reach into the viewer's private token selectors.

## Copy and lifetime

**Copy** captures the exact string admitted by that click and passes it to the shared clipboard helper. Success is reported only after the helper returns `true`; refusal or interop failure produces a local manual-copy instruction. Browser and operating-system clipboard APIs can normalize line endings, so payload identity and native clipboard representation are separate boundaries.

One viewer admits one copy operation at a time. Replacing its source resets source-dependent feedback, and an old completion cannot publish onto a newer source, including an A → B → A replacement. Retirement stops new admission and observes the already admitted producer. It does not reverse a browser write that already occurred. The viewer owns no reset timer or imported JavaScript module.

The shared HTTP fallback borrows focus synchronously for its temporary textarea, removes that textarea on both success and failure, and restores a still-connected previous target only when the textarea retained focus. A copy-event handler that moved focus elsewhere keeps that choice. Clipboard permission or browser policy can still refuse copying; do not infer successful copy from an enabled button or from the presence of the Clipboard API.

## Existing callers and verification

The Observable registration inspector keeps its complete raw capture disclosure and optional formatted disclosure. Its captured identity, sequences, paging and capture-error evidence remain caller-owned; the shared viewer does not refresh a frozen inspector. Retained legacy exception/history components use the same viewer inside their existing expansion panels.

The DataChannel exception inspector keeps record selection and its native Evidence / Stack trace tabs. Its selected record's stack text is the supplied authority; it is not necessarily a complete `Exception.ToString()` capture. Missing component provenance stays missing, and retained counts do not prove runtime health or recovery.

Use the pure support and composition tests under `tests/Test.Monica.UI/UIStackTrace/` for source-range fidelity and direct/transitive module activation; shared clipboard admission/completion/retirement tests live under `tests/Test.Monica.UI/Shared/Support/`. These are non-rendering tests and need a targeted filter because the standard runner excludes UI projects. Browser acceptance must also exercise real callers, selectable final text, default wrapping and intentional unwrapped scrolling, native keyboard Copy and truthful failure, source replacement, readable theme roles, and narrow/short owners. A parser success or a no-overflow assertion alone does not prove visual readiness.
