# Inspect and change managed values

Register `AddConfiguration(plan)` using the [input plan and store](bootstrap-and-stores.md). `ConfigurationFacade` owns inspection, preview, mutation, history, rollback, and reload. Check each returned `Res<T>`; a failed apply can carry a structured rejection in its original `Data`, and a persisted operation can report `PostCommitIssues`.

## Startup configuration

The Values page labels definitions with `ConfigurationKind.Startup` as **Startup** and explains their deployment-owned, read-only boundary. Its field rows show the frozen running value alongside the current startup-input candidate. The candidate's runtime-provided validation and `RequiresRestart` status govern the notice; the UI does not compare masked text, apply candidates, or expose source editing, JSON editing, or managed history for these definitions. Managed import/export packages exclude Startup definitions. Invalid candidates remain inspectable and do not replace the running value. Change the startup input through the deployment or local environment file, fix validation errors, and restart to adopt it.

The source-chain dialog separates the frozen startup contributors from the current candidate providers. Definition-level source contribution counts describe current candidate providers, not adoption by the running service. Running values, candidate values, and both source chains use the same sensitive-value disclosure policy as managed configuration. Refresh reads a new candidate assessment while preserving the process's frozen startup snapshot.

## Find the winning source

Use `GetDefinitionsAsync()` to discover definition keys or `GetDefinitionStateAsync(definitionKey)` for a schema, effective document version, and display-safe values. A definition key is [distinct from its binding section](options-and-validation.md). Source inspection is local to scanned definitions. For the aliased Orders example, `GetSourceChainAsync("orders", LogicalPath.FromProperties("minimum"))` returns contributions in priority order and marks the effective source. Use configuration aliases in paths, not `nameof(OptionsOrders.MinimumBatchSize)`.

`GetConfigurationSourcesAsync()` and `GetConfigurationSourceInventoriesAsync()` expose the runtime stack; inventory includes unmanaged host values by default. Sensitive display values are redacted. A lower-priority edit may be masked and leave the effective value unchanged. For external JSON targets, read `GetSourceRevisionAsync(sourceKey)` before staging a write.

## Manual managed values

When `Application.AutonomousWork=false`, the Values page labels a validated local fallback as **Temporary · not persisted** and reports no stored version. This means the deployed owner has not created its effective document. Reading, refreshing, and reloading do not create it or publish the local schema. Existing metadata and document contents remain the authority for explicit edits.

`ConfigurationDefinitionSummary.CanMutate` and `MutationReadOnlyReason` describe the current Manual write prerequisite. An edit requires an active published definition, an existing document, and matching definition key, section path, schema version, canonical schema hash, and validation capability/revision. The document's `SchemaVersion` must match the current definition: a local Manual edit cannot incidentally migrate an older stored shape. Object-code contracts require the matching locally discovered executable owner. Missing, retired, unreadable or mismatched authority is read-only; publish, initialize or migrate through the deployed owner first. Portable-only remote definitions can be edited when their complete validation contract is available.

Manual editing supports only `ConfigurationEffectiveStoreMutationTarget`. Ordinary JSON inputs and deployment files are read-only, and source inspection performs no permission probes. Obtain a complete preview and pass its `ValidationFingerprint` to apply. Apply repeats complete validation and authority inspection; changed publication evidence invalidates review. The File store rechecks publication/document existence while holding an exclusive writer lease shared by native store instances/processes using that directory; publication, retirement, purge and other native writes take the same lease. The retained `.writer.lock` file contains no values and is never deleted on release. Reads do not acquire or create it. Arbitrary external file edits do not participate in this native writer contract. EF Core holds the publication lock before its mutation lock and checks every target before committing value/history/group rows in one transaction. File batches retain their documented sequential partial-result behavior. Neither boundary can turn a missing document into a Manual seed.

Definition-scoped facade source chains and contribution rows apply the same Manual publication/document authority as the editor. Missing, retired, incompatible or unreadable authority keeps source values visible while marking the managed store read-only with its reason. These inspection results do not replace preview or the atomic persistence guard.

Custom effective-value and mutation-batch stores default `SupportsPublishedDefinitionGuard` to false. Opt in only when `ConfigurationEffectiveValueSaveRequest.PublishedDefinitionExpectation` is checked in the same serialization/transaction boundary as the write; unsupported guarded requests must be rejected before writes. Metadata and effective-value stores must identify the same logical store. Explicit reviewed changes may publish invalidation metadata, but the Configuration.EventBus bridge does not start its automatic reload subscription in Manual mode.

