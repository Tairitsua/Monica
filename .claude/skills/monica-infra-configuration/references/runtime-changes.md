# Inspect and change managed values

Register `AddConfiguration(plan)` using the [input plan and store](bootstrap-and-stores.md). `ConfigurationFacade` owns inspection, preview, mutation, history, rollback, and reload. Check each returned `Res<T>`; a failed apply can carry a structured rejection in its original `Data`, and a persisted operation can report `PostCommitIssues`.

## Find the winning source

Use `GetDefinitionsAsync()` to discover definition keys or `GetDefinitionStateAsync(definitionKey)` for a schema, effective document version, and display-safe values. A definition key is [distinct from its binding section](options-and-validation.md). Source inspection is local to scanned definitions. For the aliased Orders example, `GetSourceChainAsync("orders", LogicalPath.FromProperties("minimum"))` returns contributions in priority order and marks the effective source. Use configuration aliases in paths, not `nameof(OrdersOptions.MinimumBatchSize)`.

`GetConfigurationSourcesAsync()` and `GetConfigurationSourceInventoriesAsync()` expose the runtime stack; inventory includes unmanaged host values by default. Sensitive display values are redacted. A lower-priority edit may be masked and leave the effective value unchanged. For external JSON targets, read `GetSourceRevisionAsync(sourceKey)` before staging a write.

## Preview a complete group before saving

`ValidateCandidateValue(definition, scopePath, json)` checks the portable rules for that scope. It always returns Fragment scope and SchemaOnly coverage, including when the scope is the root. `IsSchemaValid` can permit editing or staging; it is not authorization to save an aggregate. Sibling-dependent rules require the complete, target-aware group API.

Read the schema, value version, and intended physical targets, then call `PreviewMutationGroupAsync(ConfigurationMutationGroupApplyRequest)`. The following submits paired edits to the owner in [options and validation](options-and-validation.md):

```csharp
using Monica.Configuration.Models;
using Monica.Core.Results;

var stateResult = await facade.GetDefinitionStateAsync("orders");
if (!stateResult.IsOk(out var state) || state is null)
    throw new InvalidOperationException(stateResult.Message);

var request = new ConfigurationMutationGroupApplyRequest
{
    Label = "Change order batch range",
    Commands = [Set("minimum", "30"), Set("maximum", "40")]
};
var previewResult = await facade.PreviewMutationGroupAsync(request, cancellationToken);
if (!previewResult.IsOk(out var preview) || preview is null || !preview.CanApply)
    return; // Show the safe failure or all reports and problems; retain the draft.

var applyResult = await facade.ApplyMutationGroupAsync(request with
{
    ExpectedValidationFingerprint = preview.ValidationFingerprint
});
if (!applyResult.IsOk(out var applied) || applied is null)
{
    var rejection = applyResult.Data?.ValidationPreview;
    return; // Display the original structured rejection when present and request a fresh review.
}
foreach (var outcome in applied.Outcomes)
    Console.WriteLine(outcome.Status);
foreach (var issue in applied.PostCommitIssues)
    Console.WriteLine(issue.Message);

ConfigurationMutationCommand Set(string alias, string json) => new()
{
    RequestId = Guid.NewGuid().ToString("N"),
    DefinitionKey = state.Definition.DefinitionKey,
    LogicalPath = LogicalPath.FromProperties(alias),
    MutationKind = ConfigurationMutationKind.Set,
    Value = ConfigurationStoredValue.FromJson(json),
    ExpectedSchemaVersion = state.Definition.SchemaVersion,
    ExpectedSchemaHash = state.Definition.SchemaHash,
    Target = new ConfigurationEffectiveStoreMutationTarget
    {
        ExpectedVersion = state.EffectiveValueVersion ?? 0
    }
};
```

`facade` is the injected `ConfigurationFacade`, and `cancellationToken` belongs to the preview caller. Reuse the same commands and their RequestIds for apply. Version zero means an observed missing document. A stale version, source revision, schema, executable revision, or contributing aggregate requires a fresh review, not a forced overwrite. `ExpectedValidationFingerprint` is an opaque token binding the reviewed commands and contributing baseline; do not reconstruct or parse it. Complete mutation fingerprints are process-scoped, so refresh after a host restart or replica change. Apply always validates again, including when a caller omits the token.

