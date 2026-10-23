# Logging and local inspection

Add `monica.AddLogging()` inside the host's `AddMonica(...)` callback to install Monica's Serilog provider during builder composition. It clears existing logging providers, so decide at composition time whether this host should use it. Console and file sinks are enabled by default; set `EnableFileSink = false` for a console-only deployment. `ConfigureLogger` adds filters, enrichers or sinks after the native setup without owning logger creation or disposal. `CustomLoggerFactory` replaces the default Serilog setup and bypasses `ConfigureLogger`. Keep endpoint, sink, and credential settings in host configuration.

```csharp
monica.AddLogging(options =>
{
    options.EnableFileSink = false;
    options.EnableTraceIdEnricher = true;
});
```

`AddRequestResponseLoggingMiddleware()` is an opt-in Web feature on the logging registration. It runs before routing and can log request and response bodies; decide whether those bodies should be captured before enabling it. Its `disableRequest` and `disableResponse` parameters allow one side to be excluded. Operator exception logs receive the full exception independently of [response diagnostic visibility](../../monica-infra-hosting/references/exception-diagnostics.md).

## File location and host composition

Use native file options instead of rebuilding the async sinks. `FileSizeLimitBytes` defaults to one GiB, `RollOnFileSizeLimit` to `false`, `RollingInterval` to `Infinite`, `RetainedFileCountLimit` to `31`, and `RetainedFileTimeLimit` to `null`. Nullable limits can disable the corresponding constraint. Retention is enforced by the file sink when opening or rolling files, not by a background age-based cleanup task.

`SensitiveQueryStringPathPrefixes` defaults to empty. Explicit prefixes exclude ASP.NET Core hosting diagnostic events that contain a non-empty query string and match the path exactly or below a slash boundary, case-insensitively. This supports transports carrying credentials in queries, including encoded parameter names; it drops the whole matching hosting event, not arbitrary application logs or ordinary requests without queries. Keep the sensitive path list aligned with authentication configuration.

`ForwardToProviders` defaults to `false`. Enable it to forward filtered Serilog events to other host-owned `ILoggerProvider` instances, such as `AddOpenTelemetry().UseLogging()`. Register those providers after Logging's builder phase; Monica OpenTelemetry does this during service composition. The host owns their disposal. Avoid a second sink exporting the same events to the same destination.

`LogFileLocation.Resolve(ModuleLoggingOption)` owns the default writer path. A nonblank `LogFilePath` wins over `LogDirectory` and `LogFileName`. Otherwise the absolute path is `<LogDirectory>/Logs/<LogFileName>`, using `AppContext.BaseDirectory` when the directory is blank. `LogDirectory` is the base of the `Logs` child, not the final sink directory. The file name defaults to the entry assembly name with a `.log` extension. The default Serilog writer, UI catalog, and UI reader use this same resolution contract.

For a Web host that needs interactive local inspection, add `monica.AddLoggingUI()` in the same callback. It requires Logging, ResultEnvelope, localization, and the shell. `ModuleLoggingUIOption.LogDirectory` explicitly overrides the UI inspection directory without relocating the writer; the default reader then uses the writer's file name inside that override directory. A custom logger factory can write to a different location or format, so configure the inspection directory and choose the actual file explicitly. File inspection reads local files; it does not subscribe directly to `ILogger` events or provide external log aggregation. Choose the host's UI access policy through [Monica infrastructure UI](../../monica-infra-ui/SKILL.md).

```csharp
monica.AddLoggingUI(options =>
{
    options.DefaultFetchLines = 500;
    options.MaxDisplayLines = 5000;
    options.PollingInterval = TimeSpan.FromSeconds(1);
});
```