Use preview plus `ApplyMutationGroupAsync` in Manual hosts. The older single-value `MutateAsync` convenience method has no reviewed-fingerprint argument and is therefore unavailable in this mode. History and version rollback already require their reviewed plan tokens and carry the complete-validation fingerprint into the same guarded apply path. Native File writes require permission to open the root's `.writer.lock` as well as the value/history files; access failures propagate rather than being retried as contention.

The UI retains temporary values and concrete read-only reasons for inspection; they are not an authorization bypass. Startup continues to use its separate frozen actual/candidate display above. Historical records remain inspectable, while rollback/import apply through the same current Manual authority and complete-validation checks. Purging definitions and deleting saved versions are unavailable in Manual mode; `MaintenanceReadOnlyReason` explains that these operations belong to the deployed owner or an Automatic administration host. A Manual local definition does not turn a retired persisted publication into an active publisher in the publication overview. Audit/version capture that accompanies a successful guarded value mutation remains part of that explicit operation.

Shape-aware source inspection uses the same ancestor barriers as production binding: an explicit null or empty object/list hides lower descendants. Their supplied values remain inspectable but have `IsEffective=false`, and contribution and inventory counts exclude them from effective values. An exact null or empty container remains a supplied winning hit; nonempty containers continue to merge by configuration keys, and an explicit null scalar wins over lower scalar values. Constructor defaults can supply an effective member without any winning provider hit, so do not treat the first source-chain entry as the winner. Masking does not waive physical projection revision checks: an out-of-band file change still requires runtime reload and a fresh preview.

Chained providers are aliases, not physical supplied-value rows. Inspection first proves a higher independent `IConfigurationRoot` chain is acyclic and separate from the active root, then uses its leaf values and ancestor barriers to determine whether a lower physical hit remains effective. Active-root aliases, cyclic graphs, and opaque non-root chains are not queried to establish a winner and cannot prove a lower hit effective. Physical source projection revisions still cover masked contributions; aliases have no physical projection revision.

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

`facade` is the injected `ConfigurationFacade`, and `cancellationToken` belongs to the preview caller. Reuse the same commands and their RequestIds for apply. Version zero means an observed missing document in Automatic mode; Manual mode cannot create it. A stale version, source revision, schema, executable revision, or contributing aggregate requires a fresh review, not a forced overwrite. `ExpectedValidationFingerprint` is an opaque token binding the reviewed commands and contributing baseline; do not reconstruct or parse it. Complete mutation fingerprints are process-scoped, so refresh after a host restart or replica change. Apply always validates again. Automatic callers may omit the token; Manual callers must supply it.

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

Find records with `GetHistoryAsync(definitionKey, path)` or paged history. `PreviewHistoryRollbackAsync([historyId])` returns the current physical target values, complete `ValidationReports`, `Problems`, `CanApply`, and the existing `PlanToken`. Apply a valid preview through `RollbackHistoryAsync(historyId, planToken)`; selected histories and mutation groups use the same preview contract. A previous schema-valid historical value is still checked against current object code and contributing siblings. Rollback applies new writes to the current targets and creates new history; it does not reverse or delete the original history rows. Group rollback records its relationship to the new rollback group. Rejection does not create an audit mutation or reload.

Unified versions are disabled by default. Enable `UseUnifiedVersionControl()` with explicit inclusion filters. `PreviewUnifiedVersionRollbackAsync(version)` exposes target validation, adoption problems, and `CanApply`. Pass a `ConfigurationUnifiedVersionRollbackRequest` to `RollbackUnifiedVersionAsync`, setting its required `Version` and the returned `PreviewFingerprint`. Known current-schema invalid, incomplete-authority, or failed targets block the whole operation rather than being skipped as historical incompatibilities. Existing skips for unknown definitions or hard-incompatible historical structure remain distinct. Review every target and refresh a stale preview; an old preview cannot authorize changed values or code contracts. Remote ObjectCode/Unknown definitions without local authority cannot be repaired by trusting published CLR names or by using a nonexistent owner-validation transport.

## Reload and operator UI

