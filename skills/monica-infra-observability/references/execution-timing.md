# Execution timing

`monica.AddExecutionTiming()` installs an ordered behavior on the shared typed execution pipeline for descriptors marked as business operations. It measures those operations wherever the pipeline is used, including a generic host; it is not HTTP request timing. If no business operation appears, check that requests execute through the Monica pipeline and inspect the effective plan with `ExecutionPipelineCatalogFacade` before adding ad hoc timing around controllers.

```csharp
monica.AddExecutionTiming(); // Inline aggregation, immediately queryable.
```

Inline aggregation is the default and makes completed statistics visible immediately. `UseBackgroundBatchAggregation()` queues samples and declares the HostedService dependency. Its background flush interval defaults to 250 ms; statistics queries also flush pending samples before reading the aggregate, so callers do not need to wait for the next timer tick. Choose that mode to reduce hot-path aggregation work:

```csharp
monica.AddExecutionTiming().UseBackgroundBatchAggregation();
```

Resolve `ExecutionTimingFacade` for `GetStatistics()` and `GetRunningOperations()` from application code. On Web hosts, `/execution-timing/statistics` and `/execution-timing/running` expose the same views only when the module's Minimal API switch is enabled (or the host default is enabled). `AddExecutionTimingUI()` adds a shell page and declares timing, localization, and shell dependencies; it is an optional view of the same collector. Statistics are per host, so use exported metrics or another external store for multi-instance history. The source of the behavior and query contract is `Monica.Profiling/Modules/ModuleExecutionTiming.cs`, `Monica.Profiling/ExecutionTiming/Facades/ExecutionTimingFacade.cs`, and `Monica.Profiling/Modules/ModuleExecutionTimingUI.cs`.

## Reading the UI evidence

Capture and invocation labels follow the shell's Clock-based page time display in `$monica-infra-ui` (`references/time-display.md`). Duration measurements and original collector/log payloads retain their precision.

The `/execution-timing` page reads the local facade independently of Minimal API exposure. `AutoRefreshIntervalMs` defaults to 2000 ms; a non-positive value selects manual-only refresh. Pausing stops page polling, not recording. Manual refresh still works while paused. A successful completed/running read pair replaces the displayed snapshot and capture time together; these are sequential queries, not an atomic collector transaction. A failed pair retains the last successful snapshot and reports the failure. Each page owns and drains its polling and in-flight reads on disposal.

The completed ledger searches full operation keys and display names, orders measured evidence, and pages the filtered results. Friendly names may collide: `OperationKey` identifies a completed aggregate, while `InvocationId` identifies each captured running invocation. The inspector preserves the selected capture, exact identities, recorded numeric values, and exact timestamp instants even if the page refreshes behind it. Filtering does not change the whole-snapshot summary or duration-bar denominator.

Interpret the measurements within their actual scope:

- Counts and means accumulate in the current host; completed samples include every recorded terminal completion, without an outcome field. The summary's **mean of key means** weights each operation key equally, rather than weighting by sample count.
- Rates use cumulative completions divided by minutes since the first completion, with a one-minute minimum denominator. They are not a rolling throughput window.
- Running membership and wall-clock elapsed age belong to the displayed capture. A paused view does not keep aging its rows.
- Allocation is optional: unavailable is distinct from zero. The collector does not expose a tracked-sample denominator, history, percentiles, or outcome-specific success rates. Do not infer those from the displayed fields.