| Option | Default and effective behavior |
| --- | --- |
| `DefaultFetchLines` | 500 latest complete physical lines initially; requests are clamped to at least one and the effective display limit. |
| `MaxDisplayLines` | 5,000 visible lines; values below one become one. Raw retention is twice the configured limit, at least two and capped at `int.MaxValue`. The default retains up to 10,000 raw lines. |
| `PollingInterval` | One second. Must be positive and at most 4,294,967,294 milliseconds. Positive values below one millisecond are scheduled as one millisecond. Invalid values fail reader construction. |
| `DefaultOnlyCapture` | `true`; when a keyword is active, the view initially shows only keyword matches. Severity remains an independent filter. |
| `PresetLineCounts` | 200, 500, 1,000, 2,500, and 5,000; selected counts still obey the effective display limit. |
| `LogDirectory` | Unset; the directory containing the resolved writer file. An explicit value becomes the UI's allowed inspection directory. |

The catalog lists ordinary files recursively beneath its inspection directory, including files with other extensions. Relative paths preserve nested folders. Symbolic-link/reparse-point descendants are excluded from enumeration and rejected on opening; rooted paths and directory traversal outside the configured boundary are rejected. `LogFileDescriptor.LastModified` is UTC, and catalog file sizes are bytes; the page formats binary sizes with KiB/MiB/GiB labels. An existing empty directory produces a successful empty catalog. A missing or unavailable configured directory returns a failed `Res`, without a replacement catalog. The built-in catalog page retains its previous entries and successful read time alongside that failure. Custom callers must likewise check the result before replacing their previous evidence; the stateless query service does not cache a previous catalog for them.

## Reader routes and ownership

`/logging-monitor` is the source-qualified file reader. `/logging-monitor/files` is the independent file catalog with search, sorting, pagination, Open, and Download. `?file=<escaped-relative-path>` selects a file in the reader; links between the pages carry that source path. The catalog does not start following or mutate a reader buffer. Navigating between these two routes preserves the circuit's current reader, source, retained evidence, filters, and following state. Leaving the logging workspace retires that reader; returning creates a fresh reader rather than restoring an abandoned live session.

The singleton `LoggingService` is the host facade and factory. The scoped `LoggingWorkspaceSession` owns an independent `LogReadingSession` for one Blazor circuit; only the reader and catalog components acquire that workspace owner. `GetReader()` returns the same reader while navigation stays on those exact routes, ignoring query/fragment text and honoring the application's base path. Reader components borrow the reader and own only their local subscriptions and browser work, so temporary component removal during shell access checks does not discard the workspace. The owner records first successful initialization; `MarkInitialized(reader)` ignores stale completion from a detached instance. Before deciding whether a requested route can reuse the current source, components await `LogReadingSession.SynchronizeAsync`: this serialized barrier observes previously admitted source mutations without reading the file or changing the snapshot version. A synchronous stale snapshot must not let late adoption from a previous component override a newer route's source intent.

The workspace owner observes `NavigationManager.LocationChanged`. Leaving the reader/catalog routes immediately detaches the current reader and tracks its asynchronous retirement. Reentry creates a new reader and initialization state. Cached workspace disposal unsubscribes its borrowed navigation manager and drains the current reader and every still-retiring reader at circuit disposal. Custom callers of `LoggingService.CreateSession()` still directly own and must dispose their returned session.

Each session has its own selected source, tail cursor, filter, retention, following loop, and subscriptions. Readers in different tabs or circuits cannot replace each other's files, pause each other, or change each other's keyword results. The shared `LogFileQueryService` is stateless; sessions borrow it and the host's logging provider.

A new reader starts paused. `InitializeAsync` reads the latest window; `RefreshAsync` polls once even while paused; `StartAsync` enables periodic following. The start request's cancellation token controls admission, while the session lifetime owns the continuing loop. `PauseAsync` cancels and drains that loop. `SwitchToFileAsync` prepares an independent candidate cursor, adopts a successful read, and drains old following before publishing the new paused source. Missing files are an explicit successful missing state. A failed or canceled candidate does not replace the previous source or retained evidence; cancellation after following has been stopped can leave the previous reader paused.