The five routed Configuration workspaces share `ConfigurationPageHeader` for identity, route actions and title-adjacent diagnostics. Its `Icon` parameter has been removed; custom callers should supply `Title`, `Subtitle`, optional `TitleAccessory`, and optional `Actions`. Contextual validation popovers stay attached to their actual title control. Storage effectiveness rows retain a visible compact status with the precise explanation available through native hover and keyboard-focus tooltips. Shared visual composition and containment rules belong to the [working-interface language](../../monica-ui-development/references/monica-workbench-design-language.md).

After an effective-store mutation commits, the coordinator attempts targeted local projection reload and versioned notification. A writable external-source mutation attempts a full local reload. Failures do not undo committed values; inspect each outcome and `PostCommitIssues` before declaring the change live. `GetRuntimeReloadStatusAsync()` compares loaded and stored versions. `ReloadRuntimeConfigurationAsync()` recovers from a missed or out-of-band change; it is not required after every successful facade save. `BroadcastReloadAllAsync()` requests local and best-effort remote refresh. Source report caches and observed options attempts have [separate lifetimes](options-and-validation.md); reload does not replace an existing `IOptions<T>` value or guarantee every consumer has adopted the change.

For distributed invalidation, call `.UseEventBus()` on the Configuration registration and provide a default `IDistributedEventBus`, or select a keyed distributed service. The bridge publishes invalidation metadata rather than values; all participants use the same topic, default `monica.configuration.reload`. Set a stable `ModuleConfigurationOption.PublisherKey` per logical service and reuse it across replicas; its default is the host application name. This identity is distinct from process reload `InstanceId`.

The bridge owns one explicit distributed subscription on its configured topic and service key. Each delivery uses a fresh asynchronous DI scope, while the host-owned reload receiver retains its deduplication and debounce state. Its concrete handler declares `ExecutionTransactionMode.None`: invalidation schedules configuration projection reload rather than a business database write, so it does not select or open an application UnitOfWork even when several write contexts are registered. The handler is factory-owned, excluded from structural business-type discovery, and not registered for automatic EventBus discovery. It does not appear as an application ProjectUnit. Ordinary application event handlers retain their transaction policies.

`monica.AddConfigurationUI()` contributes state, storage, history, and version pages with shell and localization dependencies. A Web host completes `app.UseMonica()` and `app.MapMonica()`. The State workspace retains `/configuration/state`; catalog selection, search results, and deep links use the definition identity and actual schema `RelativePath`, including configuration aliases rather than CLR property names. State diagnostics separate the current source snapshot from the latest observed default-options attempt; unobserved is not a green success. `EnableAffectedServiceConfirmation` adds a point-in-time affected-service prompt for shared stores; it does not prove service liveness or reload delivery. `RequiresRestart` and `StaticAfterStartup` remain consumer lifecycle requirements.

The UI masks annotated sensitive values by default. Set `ShowSensitiveValues` once on the UI module to show every configuration value:

```csharp
builder.AddMonica(monica =>
{
    monica.AddConfigurationUI(options => options.ShowSensitiveValues = true);
});
var app = builder.Build();
app.UseMonica();
app.MapMonica();
```

This host setting applies to scalar inputs and copying, structured previews and JSON editors, pending changes, history, unified versions and rollback comparisons, source inventories and files, diagnostics search, and exports. It preserves sensitivity annotations and stored data. Exports read a complete effective document before applying the selected field redaction, so sensitive siblings do not erase ordinary exported values. It does not change validation coverage, write authorization, or rollback compatibility. Effective-state and source-read facade methods also accept `includeSensitive: true` for callers that explicitly own disclosure; their default remains masked. Custom `IConfigurationSourceInspector` implementations must accept and forward this flag through batch and nested reads while retaining `IsSensitive` annotations. Adapt facade method groups to lambdas when their new optional argument changes delegate arity. `GetDebugViewAsync`, history, and saved-version payloads already carry raw values, so their consumers must choose presentation deliberately.

Storage at `/configuration/storage` independently reads provider inventories, storage descriptors, and the latest in-process store operation states. Capability and notifier flags describe registration; they are not health probes or proof of delivery. Operation states are recorded operations, not a continuous availability check or durable history. Each successful read has its own local acceptance time. Refresh retains accepted evidence until that read's newest outcome replaces it on success or invalidates it on failure; a successful empty list is distinct from unavailable evidence. Older responses, including errors, cannot overwrite newer reads. Source filtering searches descriptor metadata only, not inventory values. A masked leaf without a proven effective provider must not name the first source entry as its overriding source.