Use `ConfigurationExternalSourceMutationTarget { SourceKey, ExpectedRevision }` for a writable managed JSON source. The complete preview places its changed contribution at the source's existing provider priority. Set and remove can reveal fallback values; unshadowed array indexes can still come from lower providers. Appending a candidate as the highest provider or JSON deep-merging files would produce a different validation result.

`Preview.Definitions` can contain multiple reports for the same definition. `Target=StoredDocument` validates the complete effective-store document independently, since it can later become visible when an override disappears. `Target=EffectiveAggregate` validates the resulting production view, including higher-priority contributions and defaults. Both must pass. External JSON sections can intentionally be partial: touched contributions receive structural checks and their resulting complete effective view receives object checks; a partial file is not required to satisfy isolated sibling-dependent rules. `HasEffectiveChange=false` on an effective report explicitly identifies a masked or otherwise ineffective edit. It does not waive stored-document validation. Inspect every report and `Problems`, or use `CanApply`.

## Persistence and independent adoption

The initial complete preflight rejects invalid, SchemaOnly, Failed, stale, or unprovable groups before values, histories, group creation, reload, notifications, or unified capture. Group rejection has `Status=Rejected`, a null `MutationGroup`, and structured `ValidationPreview`; the facade preserves that data in a failed result. Operational faults also return safe structured findings when available. Read the original result's `Data` rather than using a success-only output variable. Do not infer a persisted version or parse failure messages into metadata.

Ordered commands for one effective-store definition are staged into one final document and saved once with one version increment. Each command retains its history and outcome, sharing that final version. Thus 10..20 can become 30..40 without saving an intermediate 30..20 document. Different definitions or physical sources retain their persistence-provider boundaries; this does not create a distributed transaction.

A supported JSON batch replaces one physical file atomically. Distinct files and the effective store can become visible independently, and watcher adoption order can differ from write order. The preflight proves every nonempty combination of changed provider contributions per aggregate under the actual provider stack. If any combination is invalid, the group is rejected even when its final reports are valid. More than eight changed independent provider boundaries for one aggregate are conservatively rejected as `UnprovableAdoption` before persistence. A target with no faithful provider position is also blocked. Prefer one complete store document or one supported file batch when sibling rules require simultaneous changes.

Persistence rechecks contributing values, provider instances/order, contracts, effective-store versions, and physical JSON revisions. These checks detect observed drift and permit the group's own already-proved adoption states; they do not prevent all out-of-process races. A persistence failure after earlier independent boundaries committed can produce partial outcomes and post-commit issues. Low-level store/provider writes and out-of-band edits are not advertised as validated facade operations.

Each complete preflight reads and freezes the physical JSON files and compares their production projection with the loaded contributions for affected definitions and their ancestors. For locally discovered affected owners, the persisted effective-store version must match the loaded Monica projection version; an absent document or loaded version is compared as zero. A present document's production projection must also match its loaded provider contribution, so equal versions alone do not establish agreement. A mismatch at either boundary adds `RuntimeOutOfSync` to `Problems` and blocks preview/apply before persistence. The preview leaves runtime loading unchanged. Call `ReloadRuntimeConfigurationAsync()`, then obtain and review a fresh complete preview before retrying.

`MutateAsync` and `MutateSourceAsync` remain convenience operations for one command and use the complete apply coordinator. Use the explicit group preview/apply path when paired edits or full structured review are required.

## Roll back under the current contract

Find records with `GetHistoryAsync(definitionKey, path)` or paged history. `PreviewHistoryRollbackAsync([historyId])` returns the current physical target values, complete `ValidationReports`, `Problems`, `CanApply`, and the existing `PlanToken`. Apply a valid preview through `RollbackHistoryAsync(historyId, planToken)`; selected histories and mutation groups use the same preview contract. A previous schema-valid historical value is still checked against current object code and contributing siblings. Rejection does not create an audit mutation or reload.

Unified versions are disabled by default. Enable `UseUnifiedVersionControl()` with explicit inclusion filters. `PreviewUnifiedVersionRollbackAsync(version)` exposes target validation, adoption problems, and `CanApply`. Pass a `ConfigurationUnifiedVersionRollbackRequest` to `RollbackUnifiedVersionAsync`, setting its required `Version` and the returned `PreviewFingerprint`. Known current-schema invalid, incomplete-authority, or failed targets block the whole operation rather than being skipped as historical incompatibilities. Existing skips for unknown definitions or hard-incompatible historical structure remain distinct. Review every target and refresh a stale preview; an old preview cannot authorize changed values or code contracts. Remote ObjectCode/Unknown definitions without local authority cannot be repaired by trusting published CLR names or by using a nonexistent owner-validation transport.