Mutations are serialized. Candidate cursors prevent a canceled read from advancing the adopted cursor and silently losing appended text. Cancellation propagates as `OperationCanceledException`, rather than successful empty data. `DisposeAsync` seals admission, cancels and drains admitted work and following, then completes subscriptions; repeated calls observe the same retirement task. New operations and subscriptions after retirement throw `ObjectDisposedException`. A `LoggingSubscription` is caller-owned and must also be disposed; disposing it alone does not stop following. Its bounded channel coalesces old snapshots, so it is a current-view notification channel rather than a lossless event stream.

## Physical lines, filters, and frozen evidence

Readers decode UTF-8 and publish only LF-terminated physical lines. A CR immediately before LF is removed; the first UTF-8 BOM is omitted from the first line. Incomplete text and split UTF-8 bytes remain pending across polls until the newline arrives. Polls bound their temporary complete-line queue to the effective raw retention limit, while counting every complete physical line and preserving the retained tail's exact absolute numbering. This is a line-count bound, not a byte bound on an exceptionally long unfinished physical line; pending bytes are never silently truncated. Blank and whitespace-only lines retain their original text. Exception continuation lines remain separate physical lines, usually with `Unknown` parsed severity; the reader does not invent structured events or assign their parent's level. Malformed UTF-8 follows the decoder's replacement behavior, and other encodings require a compatible writer or separate tooling.

Session-produced `ScreenLogSnapshot` instances are detached immutable evidence. They carry the observed source, monotonic view version, last successful read time (`GeneratedAt`), following state, existence, error, complete file-line count, retained range, visible lines, and all retained raw lines. Visible counts, retained counts, and total complete lines observed in the file are distinct. Appending can evict older retention; loading earlier history can evict newer retention and leave gaps. Every retained line keeps its exact absolute physical number, so a first/last retained range is not a promise that all intervening lines are present.

Framework-produced snapshots and context results expose read-only detached collections. The public model types also permit caller-supplied `IReadOnlyList` values: that interface or a record `with` copy does not freeze a mutable collection supplied by custom code. A standalone `ScreenLogBuffer` snapshot has no source identity or populated file-read metadata; its `GeneratedAt` describes buffer changes rather than a verified file read. Use a session snapshot when source generation, existence, or read age matters.

Keyword matching is ordinal and case-insensitive across the full verbatim physical line, including its header. Capture-only mode restricts the view to matches; otherwise matching rows are highlighted within the visible window. Message highlight segments describe the parsed message portion only. Severity filtering independently matches the actual parsed severity and never substitutes a level name as a message keyword. The `WithFilter`, `WithPosition`, and `WithContextTarget` line methods return copies; callers must use the returned value. Applying filters or loading history cannot mutate a previously captured inspector or export.

Custom UI integrations must migrate previous in-place helper calls: `ScreenLogSnapshot` and `LogLineViewModel` are now records, and calling a `With...` helper without assigning its result no longer changes the original line. For example, retain `line = line.WithPosition(number, bufferIndex).WithFilter(filter)`. Also account for full-line ordinal keyword matching, UTC file modification times, the lower effective capacity bounds, and cancellation exceptions when adopting these public contracts.

A source identity contains the full path, relative path, and an opaque observed generation. Switching sources and detected truncation/replacement invalidate that generation and restart physical numbering. Detection uses length, previously observed prefix/end bytes, and Windows creation time. Unix creation timestamps may fall back to modification/status-change times, so they do not invalidate a generation during an append; byte and length checks remain active. This is not an operating-system file identity: some same-length in-place rewrites outside those sampled bytes can remain undetected. Reads are bounded to the file length when opened, not filesystem-atomic archive snapshots.

`LogLineRequest` freezes the source, physical number, and verbatim target text. `ReadContextAsync` verifies the current observed generation and actual target text before returning detached surrounding complete lines; source switches, unavailable/changed content, or a changed target fail instead of reusing its number in a different file. Appending is allowed. Context inspection does not replace the reader buffer or stop following; its frozen context remains visible independently of subsequent reader updates. The legacy absolute-path `LogFileQueryService.ReadContextAsync` remains directory-contained but has no source-generation proof and should not be used by reader pages.

