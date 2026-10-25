# OpenTelemetry signals and local metrics

Use SDK exporters for measurements leaving the process. Use Monica's in-process collector and `/opentelemetry-dashboard` to inspect exact instruments, normalized tag sets and their retained observations in the current host. These are separate consumers of published .NET instruments: SDK aggregation/export intervals and the local retained window do not share an aggregation contract. They can coexist. Neither the local page nor the snapshot endpoint provides durable or cross-instance storage.

For process-counter history, use [Runtime Metrics](metrics.md); for adding module instruments, use [OpenTelemetry development](../../monica-opentelemetry/SKILL.md).

## Register the required consumer

Inside `builder.AddMonica(monica => { ... })`, choose an exporter explicitly:

```csharp
builder.AddMonica(monica =>
{
    monica.AddOpenTelemetry()
        .UseOtlpExporter();
});
```

`AddOpenTelemetry()` wires metrics and ASP.NET Core, HttpClient and runtime instrumentation. Its custom meter patterns default to `Monica.*`; no exporter is enabled by default. `UseOtlpExporter(endpoint?)` enables OTLP for metrics and any explicitly enabled traces/logs, using the SDK's endpoint configuration when no endpoint is supplied; its transport defaults explicitly to gRPC. `UseConsoleExporter()` enables console output for those same enabled signals. `UsePrometheusEndpoint(path: "/metrics")` enables only the SDK Web metrics scrape exporter. `AddMeter(pattern)` adds a custom meter name or wildcard pattern. A generic host can collect and use configured non-Web exporters without mapping Web endpoints.

Traces and logs are opt-in. `UseTracing("Orders.Application")` subscribes to the supplied `ActivitySource` names and, by default, ASP.NET Core and HttpClient spans. It does not invent a source name or alter application retry policy. `UseLogging()` includes formatted log messages and scopes by default; the shared resource name, version and deployment environment apply to all signals. Host-owned SDK providers own shutdown and exporter lifetime.

For a host configured entirely through the SDK's `OTEL_EXPORTER_OTLP_*` configuration, select `UseOtlpExporterFromEnvironment()` instead. It enables all three signals and uses the SDK's cross-signal exporter registration, including its own protocol and per-signal configuration defaults. It is mutually exclusive with `UseOtlpExporter`; do not separately register signal-specific OTLP exporters. Add custom trace sources through `UseTracing(...)` as needed. The host decides whether the environment calls for this exporter; an endpoint value alone does not make Monica enable it.

When combined with Monica Logging, opt into `ForwardToProviders` so Serilog delivers filtered events to the OpenTelemetry logging provider. Registering a provider alone does not enable Serilog forwarding. For example:

```csharp
builder.AddMonica(monica =>
{
    monica.AddLogging(options => options.ForwardToProviders = true);
    monica.AddOpenTelemetry()
        .UseTracing("Orders.Application")
        .UseLogging()
        .UseOtlpExporterFromEnvironment();
});
```

Console Serilog output and an OpenTelemetry console log exporter are separate destinations; choose them deliberately to avoid duplicate console output. Sensitive query-path filtering is owned by [Logging](logging.md).

OpenTelemetry automatically requires Monica's localization and result envelope modules; the latter also requires JSON serialization. The optional snapshot endpoint uses this host-owned JSON/result contract, so a minimal host does not need to add these dependencies separately. Hosts can configure those modules explicitly to customize their shared wire and error-presentation contracts.

For an existing Monica Blazor host, the smallest local workspace registration is `monica.AddOpenTelemetryUI()`. That UI module requires OpenTelemetry and enables its in-process collector. Configure that collector explicitly when changing its subscriptions or capacity:

```csharp
builder.AddMonica(monica =>
{
    monica.AddOpenTelemetry(options =>
        {
            options.MeterPatterns = ["Orders.*"];
            options.SamplesPerSeries = 600;
            options.MaxTagSetsPerInstrument = 200;
        })
        .UseInProcessCollector()
        .UseInstrumentationMetersInProcessCollector(false);
    monica.AddOpenTelemetryUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
```