Open a file using its exact source key. The file view is a current physical JSON read with known managed sensitive paths redacted by default; it is not a guarantee that all unmanaged secrets are hidden. Its contents can differ from accepted runtime provider inventories until runtime reload. Changing the dialog's source key immediately clears the previous source's evidence and rejects its late responses. Same-key refresh can retain the accepted file while pending; failure is unavailable, while an accepted empty JSON object remains valid file evidence.

The Values page's Configuration sources dialog compares one locally scanned definition, using `GetDefinitionSourceContributionsAsync(definitionKey)`. Larger provider priority indexes take precedence. Supplied counts that source's registered scalar contributions; Effective counts those still selected at runtime. Counts across sources are not a distinct-value total, and zero effective contributions is ordinary precedence evidence. Writable and Read-only describe source capability, not health or the current user's authorization. A successful empty result means no reported contributions for this definition, not an absence of host providers. The named file action opens the exact JSON source and returns native focus to its opener after Close or Escape.

Diagnostics at `/configuration/debug` starts from the facade's raw Microsoft configuration debug view. Credential-like path and value masking is a UI heuristic, not complete schema-based privacy: `GetDebugViewAsync()` itself does not redact values. Search uses safe line text and cannot match a hidden credential value. Deliberate per-line reveal exposes the raw line; hide it again when finished. Refresh immediately remasks retained lines, prevents reveal during the read, and does not carry a revealed line index into replacement data. With `ShowSensitiveValues` enabled, lines and search use raw values, and per-line reveal controls and masking notices are omitted.

`ConfigurationStoragePage`, `ConfigurationDebugPage`, `ConfigurationDefinitionSourcesDialog`, and `ConfigurationSourceFileDialog` implement `IAsyncDisposable`; custom owners must await teardown. Their read state borrows the facade and other injected services. Retirement rejects new work, ignores late UI adoption, closes owned file dialogs, and drains actual tokenless facade reads, native dialog results and acquired async components. The Sources dialog also owns its nested file viewer, so closing the parent or retiring its route drains that child before the owned dialog operation completes. It does not dispose the borrowed services or establish that an underlying read was cancelled.

Staging field edits, applying an analyzed JSON fragment to the draft, or accepting import candidates changes pending state, not persisted values. Zero portable fragment issues do not prove whole-object validity. Use Review & save to inspect the current complete-group preview and apply its opaque fingerprint with the reviewed commands. Paired sibling edits must be reviewed together. Rejected saves retain staged edits and typed findings; save and rollback previews require complete validity.

Catalog selection, route changes, and Refresh do not implicitly save or clear the circuit's drafts. Review and undo act on the captured items, preserving a newer draft or issue at the same path. Refreshed baselines reconcile only still-current drafts. An already admitted save reconciles actual applied outcomes even if its route retires; returning to the workspace must not offer those unchanged applied drafts again. Component lifecycle mechanics belong to [UI development](../../monica-ui-development/references/blazor-best-practices.md#22-bound-async-work-to-the-component-lifetime).

History at `/configuration/history` reads persisted mutation and group metadata from the configured history store; it is not an availability or adoption trend. Distinguish an unavailable query from a successful empty store or a scope with no matches. Mutations filter by definition, modification dates, exact schema path, and persistence target; Groups filter by definition and creation dates, without inheriting mutation-only path or target filters. `QueryHistoryPageAsync` defaults to 50 mutation units, not 50 rows: matching rows in one group stay together. Group pages count groups. Continue with each returned exclusive cursor under the same query, and restart pagination when the scope changes. A filtered mutation unit need not contain every row of its group; read group details for complete audit context.

Source names, physical and projected paths, and revisions describe the target at the time of the audited write; they do not establish the currently winning source. Modifier and reason fields are recorded context, not independent proof of authenticated actor identity. History facade payloads retain stored `OldValue` and `NewValue`; do not advertise raw history responses or exports as redacted. Operator history previews use current field sensitivity, including sensitive ancestors and descendants. A different historical schema version or hash alone does not classify an ordinary resolved field as sensitive. Missing definitions and unresolved paths remain hidden in the default mode; `ShowSensitiveValues` also reveals those historical values. Displaying a historical value does not authorize rollback: current compatibility and complete validation still govern writes. Review rollback against current targets and complete validation, supply a reason when useful, and report actual outcomes and post-commit issues.

