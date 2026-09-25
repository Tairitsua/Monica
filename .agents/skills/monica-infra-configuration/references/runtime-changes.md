# Inspect and change managed values

Register managed configuration with `AddConfiguration(plan)` using the [input plan and store](bootstrap-and-stores.md). `ConfigurationFacade` is the host-facing boundary for source inspection, candidate validation, mutation, history, rollback, and reload. Check each returned `Res<T>` before using its data; a persisted change can also report `PostCommitIssues` for reload or notification work after commit.

## Find the winning source

Use `GetDefinitionsAsync()` to discover definition keys, or `GetDefinitionStateAsync(definitionKey)` for one schema and its display-safe effective values. A definition key is [distinct from its section path](options-and-validation.md). For a local definition, `GetSourceChainAsync(definitionKey, LogicalPath.FromProperties(nameof(OrdersOptions.BatchSize)))` returns contributions in priority order and marks the effective source. `GetConfigurationSourcesAsync()` and `GetConfigurationSourceInventoriesAsync()` show the whole runtime stack; inventories may include unmanaged host values. Source-chain inspection is local to definitions scanned in this process. Sensitive display values are redacted. Inspect the chain before editing a lower-priority source, since that edit may not change the effective value.

## Apply a reviewed change

Use `ValidateCandidateValue(definition, scopePath, json)` to check a complete candidate before saving. For a single effective-store value, read `GetDefinitionStateAsync(definitionKey)` and pass its current schema version and effective document version to `MutateAsync`. For the `OrdersOptions` example in [options and validation](options-and-validation.md), after checking the returned state:

```csharp
using Monica.Core.Results;

var stateResult = await facade.GetDefinitionStateAsync("orders");
if (!stateResult.IsOk(out var state) || state is null)
    throw new InvalidOperationException(stateResult.Message);

var result = await facade.MutateAsync(new ConfigurationMutationRequest
{
    DefinitionKey = state.Definition.DefinitionKey,
    LogicalPath = LogicalPath.FromProperties(nameof(OrdersOptions.BatchSize)),
    MutationKind = ConfigurationMutationKind.Set,
    Value = ConfigurationStoredValue.FromJson("200"),
    ExpectedSchemaVersion = state.Definition.SchemaVersion,
    ExpectedValueVersion = state.EffectiveValueVersion ?? 0
});
if (!result.IsOk(out var change) || change is null)
    throw new InvalidOperationException(result.Message);
foreach (var issue in change.PostCommitIssues)
    Console.WriteLine(issue.Message);
```

Version zero means an observed missing effective document. A stale version is a conflict to review and retry, not a reason to force an overwrite. To edit a writable managed JSON source instead, use `GetSourceRevisionAsync(sourceKey)` and `MutateSourceAsync(ConfigurationSourceMutationRequest)` with `SourceKey`, `ExpectedSourceRevision`, definition key, logical path, schema version, and JSON value. For several reviewed changes, `ApplyMutationGroupAsync(ConfigurationMutationGroupApplyRequest)` accepts ordered commands with explicit effective-store or external-source targets; inspect each outcome and any post-commit issues. Do not assume different physical sources form one database transaction.

## Roll back and reload

Use `GetHistoryAsync(definitionKey, path)` or paged history to find a mutation. `PreviewHistoryRollbackAsync([historyId])` returns current target values and a `PlanToken`; after reviewing the preview, pass that token to `RollbackHistoryAsync(historyId, planToken)`. The same preview path supports selected histories or a mutation group. Unified versions are disabled by default; enable `UseUnifiedVersionControl()` with an explicit definition/category filter before expecting version capture or restore.

After an effective-store mutation commits, the facade's mutation service attempts targeted local projection reload and dispatches a versioned change signal through configured notifiers. A writable external-source mutation attempts a local full reload. Reload or notification failure does not undo the committed value; inspect `PostCommitIssues` before reporting that the change is live. `GetRuntimeReloadStatusAsync()` compares loaded and stored versions. Use `ReloadRuntimeConfigurationAsync()` for recovery or to load an out-of-band edit; it is not a mandatory extra call after every successful facade mutation. `BroadcastReloadAllAsync()` requests a local refresh and best-effort refresh of other processes.

For distributed invalidation, call `.UseEventBus()` on the Configuration registration and provide a default `IDistributedEventBus`, or pass a keyed distributed service key. The bridge automatically publishes effective-store change signals after facade mutations and sends explicit broadcast requests; it carries only invalidation metadata, never configuration values. All participants use the same topic (default `monica.configuration.reload`); ensure the EventBus provider and subscriptions work before diagnosing replica reload. A setting marked `RequiresRestart` or `StaticAfterStartup` is not made live merely by a successful projection reload.

When several services share a store, set a stable `ModuleConfigurationOption.PublisherKey` for each logical service and reuse it across that service's replicas. The default derives from the host application name. This identity helps reconcile published metadata and affected-service views; it is separate from the per-process reload `InstanceId`.

The optional `monica.AddConfigurationUI()` contributes state, storage, history, and version pages. It brings shell and localization dependencies; a Web host must complete `app.UseMonica()` and `app.MapMonica()`. `EnableAffectedServiceConfirmation` adds a point-in-time affected-service prompt for shared stores. It does not prove those services are live or reloaded.

Public operations: `Monica.Configuration/Facades/ConfigurationFacade.cs`; mutation lifecycle: `Monica.Configuration/Services/ConfigurationMutationGroupApplyService.cs`; request and source-chain contracts: `Monica.Configuration/Models/ConfigurationMutationRequest.cs`, `ConfigurationMutationGroupApplyModels.cs`, and `ConfigurationSourceChain.cs`. Bridge and UI: `Monica.Configuration.EventBus/Modules/ModuleConfigurationEventBusBuilderExtensions.cs`, `Monica.Configuration.EventBus/Modules/ModuleConfigurationEventBus.cs`, and `Monica.Configuration.UI/Modules/ModuleConfigurationUI.cs`. Checks: `tests/Test.Monica.Configuration/Services/ConfigurationSourceInspectorTests.cs`, `tests/Test.Monica.Configuration/Projection/MonicaConfigurationProviderPartialReloadTests.cs`, and `tests/Test.Monica.Configuration.EventBus/ConfigurationEventBusBridgeTests.cs`.