Keep the host's normal Blazor setup; see [UI integration](../../monica-infra-ui/SKILL.md). `AddOpenTelemetryUI()` registers `/opentelemetry-dashboard` under Monitor. It does not assign a page access policy; the host owns authentication and any required access restriction. Local facade/page use does not depend on the HTTP snapshot route or an exporter.

`UseInProcessCollector()` without the UI enables the singleton hosted collector and scoped `OpenTelemetryFacade`. `GetSnapshotAsync()` returns a `Res<OpenTelemetrySnapshot>`; check its status before reading the graph. Successful empty instruments or empty series are valid startup/admission states. It reads existing evidence and does not force observable callbacks. The optional Web route is `GET /opentelemetry/snapshot`. OpenTelemetry's mapped Web endpoints, including the SDK scrape endpoint, follow the module's `EnableMinimalApi` override or the host's `EnableMinimalApiByDefault`, which defaults to `false`. Keep `UseMonica()` and `MapMonica()` in the Web lifecycle.

`Application:AutonomousWork=false` holds Monica-configured OTLP export and reports that decision in resource diagnostics. In-process collection, local console output and explicitly requested Prometheus scraping remain available. Custom exporters registered outside Monica own their participation policy.

`ResourceServiceName` and `ResourceServiceVersion` fall back to the application project name and version; `DeploymentEnvironment` defaults to `Deployment:Scope` and adds its resource attribute when nonempty. The three built-in instrumentation flags default to `true`. `UseInstrumentationMetersInProcessCollector(false)` changes only the local collector's extra subscriptions; SDK instrumentation remains enabled. Normalized non-empty custom patterns are used by both consumers. An empty normalized pattern list means **all published meters** to the local matcher; it does not mean disabled collection and does not add an equivalent SDK wildcard subscription.

## Retention and measurement boundaries

| Option | Default | Actual local effect |
| --- | --- | --- |
| `SamplesPerSeries` | `600` | Retained observations per exact tag-set series. Values below one retain one observation; snapshots report this effective capacity. |
| `MaxTagSetsPerInstrument` | `200` | Maximum admitted distinct tag sets per published instrument. Non-positive values admit none; snapshots report zero. |
| `ObservableInstrumentInterval` | `1 s` | Local listener's observable callback cadence. Non-positive values use one second. It is independent of page polling and SDK collection. |
| `IncludeInstrumentationMetersInProcessCollector` | `true` | Adds local subscriptions implied by enabled built-in instrumentation flags. |

Direct instruments record when the application emits a measurement; they do not wait for the observable timer. Each retained point has the collector's UTC observation time. The ring buffer evicts oldest points per series, so sample capacity is not a guaranteed duration. Existing admitted series continue updating after the tag-set limit is reached. `DroppedSamples` counts rejected **observations** for additional tag sets, not distinct rejected tag sets; `Overflow` indicates that this count is nonzero. The limit does not bound the number of published instruments in the registry. Instrument evidence remains retained for the collector lifetime, including instruments whose owning meter was later disposed.

The local snapshot builds its instrument/series graph from individually synchronized reads. Its `TimestampUtc` is the snapshot-build time; it is not a shared atomic measurement instant across instruments or tag sets. Preserve each series' `LastUpdatedUtc` and each point's `TimestampUtc` when comparing evidence.

`InstrumentId` is an opaque identity assigned to one published instrument object and stable only within that collector lifetime. It distinguishes separate instruments even when names and metadata match. It is not a durable, cross-host or cross-restart key. `MeterVersion` is the version declared by the meter and may be null.

Tags are normalized to ordinal string keys and invariant string values; original CLR tag-value types are not preserved. Duplicate keys take the last value. A length-prefixed `TagSetSamples.Key` distinguishes normalized tag sets without delimiter collisions, including empty strings and control characters. An untagged series is a real series, and its empty key is distinct from a tag whose value is empty.

