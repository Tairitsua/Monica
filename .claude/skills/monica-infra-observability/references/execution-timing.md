# Execution timing

`monica.AddExecutionTiming()` installs an ordered behavior on the shared typed execution pipeline for descriptors marked as business operations. It measures those operations wherever the pipeline is used, including a generic host; it is not HTTP request timing. If no business operation appears, check that requests execute through the Monica pipeline and inspect the effective plan with `ExecutionPipelineCatalogFacade` before adding ad hoc timing around controllers.

```csharp
monica.AddExecutionTiming(); // Inline aggregation, immediately queryable.
```

Inline aggregation is the default and makes completed statistics visible immediately. `UseBackgroundBatchAggregation()` queues samples and declares the HostedService dependency; its default flush interval is 250 ms, so a just-completed operation may not yet appear in the aggregate. Choose that mode to reduce hot-path aggregation work:

```csharp
monica.AddExecutionTiming().UseBackgroundBatchAggregation();
```

Resolve `ExecutionTimingFacade` for `GetStatistics()` and `GetRunningOperations()` from application code. On Web hosts, `/execution-timing/statistics` and `/execution-timing/running` expose the same views only when the module's Minimal API switch is enabled (or the host default is enabled). `AddExecutionTimingUI()` adds a shell page and declares timing, localization, and shell dependencies; it is an optional view of the same collector. Statistics are per host, so use exported metrics or another external store for multi-instance history. The source of the behavior and query contract is `Monica.Profiling/Modules/ModuleExecutionTiming.cs`, `Monica.Profiling/ExecutionTiming/Facades/ExecutionTimingFacade.cs`, and `Monica.Profiling/Modules/ModuleExecutionTimingUI.cs`.
