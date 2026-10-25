# Memory analysis and host diagnostics

Use Memory Analysis to inspect the current host process, distinguish immediate memory from last-GC evidence, explore its retained runtime history, or request an explicit diagnostic action. It is process-local inspection, not a remote metrics store or an application health decision.

## Register the view or backend

The smallest UI registration is inside the host's normal Monica callback:

```csharp
builder.AddMonica(monica =>
{
    monica.AddMemoryAnalysisUI();
});
```

`AddMemoryAnalysisUI()` requires Memory Diagnostics, Runtime Metrics, Localization and the shell. It registers the Memory branch beneath Monitor. No OpenTelemetry exporter, database or external broker is required. A complete Blazor Web host still uses `app.UseMonica()` and `app.MapMonica()`; see shell composition and access in `$monica-infra-ui` (`references/shell-and-access.md`) for page access policy and the full host lifecycle. The Memory registrations currently select no page access-policy type, so the shell's global operational policy does not automatically protect them. Enforce the host's required access boundary; diagnostic options are not authorization.

For backend-only use, register `monica.AddMemoryDiagnostics()` and resolve the scoped `MemoryDiagnosticsFacade`. Its observation methods are `GetCurrentSnapshot()` and `GetGcDetails(GCKind.Any)`. Check each `Res<T>` before using its data. The diagnostic service/provider are host-owned singletons; a caller borrows the scoped facade and does not own those singleton lifetimes. Runtime history requires its separate `AddRuntimeMetrics()` registration, described in [metrics](metrics.md).

| Route | Task and evidence |
| --- | --- |
| `/memory-analysis` | Immediate process/managed memory, last-GC heap/committed values, lifetime collection counts and runtime GC configuration |
| `/memory-analysis/gc` | Last collection identity, reported pause, heap and memory-load context, generation before/after movement |
| `/memory-analysis/history` | Retained Runtime Metrics memory/allocation observations, actual timestamp plots, a complete paged sample ledger and frozen sample inspection |
| `/memory-analysis/allocations` | Optional allocation workspace with distinct ETW tick traffic and heap census sources |
| `/memory-analysis/diagnostics` | Explicit current-host collection/dump actions and their actual outcomes |

The allocations destination appears in navigation only when selected through the fluent feature method:

```csharp
builder.AddMonica(monica =>
{
    monica.AddMemoryAnalysisUI().EnableTypeAllocationTab();
});
```

The retained `EnableTypeAllocationTab()` name now selects the allocations route and requires `ModuleTypeAllocation`; it does not start collection. A direct route without that feature reports unavailable instead of resolving an absent panel factory. Register `monica.AddTypeAllocation(...)` as well when configuring its host options. `StartAutomatically(mode)` explicitly adds host startup collection and the HostedService dependency; opening or leaving a page does not implicitly start or stop it.

| Option | Default | Meaning |
| --- | --- | --- |
| `ModuleMemoryAnalysisUIOption.AutoRefreshIntervalMs` | `2000` | Overview, GC and history view reads. Non-positive intervals select manual-only reads. |
| `AllowManualGC` | `true` | Permits the page state to admit manual collections; configure false for observation-only UI. |
| `AllowGcDump` | `true` | Permits page-state dump admission; the native tool and server permissions remain necessary. |
| `EnableTypeAllocationTab` | `false` | Selected through the fluent method, rather than setting its internal setter. |
| `ModuleTypeAllocationOption.AutoStartCollection` | `false` | Selected through `StartAutomatically(...)`. |
| `DefaultSamplingMode` | `High` | Auto-start's legacy strategy alias; Low/High are not different implemented fidelity levels. |
| `MaxTrackedTypes` | `500` | Must be nonnegative. Bounds the retained live tick ranking; zero preserves cumulative totals without retaining type rows. |
| `AutoStopAfter` | `10 minutes` | Stops each started live session after the configured duration. Non-null values must be from 1 through 4294967294 milliseconds; null requires explicit stop or host retirement. |

## Collection ownership and successful view reads

`MemoryAnalysisPageState` admits one read or diagnostic action at a time. A refresh reads the memory snapshot, GC details and runtime trend sequentially, then publishes detached results and a UTC successful view capture time together. This is a grouped **view read**, not an atomic backend transaction: a GC or runtime capture can occur between the three calls. Preserve each observation's source timestamp and basis.

An actual read failure preserves all three previous observations and their capture time. `LoadError == null` means no outstanding failure; an empty actual error message still represents failure. Before a successful read, evidence is missing rather than zero. A successful empty runtime history is valid and does not invalidate the immediate memory/GC observations. A zero GC index reports that no completed collection is available; it must not be presented as a measured generation-zero collection.

Pausing affects the page loop only. Runtime collection and retention eviction continue independently; manual refresh remains available and reads whatever evidence is currently retained. Non-positive intervals keep both the effective `AutoRefresh` and `CanAutoRefresh` false. Each overview, GC or history route owns a fresh state from `MemoryAnalysisPageStateFactory.Create()` and drains its admitted work/loop on retirement without disposing borrowed facades or stopping host collectors. Concurrent or retired calls return an inert Conflict and do not replace evidence/errors.

