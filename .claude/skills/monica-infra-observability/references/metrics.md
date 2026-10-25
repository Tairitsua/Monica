# Runtime metrics

Use Runtime Metrics to inspect the current process and its bounded local history. For exporting measurements or inspecting exact instrument/tag-set series, use [OpenTelemetry metrics](opentelemetry-metrics.md). These registrations have independent collection and retention; enabling the local Runtime Metrics page does not require OpenTelemetry.

For immediate memory and last-GC observations, optional allocation tracking, heap census, or explicit GC/dump actions, use [Memory analysis](memory-analysis.md). Its history route consumes the same local Runtime Metrics collector; these tasks do not require an exporter.

## Runtime Metrics

Register the collector and, for a Blazor host, its shell page inside `builder.AddMonica(monica => { ... })`:

```csharp
builder.AddMonica(monica =>
{
    monica.AddRuntimeMetrics(options =>
    {
        options.MaxHistoryPoints = 300;
        options.SampleIntervalMs = 1000;
    });
    monica.AddRuntimeMetricsUI(options => options.AutoRefreshIntervalMs = 2000);
});
```

`AddRuntimeMetricsUI()` requires Runtime Metrics through its module dependency and registers `/runtime-metrics` in the shell. An explicit `AddRuntimeMetrics(...)` call is useful when configuring collection; it is not an additional prerequisite for the UI registration. The host starts one process-local EventCounter collector and observable instruments. The host owns their lifetime; opening, pausing, or disposing a page does not start, pause, or dispose that collector.

| Option | Default | Effect |
| --- | --- | --- |
| `ModuleRuntimeMetricsOption.SampleIntervalMs` | `1000` | Interval of the collector's snapshot timer; configure a positive value. |
| `ModuleRuntimeMetricsOption.MaxHistoryPoints` | `300` | Maximum retained snapshot count; configure a non-negative value. This bounds points, not a guaranteed wall-clock window. |
| `ModuleRuntimeMetricsUIOption.AutoRefreshIntervalMs` | `2000` | Interval for that page to read retained history. Zero or negative values require manual refresh. |

The scoped `RuntimeMetricsFacade` exposes `GetTrend()`, `GetLatestPoint()`, and `SubscribeToUpdates(callback)`. Read their `Res<T>` status before consuming data. `GetTrend()` copies the currently retained point list and its capacity/sampling metadata without forcing a sample. It can succeed with an empty list before the first capture. `GetLatestPoint()` reads the separately cached latest sample and fails before any sample exists. A subscription receives captured points and returns an unsubscribe action; release the subscription with its consumer. These are local process APIs, not queries against an exporter or a metrics database.

The optional Web API is `GET /runtime-metrics/latest`, enabled through the host's Minimal API policy or the module's `EnableMinimalApi` override. The host default is `false`. This route returns the latest point through the facade envelope; it does not expose retained history. A generic host can collect and expose the facade without mapping this route. For a complete Web host, keep the normal `app.UseMonica()` and `app.MapMonica()` lifecycle.

Capacity and startup states have different meanings. With `MaxHistoryPoints = 0`, collection and the latest-point cache still advance, but history is trimmed to empty; the trend page therefore has no current retained point. With capacity `1`, one retained sample cannot establish a trend. Do not replace an empty successful history with a latest-point query or call an empty result a collector failure. The default capacity and interval are roughly five minutes at steady capture cadence, not durable retention.

### Collection time and view time

The collector snapshots cached `System.Runtime` counter payloads on its own timer. Counter reporting is requested at no less than one second, so a faster snapshot timer can repeat cached values. A sample's `Timestamp` is UTC capture time; it does not prove that every counter was newly reported at that instant. A field whose counter has not arrived is currently represented as zero, so zero alone does not establish counter availability.

The Runtime Metrics page reads only `GetTrend()`. Each successful read detaches the retained list and records a UTC view capture time. Its current point is the last retained sample; its previous point is the latest point from the previous successful **view read**, even when both reads return the same sample. A failed refresh retains the previous trend, points, and capture time while surfacing the read failure. An empty successful history remains distinct from an initial failure. Manual refresh and pausing affect view refresh only; host sampling and eviction continue, and resuming reads whatever is still retained. Do not label a per-view difference as the change between adjacent collector samples.

### Browse retained observations

The CPU and working-set plots use elapsed UTC time and the complete retained observations, with physical-size labels outside the SVG. Hover previews an observation; each plot's native sample browser provides Arrow/Home/End access and a touch selection path. Its accessible text includes the retained ordinal, exact UTC timestamp, value and unit. Escape dismisses the preview without moving focus. The paged ledger remains the path to every retained sample's frozen raw inspector and successful view-capture time.

Automatic reads preserve a browsed observation while its original collector object remains retained, even when its ordinal changes. If it leaves the window, the browser selects the first remaining observation without moving focus. A visible focused preview explains that fallback; Escape keeps a dismissed preview hidden through refreshes until further deliberate interaction. Pausing keeps the displayed history fixed while collection continues. Plot selection does not pause collection or change frozen inspector evidence. Plots, page refresh, and inspector dismissal have separate lifetimes; the Runtime plots add no JavaScript measurement or resize-observer work.

### Units and counter limits

`GcHeapSizeBytes`, `WorkingSetBytes`, generation sizes, and allocation values are byte-based fields. The compatibility `GcHeapSizeMB`, `WorkingSetMB`, `AverageHeapSizeMB`, and `MaxHeapSizeMB` values use decimal MB (`bytes / 1_000_000`), not MiB. If a display scales a byte rate by `1024` or `1_048_576`, label it KiB/s or MiB/s. CPU usage, time in GC, and fragmentation are percentages; `ThreadCount` is the ThreadPool thread count.

The collector currently caches each payload's `Mean`, otherwise `Increment`, otherwise `Count` directly. `AllocationRateBytesPerSecond` carries the reported allocation value without applying a reporting-interval normalization. Likewise, `Gen0GcCount`, `Gen1GcCount`, and `Gen2GcCount` carry cached reported increments, not lifetime totals or guaranteed counts for each snapshot interval. Do not rescale these values using the page refresh interval, sum repeated snapshots as independent GC events, or infer a reliable period total from them. Preserve this limit in labels and raw inspection; changing aggregation or normalization belongs to the collector contract, not a visual refactor.

Runtime Metrics also registers instruments on the `Monica.Profiling.Runtime` meter. These observe the cached latest point; they do not export its retained history. Select OpenTelemetry and an exporter separately when remote measurements are needed. For module instrumentation work, use `$monica-opentelemetry`.

Source ownership: `Monica.Profiling/Modules/ModuleRuntimeMetrics.cs`, `ModuleRuntimeMetricsUI.cs`, and `RuntimeMetrics/` establish registration, facade, sampling, and units. `UIRuntimeMetrics/State/` owns page view refresh and retained-read semantics; its non-rendering host tests live under `tests/Test.Monica.Profiling/UIRuntimeMetrics/`.

## OpenTelemetry metrics

Registration, exporters, local retained windows, exact series selection, histogram statistics and diagnostic copy formats are owned by [OpenTelemetry metrics](opentelemetry-metrics.md). For authoring module instruments rather than consuming them, use OpenTelemetry development in `$monica-opentelemetry`.