### Values, units and deltas

Direct counters and up/down counters store running totals of admitted deltas for each series. Gauges, observable instruments and histograms retain their reported measurement values. `CurrentValue` is the latest retained value. `Delta` subtracts the two latest retained values and is zero with fewer than two observations; it is neither a rate nor the difference between successful page reads. The instrument-level `CurrentValue` and `Delta` properties sum their tag sets without timestamp alignment. Such a sum is not a meaningful physical aggregate for every gauge or histogram; inspect independent series instead.

Every stored numeric value is binary64 (`double`). Integer and decimal measurements are converted before retention, so integer precision beyond binary64's exact range and original decimal precision are not recoverable from this API. NaN and infinities remain raw observations and can propagate through running totals, deltas or instrument sums. Do not replace them with zero, infer missing-counter availability from zero, or derive a rate using the page interval.

The instrument's declared unit owns the measurement meaning. Raw plots and inspection preserve that unit. Presentation of `By` scales bytes with binary factors and labels B/KiB/MiB/GiB/TiB; `By/s` uses the corresponding binary rate labels. A declared `s` may display as microseconds, milliseconds or seconds. `%` remains the reported percentage value; the formatter does not multiply a fraction by 100. Unknown or absent units do not justify inferred conversions. This differs from Runtime Metrics' compatibility decimal-MB fields; retain the source-specific contract.

### Histogram windows and non-finite evidence

Histogram statistics describe the **finite retained observations in one selected series**, not lifetime totals, SDK histogram aggregates or combined tag sets. `Count` is the number used; `NonFiniteSampleCount` reports retained exclusions. Mean, min/max and interpolated P50/P95/P99 use those finite values. Buckets use the retained min/max range: ordinarily ten separate, noncumulative buckets; equal values produce one bucket. The first bucket includes both bounds; later buckets exclude the lower and include the upper bound. Eviction can change every statistic.

An all-non-finite or empty retained window has `Histogram = null`. The series' `FiniteSampleCount` and `NonFiniteSampleCount` still explain its raw history. The plot includes every finite observation at its actual UTC x-coordinate, including duplicates. Non-finite points break line segments rather than becoming zeros or a line bridging unavailable values; the paginated ledger and frozen inspector retain their exact raw evidence.

## Page refresh, selection and retirement

The page defaults to five-second view polling and offers manual, one-second and five-second choices. Manual mode freezes the adopted view while host collection and eviction continue. Explicit Refresh reads current retained evidence without forcing a measurement. The state accepts positive polling intervals in the timer's inclusive `1–4294967294 ms` range; null or non-positive means manual mode. Unsupported positive intervals are rejected before changing the preference.

A successful read adopts a detached read-only graph and records `SnapshotCapturedAt`, the successful view-adoption UTC time. This differs from snapshot-build and observation times. A failed refresh retains the previous graph, selection and view time while exposing the actual error; an empty error message is still a failure, while null means none. A busy or retired read returns Conflict with an empty message without replacing evidence or creating a false source failure.

The first populated matching read selects its first actual instrument and series once. Registry meter filtering uses exact ordinal equality; instrument-name filtering uses a case-insensitive substring. Changing filters can clear an excluded selection. Explicit selection admits only current adopted object references, so queued clicks from older views cannot select a new equivalent object accidentally. On refresh, a nonempty instrument ID must match exactly one current instrument. Legacy snapshots without an ID reconcile only a unique full metadata match, including meter version. Series reconciliation requires both the exact key and ordinal tags. Absent or ambiguous matches clear selection without choosing a replacement, and deliberate clears remain clear.

The sample inspector captures the selected instrument, series, original sample index, unchanged point, snapshot-build time and successful view time together. Refresh, sorting, paging and retention eviction do not update an open inspector. A stale series/index request is rejected. The full ledger provides exact sample inspection; plot markers are supporting evidence rather than hundreds of keyboard targets.