Diagnostics uses `Create(false)` to start with page polling off even when a positive interval is configured. It performs an initial evidence read and subsequent explicit reads; it does not create unused background chart polling. A caller creating custom UI can use the same explicit initial preference. The optional page `TimeProvider` owns view/action UTC times and polling, independently of the Runtime Metrics collector's sampling clock.

## Interpret the source and units

| Evidence | Actual source and interpretation |
| --- | --- |
| Working set | `Environment.WorkingSet` at the read: process resident memory, not managed heap or a configured memory limit |
| Total managed memory | `GC.GetTotalMemory(false)`: managed heap excluding fragmentation, without forcing a GC |
| Snapshot heap/committed values | `GC.GetGCMemoryInfo()`: last-GC evidence, not a new heap census at the snapshot timestamp |
| Gen 0/1/2 collection counts | `GC.CollectionCount(...)`: lifetime process counts, unlike Runtime Metrics' cached reported increments |
| Collection identity and generation rows | `GC.GetGCMemoryInfo(GCKind.Any)`: the last reported collection and its before/after memory values |
| `PauseDuration` | The larger of the first two reported pauses in the current implementation, not their sum or total collection elapsed time |
| `PauseTimePercentage` | Running GC pause percentage since runtime load, updated when a GC completes; not this collection's fraction |
| Available memory/load/threshold | Last-GC memory context, not an independent current OS free-memory measurement |
| Generation `CollectedBytes` | `SizeBeforeBytes - SizeAfterBytes`: signed net size movement, not an exact freed-object count; zero/negative movement is valid |

Raw diagnostics sizes are bytes. Displays that divide by 1024 use KiB/MiB/GiB; retained runtime heap `...MB` values use decimal MB (`bytes / 1_000_000`). Keep signed byte movement, raw integers, UTC observation identity and explicitly qualified local offsets inspectable. GC ratios, finalizer counts and fragmentation do not establish a host severity threshold by themselves.