Versions at `/configuration/versions` lists the newest 200 saved unified snapshots. This bounded list has no continuation or total-retention count. Its latest snapshot is the latest saved capture, not live configuration or proof of consumer adoption. Captures arise automatically from eligible included mutations when unified version control is enabled; Refresh and comparison do not create a capture, and the page has no manual-capture operation. An unavailable list or comparison is distinct from a successfully empty result.

`HasLoadedVersions` identifies an accepted successful list, including an empty one. Starting a refresh retains that list while `Loading=true`; the newest completed load replaces it on success or clears it and the flag on failure. Late older loads cannot restore or invalidate newer evidence.

Local effective snapshots use complete production binding and schema-owned serialization, including constructor defaults and configuration aliases. An explicit empty object in a higher provider still blocks lower-provider descendants; absent CLR members retain their constructor defaults, which are included in the saved capture. Explicit null and empty collections retain their bound meanings. `ConfigurationEffectiveValueSeedFactory.CreateEffectiveJson` requires matching locally discovered executable authority and the captured host configuration; source-only `CreateRuntimeJson` deliberately does not complete defaults. Published-only owners retain their persisted effective JSON without instantiating remote types. This correction changes new local effective reads and captures, not existing saved snapshots: incomplete historical documents remain unchanged and can still fail complete rollback validation. Create a new eligible mutation for a new complete capture rather than filling historical fields with today's defaults.

Select a ledger snapshot to compare it with the latest saved snapshot, or choose an explicit origin and target from all loaded snapshots. Equal pairs are valid no-change comparisons; reversed pairs reverse the direction. Previous means the next older retained snapshot in the loaded list, not the preceding numeric version: deleted versions leave gaps. Every selection, including a cached comparison or clearing a selection without a previous snapshot, retires older comparison adoption.

Unified-version facade snapshots retain complete JSON values; they are not API-redacted. The UI reports captured/current schema drift separately from visibility. In the default mode, current sensitivity annotations redact sensitive ancestors, descendants and dictionary/list templates; unknown structure and missing definitions remain hidden. A version/hash mismatch does not hide every otherwise known ordinary field. An absent Added or Removed side remains absent. `ShowSensitiveValues` bypasses comparison masking even when a definition is unavailable. Rollback preview/apply still checks its current schema, executable contract, and reviewed fingerprint independently of this presentation setting.

The internal `ConfigurationVersionsPageState` is now constructed and owned by each routed page rather than registered as circuit-scoped DI state. Keep the facade, snackbar, and localizer borrowed; await the page state's `DisposeAsync()` without disposing those services. `ConfigurationUnifiedVersionDiffPanel` implements `IAsyncDisposable`: custom component owners must await teardown, and changed comparisons must be supplied as new immutable records. The panel copies the admitted pair and snapshot collections before schema reads, rejects superseded results, and drains actual tokenless reads on retirement. `ConfigurationUnifiedVersionRollbackDialog` and `ConfigurationUnifiedVersionRollbackPreview` replace `IDisposable` with `IAsyncDisposable`; migrate custom owners to await their actual teardown. `ConfigurationPostCommitIssuesDialog` also owns async clipboard retirement: it drains admitted tokenless calls and suppresses late snackbar feedback, without reversing a completed clipboard write. Cancellation or closing a view does not establish that a facade operation stopped; the general [UI lifetime contract](../../monica-ui-development/references/blazor-best-practices.md#22-bound-async-work-to-the-component-lifetime) still applies.

The current-value JSON editor uses one verified baseline, redacted by default, for its initial text and subsequent difference analysis. When an aggregate display is redacted, it can reconstruct a scalar-only root object only if all public scalar values are known; sensitive fields remain preservation placeholders. It does not infer missing nulls, nested object shapes, dictionary keys, or list entries from schema samples. Unknown structure disables Analyze and Apply with a localized explanation; edit known scalars individually instead. Existing drafts and issues wholly inside redacted paths survive visible-scope replacement. Overlapping parent edits or diagnostics that span hidden and visible paths block editing; save or undo that state first. With `ShowSensitiveValues` enabled, the editor requests the complete effective document and includes annotated fields in its baseline and draft analysis. It still requires a known original shape and preserves explicitly redacted imported paths.