The reader's cursor menu captures the admitted physical line and callbacks before awaiting an action. Copy uses that retained line; filtering, position navigation and context inspection retain their existing source checks. Every admitted right-click advances the opening revision, so an older completion cannot dismiss a replacement menu. The shared [cursor-menu contract](../../monica-infra-ui/references/shell-and-access.md) owns native transitions and page coordinates; it does not replace the reader session, source-generation validation, or inspector ownership.

Missing (`FileExists = false`), existing but empty, and failed/stale reads are different states. Read failures retain the last successful source, lines, and read time while exposing `Error`; a later successful read clears it. Parsed log timestamps are separate evidence from the reader's read time.

## Export and HTTP contracts

| Operation | Content and ownership |
| --- | --- |
| `LogReadingSession.ExportBufferAsync()` | A frozen copy of all retained raw lines belonging to that reader, independent of visible filters or pagination. UTF-8 with LF separators and no metadata headers; incomplete pending text is excluded. |
| `LogReadingSession.ExportSnapshot(snapshot)` | Only the visible physical lines in the supplied frozen snapshot, using the same UTF-8/LF normalization. |
| `GET /logging-ui/files/{*filePath}` | Original file bytes through a caller-owned live handle, preserving BOMs, existing terminators, and unfinished text. The endpoint owns/disposes the handle and copies at most its opening extent. A concurrent shrink can shorten the response; no fixed content length or immutable archive guarantee is advertised. |
| `GET /logging-ui/current/export` | The separate legacy host buffer, with existing timestamp/filter/retention headers and `temp-logs-<timestamp>.log` naming. It never exports an interactive page session. |

Existing `LoggingService.InitializeAsync`, `StartAsync`, `PauseAsync`, filter, source-switch, and subscription methods deliberately continue operating on the separate host-owned legacy reader. The host container retires it. Interactive reader pages use their own session and browser export; they never seed or replace the legacy export buffer. It can therefore remain empty when only interactive pages have been used. Normalized reader exports are text artifacts, not byte-for-byte copies of the original file; use file Download when original bytes are required.

Legacy exports retain platform line separators and the existing `Keyword`/`OnlyCapture` filter header when no severity is configured; explicit severity adds its own metadata field. Reader exports instead capture separate UTF-8/LF byte arrays. Those `LogExportResult.Content` arrays belong to the caller: later reader changes cannot alter them, but they remain mutable byte arrays rather than write-protected artifacts.

The optional `GET /logging-ui/files` catalog endpoint follows the inherited Minimal API switch. File downloads and the legacy buffer-export endpoint are required UI endpoints and remain mapped when optional Minimal APIs are disabled, subject to Monica's endpoint port policy. They are excluded from API description. Requests pass `RequestAborted` to listing/open/export and stream copying. Browser file-download paths escape individual relative-path segments; page retirement cancels owned fetches and releases imported JS handles.

If a reader is empty, first distinguish missing, existing empty/incomplete, no filter matches, and a failed read. Check file-sink enablement, the resolved sink path, any UI directory override, and the selected relative file. For a console-only host, use its console or external collector rather than expecting this file reader to receive application log events.

Sources: [Logging module/options](../../../Monica.Logging/Modules/ModuleLogging.cs), [shared file location](../../../Monica.Logging/Services/Support/LogFileLocation.cs), [UI registration/endpoints](../../../Monica.Framework.UI/Modules/ModuleLoggingUI.cs), [workspace ownership](../../../Monica.Framework.UI/UILogging/State/LoggingWorkspaceSession.cs), [reader lifecycle](../../../Monica.Framework.UI/UILogging/State/LogReadingSession.cs), [UTF-8 cursor](../../../Monica.Framework.UI/UILogging/Support/LogTailService.cs), and [catalog/download boundary](../../../Monica.Framework.UI/UILogging/Support/LogFileQueryService.cs). Non-rendering host/file and ownership checks belong to `tests/Test.Monica.Framework.Logging`; verify layout and native interactions in the bridge browser. Concurrent admission checks alone do not prove a deterministically held mid-read handoff race; use a controllable read boundary when that stronger concurrency proof is required. Add or run bUnit rendering tests only on explicit user request.