Each page owns one polling loop and permits one admitted read. Interval transitions stop and drain the old loop before starting another. Disposal retires notifications, dialogs, clipboard work and rendering, and drains admitted page work; it does not stop or dispose the scoped facade or singleton host collector. Repeated state disposal shares one completion, and late operations cannot restart polling. `StateChanged` is a synchronous `Action` scheduling notification; component owners observe and drain their asynchronous rendering tasks. Existing subscribers using the former `Func<Task>` event must adapt to that ownership contract.

## Diagnostic copies

The copy buttons export the **whole adopted snapshot**, independent of registry filters, selected series or current pagination. They copy text to the clipboard; they do not create a download, send measurements to an exporter or reread the collector.

The JSON summary contains resource and instrument metadata, collector identity, normalized tags, current/delta values, retained/finite/non-finite counts and available histogram statistics. It deliberately omits the retained sample arrays. Finite values remain JSON numbers; NaN, positive infinity and negative infinity are the strings `"NaN"`, `"Infinity"` and `"-Infinity"`. The source DTOs also permit these named strings on raw point values and current/delta numeric fields without changing global host JSON settings. Consumers must handle this explicit number-or-named-string representation.

For exact clipboard series identity, use `keyUtf16` and `tagEntries`, whose entries contain `keyUtf16` and `valueUtf16`. Each encoding is `u` followed by four uppercase hexadecimal digits per original UTF-16 code unit; the empty string is `u`. Entry order is ordinal by normalized raw key. These fields distinguish invalid Unicode code units that JSON's readable `key` and `tags` strings replace during encoding; readable property names alone can become ambiguous. The in-process graph preserves raw ordinal identity. HTTP snapshots serialize `TagSetSamples.Tags` keys verbatim, independently of the host's dictionary-key naming policy, preserving ordinal distinctions such as `A` and `a`. String encoding still follows the host serializer's Unicode behavior, which may replace unpaired UTF-16 surrogates; ordinary HTTP snapshots do not provide the clipboard's encoded identity fields.

The Prometheus-style clipboard copy uses fixed `monica_diagnostic_*` **gauge** families for local current/delta values, retained counts and histogram-window summaries/bucket counts. Instrument/resource metadata and encoded tag identity stay in labels. User tag-label keys encode UTF-16 code units, avoiding family/label-name collisions from arbitrary names. Missing legacy IDs use an export-local ordinal labeled `instrument_id_source="snapshot"`; it is not a persistent identity. Non-finite values use Prometheus `NaN`, `+Inf` and `-Inf`. The separate bucket-count gauges are noncumulative retained-window evidence, not native cumulative histogram `_bucket` families. Use the SDK Prometheus endpoint for actual scrape/export semantics.

## Diagnose by ownership

If exports contain no measurements, verify the chosen exporter, instrument publication and SDK meter subscriptions. If only the local registry is missing an instrument, inspect its local pattern selection and extra-instrumentation switch. An instrument with no series may have no admitted observation yet or an effective zero tag-set capacity. A series with one point has no adjacent difference/trend; all-non-finite samples explain a missing finite plot or histogram. If only `/opentelemetry/snapshot` or the SDK scrape endpoint is absent, inspect the Minimal API policy and required consumer registration separately from facade/page collection. Pausing the page cannot pause the host's instrument producers or restore evicted samples.

Source ownership: `Monica.OpenTelemetry/Modules/ModuleOpenTelemetry.cs`, `InProcessCollector/Models/` and `InProcessCollector/Services/` establish registration, local identity, normalization, retention and histogram calculations. `Monica.OpenTelemetry.UI/Modules/ModuleOpenTelemetryUI.cs` owns route composition; `UIOpenTelemetry/State/` and `Support/` own detached view adoption, exact selection and copy/plot contracts. Non-rendering behavior tests belong in `tests/Test.Monica.OpenTelemetry/`. The reference host at `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` composes the SDK Prometheus exporter.