Migration: fragment `IsValid` no longer means schema success; use `IsSchemaValid` only for fragment editing. Handle `Rejected` and nullable `MutationGroup`, preserve failed-result preview data, and expect multiple target reports per definition. Complete validity requires coverage, current authority, and all reports, not merely an empty issue list or matching SchemaHash. Same-definition paired commands now share one document version; do not assume one version per history row.

Public contracts: `Monica.Configuration/Facades/ConfigurationFacade.cs`, `Monica.Configuration/Models/ConfigurationMutationGroupApplyModels.cs`, `Monica.Configuration/Models/ConfigurationMutationGroupValidationPreview.cs`, `Monica.Configuration/Models/ConfigurationCandidateValidationReport.cs`, `Monica.Configuration/Models/ConfigurationHistoryRollbackPreview.cs`, and `Monica.Configuration/Models/ConfigurationUnifiedVersionApplyPreview.cs`.

Behavioral checks:

- `tests/Test.Monica.Configuration/Services/ConfigurationMutationObjectValidationTests.cs`: one final save/shared histories, invalid no effects, masking, priority/fallback, every adoption subset, aliases/null, stale siblings, physical/store projection drift and explicit reload, and current-code rollback.
- `tests/Test.Monica.Configuration/Services/ConfigurationSourceInspectorTests.cs`: distinct provider identities, masked ancestor barriers, exact null/empty container hits, nonempty merges, scalar null winners, truthful contribution/inventory counts, independent chained evidence, and conservative active-root/cyclic/opaque alias handling.
- `tests/Test.Monica.Configuration/Services/Support/ConfigurationRollbackPersistencePlannerTests.cs`: production-bound defaults under empty/null higher-provider barriers without false leaf drift, while changed masked physical contributions still block until reload.
- `tests/Test.Monica.Configuration/Services/Support/ConfigurationEffectiveSnapshotReaderTests.cs`: complete local capture after an empty higher-provider object, defaults and aliases, distinct absent/empty/null collections, published-document preservation, and rejection without local authority.
- `tests/Test.Monica.Configuration/Projection/MonicaConfigurationProviderPartialReloadTests.cs`: targeted projection reload.
- `tests/Test.Monica.Configuration.EventBus/ConfigurationEventBusBridgeTests.cs`: invalidation metadata and bridge behavior.
- `tests/Test.Monica.Configuration.EventBus/ConfigurationReloadTransactionTests.cs`: explicit default/keyed subscriptions, reload delivery through the real pipeline with multiple write contexts, and asynchronous delivery-scope disposal.
- `tests/Test.Monica.Configuration.UI/State/ConfigurationVersionsPageStateTests.cs`: cached-selection supersession, equal/reversed pairs, accepted-list retention during refresh, newest-load adoption, failure versus empty success, and actual read retirement.
- `tests/Test.Monica.Configuration.UI/Components/ConfigurationUnifiedVersionDiffPreparationTests.cs`: frozen comparison input, late schema reads, renewed schema authority, and owned preparation retirement without a renderer.
- `tests/Test.Monica.Configuration.UI/Support/ConfigurationUnifiedVersionValuePrivacyTests.cs`: schema drift independent of visibility, legitimate absent sides, and ancestor/subtree/template redaction.
- `tests/Test.Monica.Configuration.UI/State/ConfigurationStoragePageStateTests.cs`: independent read outcomes, accepted refresh evidence, synchronous producer reentrancy, superseded reads, collection capture, and actual retirement drain.
- `tests/Test.Monica.Configuration.UI/State/ConfigurationSourceFileStateTests.cs`: exact source-key changes, retained same-key file evidence, failure versus missing or empty payloads, and late read rejection.
- `tests/Test.Monica.Configuration.UI/Support/ConfigurationDialogSessionLifetimeTests.cs`: actual native inline completion, retired-provider dismissal, captured component drainage, linked parent/child retirement and retained persistence results without rendering.
- `tests/Test.Monica.Configuration.UI/State/ConfigurationDebugPageStateTests.cs`: deliberate reveal, immediate refresh remasking, safe replacement evidence, unavailable versus empty text, and actual retirement drain.

- `tests/Test.Monica.Configuration.UI/Support/ConfigurationValueVisibilityTests.cs`: host disclosure across editor baselines, exports, previews, and unavailable historical schema.
