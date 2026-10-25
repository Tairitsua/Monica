# JSON viewing and editing

Use `Monica.UI.Shared.Components.Json.MoJson` for JSON evidence and drafts. It belongs to the shared `Monica.UI` library; feature modules do not need a separate JSON module or editor package. A normal Monica UI host supplies localization, theme assets, interactivity and `MoClipboard.copyText` through its shell. All assets are local.

```razor
@using Monica.UI.Shared.Components.Json

<MoJson Value="@snapshotJson" Label="@L["Payload"]" />

<MoJson @bind-Value="draftJson" ReadOnly="false" Rows="12"
        Label="@L["Arguments"]" Disabled="@saving"
        ValidationChanged="OnSyntaxChanged" />

@code {
    private string draftJson = "{}";
    private bool syntaxValid;
    private void OnSyntaxChanged(MoJsonValidation result) => syntaxValid = result.IsValid;
}
```

`Value` is the exact caller-owned source. The default read-only mode displays an indented projection; the toolbar switches to raw source without publishing `ValueChanged`. Copy forwards the original source, including whitespace, rather than the projection or line numbers; the platform clipboard can normalize line endings. Set `FormatOnDisplay="false"` when physical source layout is evidence, such as a configuration file. Use `AllowPlainText="true"` for content that legitimately includes non-JSON tool output or state-store values: JSON is highlighted, other content stays encoded plain text with a neutral status. Do not use this option to hide errors in a JSON-only editor.

Set `ReadOnly="false"` to edit. The native textarea retains selection, undo, IME and normal Tab navigation; it scrolls horizontally rather than wrapping. Syntax colors and the optional gutter track its scroll. No library intercepts keyboard shortcuts. Explicit Format and Minify commands change the bound draft only when it is valid JSON; invalid and empty drafts remain intact. Formatting preserves numeric lexemes and duplicate properties, and keeps Unicode readable. Text is always Razor-encoded, including values containing HTML; the formatting encoder never turns source into trusted markup.

Syntax validation is strict `System.Text.Json` parsing with its default depth limit of 64. Comments, trailing commas and additional root values are rejected. `ValidationChanged` publishes changed syntax evidence on initialization, external value replacement and local edits. `MoJsonValidation` distinguishes valid JSON, empty input and invalid input, with parser details and **one-based UTF-8 byte positions**, not character columns. A scalar such as `null`, a string or an array is valid JSON. Callers still own schema/root-shape validation, form admission, access checks, redaction, save behavior and persistence; this component does not join `MudForm` or submit a draft. In particular, retain the configuration module's redaction baseline and analyzed-draft gates.

The default toolbar includes format/raw, line-number visibility and copy; read-only mode also offers wrapping. Editing offers format and minify. `ShowLineNumbers` defaults to true, `WrapLines` to false and `FormatOnDisplay` to true. These set initial/local presentation state and resynchronize when their parameter value changes. `Rows` defaults to 10 and clamps to 2–40 for editing. `MaxHeight` defaults to `420px`; overflow stays inside the code region. `ShowToolbar` and `ShowStatus` default to true. Hide chrome deliberately for a nested preview, rather than restoring module-specific highlighting or formatting controls. Keep `Label` and `Placeholder` localized at the caller.

The component owns its scroll enhancement and clipboard admission. Retirement stops new commands, drains initialization and the actual clipboard producer, then releases its JavaScript references. A missing enhancement leaves an ordinary usable textarea; copy feedback reports failure rather than claiming success. No browser state or network editor service is required.

Non-rendering tests under `tests/Test.Monica.UI/Shared/` cover source fidelity, strict parsing, Unicode byte positions, numeric preservation, token spans and clipboard ownership. Filter these tests explicitly; do not run the bUnit rendering suite unless requested. Browser verification should cover native editing and invalid drafts, format/minify, exact-source copy, external replacements, line-number and wrapping toggles, long content scroll synchronization, theme contrast and narrow owners. Check real module callers as well as an isolated fixture.