The history route uses the same bounded local collector described in [Runtime Metrics](metrics.md#runtime-metrics). Capacity zero, capacity one, startup waiting, cached counter reuse and collector-reported allocation/GC-increment limits retain that contract. Its latest generation composition is one retained sample, not an aggregate or a separate generation history. Do not force a sample, replace successful empty history with a latest-point call, rescale allocation by page polling, or sum repeated reported increments.

Memory history plots use component-owned SVG geometry without JavaScript resize callbacks. Each finite retained sample keeps its original UTC timestamp and raw value in native point evidence; the x coordinate follows actual elapsed time rather than row position. Non-finite measurements leave gaps instead of being converted to zero. Heap and allocation plots keep independent decimal-MB and binary-MiB/s scales. The complete native sample ledger remains the keyboard-accessible path to exact frozen inspection.

## Explicit collection and dump actions

`AllowManualGC` and `AllowGcDump` are enforced at **page-state admission**, as well as in controls. A denied action returns Forbidden without invoking its backend; these UI-host options do not restrict an independent backend facade caller or establish route authorization.

The page requests collection through `ForceGarbageCollectionAsync(generation)`. Its production facade defaults are blocking and non-compacting; a negative generation selects all generations, a nonnegative request is capped by `GC.MaxGeneration`, and blocking collection waits for pending finalizers. It affects the current host process, not only the page, and is an explicit operation rather than an observation refresh.

`CreateGcDumpAsync()` calls the native `dotnet-gcdump collect` tool for the current host PID. The tool must be installed and discoverable on the **host process PATH**, its diagnostics channel must be usable, and the host identity must be able to write the destination. [The tool requests a full generation-two collection](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump#dotnet-gcdump-collect); this operation can suspend the host. Its absence is different from a native permission/startup failure, and an unsuccessful exit preserves both output streams and the exit code in the failed result.

The default output is a collision-resistant `gcdump_<pid>_<UTC timestamp>_<unique id>.gcdump` filename under the server OS temporary directory. The facade also accepts an explicit server output path when called directly. Relative paths resolve against the host working directory, and `.gcdump` is appended unless the extension is exactly lowercase `.gcdump`, matching the tool's destination convention. The provider passes that entire normalized absolute path as one argument. It drains both redirected streams while the tool runs, awaits exit and both streams, and reports success only after exit zero and a nonempty output file. The successful `Res<string>.Data` contains that actual absolute file path; it does not prove a complete heap graph. No browser download endpoint or artifact-storage service is implied by a returned path; server retention and transfer belong to the host/operator.

`LastAction` records the original result status/message, requested generation when relevant, UTC start/completion times and actual successful server dump path. A successful action attempts a subsequent view read, but that read cannot turn the action into a failure: `LoadError` separately reports a failed evidence refresh and prior view evidence remains available. Failed actions do not run a follow-up read. Busy admission does not erase previous action evidence. The dump facade adds no cancellation token or operation timeout; the native tool retains its own collection-timeout policy. Page retirement drains an admitted provider call and suppresses late UI publication rather than claiming the tool was cancelled.

## Allocation traffic and heap census are different sources

`TypeAllocationFacade` exposes current/history snapshots, explicit start/stop/reset, subscription with an unsubscribe action, and heap capture. Live start/stop/reset controls the host-owned collector shared by circuits. Subscribers marshal their own UI work, unsubscribe on retirement, and schedule lifecycle commands after the callback returns: synchronous lifecycle re-entry from a provider notification is rejected. Subscriber failures are logged independently. A page drains its own admitted work on retirement; it does not stop shared collection merely because navigation closes the workspace. Retain selected heap evidence independently of incoming tick updates or a live refresh.

The following provider contracts require their actual platform/native prerequisites; registering the optional feature is not proof of native availability:

| Source | Measurement and prerequisites |
| --- | --- |
| Windows CLR ETW `GCAllocationTick` | Current-PID observed tick count and sampled type-attributed allocation volume. The singleton applies provider PID filtering and a callback PID guard. Windows trace-session/provider permission is required; actual start failure reports unavailable. No EventPipe fallback is implemented. |
| Windows PSS clone inspected through ClrMD | Enumerated non-free valid heap objects and bytes grouped by type. Requires Windows 8.1/Server 2012 R2 or later, a managed runtime, matching readable DAC and a walkable heap. The operation inspects a clone of the current process, serializes admitted scans, disposes runtime/target resources and has no passive live-attach or Linux full-dump fallback. |

An ETW tick is not one allocated object. The type is the allocation crossing the runtime tick threshold; its attributed volume can include other contributing allocations. Low and High remain compatibility aliases for that same tick stream: do not promise 1%/10% sampling, distinct event-rate limits, precision or overhead. Count/bytes labels must use the source meaning; dividing tick volume by ticks does not yield average object size and cannot identify LOH placement.

A PSS heap walk enumerates objects; it does not traverse roots to prove reachability, force a GC to create a live set, or establish exact allocation traffic. Its timestamp is capture completion and its duration includes clone/runtime loading/walk work. Heap object averages are meaningful for that census, but the existing average-size-based LOH flag is still a heuristic rather than recorded heap membership. Never combine the two sources' totals or relabel one as the other.

ETW totals and bounded history survive stop/start until `ResetData()`. Snapshot duration describes the current/last session window, or its window since reset, so cumulative totals may include earlier sessions outside that duration. Do not divide those totals by the last session duration to invent a session rate. Before any collection, the source is `None`; active reads use their UTC read time, while stopped evidence retains its stop time and recorded strategy. `TypeAllocationFacade.CurrentMode` is Disabled when no session is active even though the retained snapshot's `SamplingMode` still identifies its original alias.

The workspace retains detached live and heap snapshots with separate adoption times. Selecting an empty heap source does not perform a capture; a successful explicit capture selects the resulting census. Live updates and refresh/reset remain in the live cache and cannot replace that census. Workspace reset requires inactive collection and preserves heap evidence; the backend facade's reset owns the shared live counters/history. The compatibility `TopN` property controls rows per native page, with every retained matching type reachable through paging and search over full type identity.

Keep idle/no ticks, collecting without observations, stopped retained traffic, heap evidence and provider failure distinct. The module's default auto-stop is ten minutes; null has no hidden fallback and leaves stopping to an explicit action or host retirement. A non-null duration must be from 1 through 4294967294 milliseconds, including both endpoints. Stop/shutdown drains the session's admitted processing/publication, retains final evidence and original strategy/duration, and prevents a retired session from mutating its successor. The provider excludes `System.Runtime.*` types by default from both tracked rows and totals. `MaxTrackedTypes` accepts zero for totals-only capture, retaining cumulative count/bytes without type rows; negative values are invalid. Evicted ranking rows remain in cumulative totals, while the current UI page shows only part of the retained ranking. Even the full retained ranking can therefore sum below 100%; do not renormalize it into a complete process-allocation distribution.

`DroppedEventCount` means current-process ticks skipped for unknown type names or invalid byte payloads. It does not count excluded types, ranking eviction or ETW transport loss; session loss is not exposed by this model. Payload v2+ uses the reported 64-bit allocation amount, while older events use the unsigned 32-bit payload. Invalid modern values are rejected rather than clamped or guessed. Missing/skipped evidence is not proof of an exact loss percentage or zero transport loss.

Source ownership: `Monica.Profiling/Modules/ModuleMemoryDiagnostics.cs`, `ModuleMemoryAnalysisUI.cs`, `ModuleTypeAllocation.cs`, `MemoryDiagnostics/` and `TypeAllocation/` own registration, provider/action prerequisites and measurement contracts. `UIMemoryAnalysis/State/` owns view refresh, admission and retained evidence; `Pages/UIMemory*.razor` owns the route family. Use source and actual result envelopes to diagnose platform/tool availability; no successful native capability is implied by these registration examples.