## Reload and operator UI

After an effective-store mutation commits, the coordinator attempts targeted local projection reload and versioned notification. A writable external-source mutation attempts a full local reload. Failures do not undo committed values; inspect each outcome and `PostCommitIssues` before declaring the change live. `GetRuntimeReloadStatusAsync()` compares loaded and stored versions. `ReloadRuntimeConfigurationAsync()` recovers from a missed or out-of-band change; it is not required after every successful facade save. `BroadcastReloadAllAsync()` requests local and best-effort remote refresh. Source report caches and observed options attempts have [separate lifetimes](options-and-validation.md); reload does not replace an existing `IOptions<T>` value or guarantee every consumer has adopted the change.

For distributed invalidation, call `.UseEventBus()` on the Configuration registration and provide a default `IDistributedEventBus`, or select a keyed distributed service. The bridge publishes invalidation metadata rather than values; all participants use the same topic, default `monica.configuration.reload`. Set a stable `ModuleConfigurationOption.PublisherKey` per logical service and reuse it across replicas; its default is the host application name. This identity is distinct from process reload `InstanceId`.

`monica.AddConfigurationUI()` contributes state, storage, history, and version pages with shell and localization dependencies. A Web host completes `app.UseMonica()` and `app.MapMonica()`. Fragment editors label their limited checks; save and rollback previews require complete validity. State diagnostics separate the current source snapshot from the latest observed default-options attempt; unobserved is not a green success. Rejected saves retain staged edits and display typed findings. `EnableAffectedServiceConfirmation` adds a point-in-time affected-service prompt for shared stores; it does not prove service liveness or reload delivery. `RequiresRestart` and `StaticAfterStartup` remain consumer lifecycle requirements.

The current-value JSON editor uses one verified, redacted baseline for its initial text and subsequent difference analysis. When an aggregate display is redacted, it can reconstruct a scalar-only root object only if all public scalar values are known; sensitive fields remain preservation placeholders. It does not infer missing nulls, nested object shapes, dictionary keys, or list entries from schema samples. Unknown structure disables Analyze and Apply with a localized explanation; edit known scalars individually instead. Existing drafts and issues wholly inside redacted paths survive visible-scope replacement. Overlapping parent edits or diagnostics that span hidden and visible paths block editing; save or undo that state first. These editor guarantees do not change import/export or structured collection editing.

Migration: fragment `IsValid` no longer means schema success; use `IsSchemaValid` only for fragment editing. Handle `Rejected` and nullable `MutationGroup`, preserve failed-result preview data, and expect multiple target reports per definition. Complete validity requires coverage, current authority, and all reports, not merely an empty issue list or matching SchemaHash. Same-definition paired commands now share one document version; do not assume one version per history row.

Public contracts: `Monica.Configuration/Facades/ConfigurationFacade.cs`, `Monica.Configuration/Models/ConfigurationMutationGroupApplyModels.cs`, `Monica.Configuration/Models/ConfigurationMutationGroupValidationPreview.cs`, `Monica.Configuration/Models/ConfigurationCandidateValidationReport.cs`, `Monica.Configuration/Models/ConfigurationHistoryRollbackPreview.cs`, and `Monica.Configuration/Models/ConfigurationUnifiedVersionApplyPreview.cs`.

Behavioral checks:

- `tests/Test.Monica.Configuration/Services/ConfigurationMutationObjectValidationTests.cs`: one final save/shared histories, invalid no effects, masking, priority/fallback, every adoption subset, aliases/null, stale siblings, physical/store projection drift and explicit reload, and current-code rollback.
- `tests/Test.Monica.Configuration/Services/ConfigurationSourceInspectorTests.cs`: source chains and inventory.
- `tests/Test.Monica.Configuration/Projection/MonicaConfigurationProviderPartialReloadTests.cs`: targeted projection reload.
- `tests/Test.Monica.Configuration.EventBus/ConfigurationEventBusBridgeTests.cs`: invalidation metadata and bridge behavior.
