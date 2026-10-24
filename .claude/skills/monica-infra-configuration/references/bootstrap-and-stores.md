# Startup inputs and managed stores

## Deployment-owned startup options

Use `[Configuration("Deployment:Resources", Kind = ConfigurationKind.Startup)]` for values that decide host construction, such as provider activation and connection settings. Supply ordinary host configuration before `AddMonica`; Core captures one immutable provider snapshot before invoking its callback. Read that snapshot through `GetStartupOptions<T>()` from `Monica.Configuration.Bootstrap`. The extension is available on the Monica builder and module composition contexts, without constructing a service provider or accessing a managed store.

```csharp
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;

builder.AddMonica(monica =>
{
    var resources = monica.GetStartupOptions<OptionsResources>();
    monica.AddConfiguration();
    // Compose resource providers from the validated frozen object.
});

[Configuration("Deployment:Resources", Kind = ConfigurationKind.Startup)]
public sealed class OptionsResources
{
    public bool RedisEnabled { get; set; }
    public string? RedisConnection { get; set; }
}
```

The first read binds and validates the complete owner immediately, using the same aliases, shape-aware materializer, portable constraints, and pure object rules as managed options. Every discovered Startup owner is validated before service registration completes, including owners that no module reads explicitly. A missing section can use valid constructor defaults; included optional providers must have a valid disabled/default shape, and the resource owner checks prerequisites when enabled. Types from omitted discovery assemblies are not eagerly created. Startup requires an explicit section path and rejects overlapping Managed ownership. Invalid values and binding/execution faults always fail composition, independent of managed `RuntimeValidationBehavior`.

Ordinary provider precedence is preserved. Explicit nulls, empty containers, and provider origins are captured with values. Sources appended or changed after the callback begins affect only the deployment candidate; they cannot change the running Startup object. The default-name `IOptions<T>`, `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` all return the same object across scopes. Consumers must treat it as read-only. Named options, Configure/PostConfigure, options validators, change-token sources, and custom options factories/accessors are rejected for Startup. Monitor subscriptions never fire. The validation boundary ends when `AddMonica` returns; do not mutate these registrations afterward.

`Application:EnvironmentFiles` selects host-owned JSON deployment sources; see [host composition](../../monica-infra-hosting/references/host-composition.md) for source ordering, complete-section selection, path rules, and atomic reload acceptance. Files cannot own Managed sections, even when a higher source hides those values. The operator UI exposes their JSON identity and physical path for inspection; `IDeploymentConfigurationProvider` marks them read-only, so external managed mutations cannot add a Managed section to a deployment file. Accepted reloads update the visible candidate and may require restart; rejected reloads retain the previous accepted source values and shapes. Neither changes the frozen Startup object.

A Startup-only `AddConfiguration()` host needs no store, including in Development with DI validation enabled. Its catalog, definitions, actual/candidate values, validation, and source chains remain inspectable. Store-only administrative actions fail explicitly when no store is configured. Registering the module makes discovered Startup owners visible in the configuration UI; an early read alone provides frozen options without requiring the UI or configuration module.

Resolve `ConfigurationFacade` and `ConfigurationEffectiveStateReader` from the host. Direct callers must now supply `IServiceProvider` rather than the former eagerly supplied store collaborators: the facade resolves each capability on use, and the state reader resolves the effective store only for Managed reads. This constructor change lets Startup-only hosts compose without placeholder stores. A configured managed host with an empty local catalog still publishes its empty inventory so ordinary removal/retirement semantics remain intact.

Global catalog reads include remote Managed definitions when a metadata store is explicitly configured; scoped Startup reads remain independent of it, and a metadata failure does not make local Startup definitions unavailable. Runtime reload-status inspection also works without a store. `ConfigurationReloadStatusReport.EffectiveValueStore` is null when there are no local Managed owners to compare; consumers must handle that absence instead of displaying a fabricated store.

## Managed input plans

`MonicaConfigurationInputPlan.Create` declares exactly one store composition, an optional section-path convention, and ordered managed JSON files. Creating the plan performs no store I/O. `AddConfiguration(plan)` applies its runtime half; the plan's path convention cannot be overridden later. For a host that does not need a startup options snapshot, the file store is a simple local choice:

```csharp
var plan = MonicaConfigurationInputPlan.Create(inputs => inputs
    .UseFileConfigurationStore(options => options.RootDirectory = "configuration-store")
    .AddManagedJsonFile("appsettings.Managed.json", optional: true));

builder.AddMonica(monica => monica.AddConfiguration(plan));
```

The existing managed snapshot API remains available for consumers that intentionally read managed state before composition. New deployment topology should use Startup ownership above. A managed snapshot and its runtime module must use the same input plan:

```csharp
using var bootstrap = plan.BuildBootstrapConfiguration(builder);
var snapshot = plan.LoadEffectiveOptionsSnapshot(bootstrap, [typeof(OrdersOptions)]);
var startupOrders = snapshot.Get<OrdersOptions>();
builder.AddMonica(monica => monica.AddConfiguration(plan));
```

Use `startupOrders` while composing host services or modules; the async snapshot variant is available for async startup. The bootstrap root is caller-owned and disposed here. The read uses the host's current configuration, then the plan's managed JSON files in declaration order; later files have higher precedence. It overlays effective values from the selected store without writing definitions or values. Treat it as a point-in-time read: runtime activation or another writer may later change the store. Keep topology-driving options static after startup or require restart. See [options and validation](options-and-validation.md) for declaring `OrdersOptions` and its binding path.

Bootstrap snapshots share production managed binding: configuration aliases, constructor defaults, missing versus explicit null, empty containers, and provider array-index precedence are preserved. They do **not** run `IValidatableObject` rules, adopt `RuntimeValidationBehavior`, apply consumer Configure/PostConfigure callbacks, publish runtime diagnostics, or observe actual-options attempts. Successful snapshot binding is not a complete validation report. Validate topology requirements explicitly in the owning startup code when necessary; runtime FailFast is not an earlier bootstrap gate.

`AddManagedJsonFile` resolves paths from the host content root and defaults to `optional: true`, `reloadOnChange: true` at runtime. Bootstrap and startup-snapshot reads do not watch files. Avoid adding the same managed JSON path twice.

For a relational store, reference `Monica.Configuration.EfCore` and select `UseDbConfigurationStore(db => db.UseSqlite(connectionString))` in the plan. The callback can run separately for bootstrap and runtime: keep it deterministic, side-effect-free, and stable across both. The host owns `ConfigurationDbContext` migrations; neither input plan nor runtime module creates/upgrades the schema. The file store uses identity-addressed files under `RootDirectory`; legacy key-named files require migration or recreation. A custom store composition must provide the same logical store in startup and runtime phases.

An initial `Created` event exposes `PreviousSchemaVersion`, `PreviousSchemaHash`, and `PreviousSchemaJson` as `null` in the public publication-history DTO. The EF Core read projection recognizes legacy exact-empty (`""`) prior hash/JSON values only for `Created` rows whose `PreviousSchemaVersion` is `null`, and projects those fields as `null`. Nonempty prior hash/JSON bytes, including whitespace, and every other history row retain their recorded values. This is read projection only: it does not rewrite stored rows, require a SQL migration, or change repository-wide string defaults.

Runtime activation is a separate boundary. A missing document is seeded from the actual declared defaults and source-faithful binder, then checked before creation. DiagnosticOnly permits ordinary invalid values with diagnostics; FailFast refuses an invalid first seed before persistence, and operational validation faults are fatal in both modes. Existing documents are not rewritten as a startup repair. See the [policy and observed-options contract](options-and-validation.md) before selecting runtime enforcement.

For slow startup, measure `monica.configuration.startup.stage.duration` from the `Monica.Configuration` meter. Values are milliseconds with bounded `stage` and `result` tags. `provider-activation` contains `metadata-publication` and `projection-reload`; `runtime-validation` is separate. Web activation emits these during `UseMonica`, so a listener must already be active to receive them; a later exporter cannot recover an earlier histogram measurement. Preserve both source validation and materialized-object rules when investigating validation cost.

Built-in JSON and managed providers index child-key traversal per data snapshot to avoid repeatedly scanning the whole configuration tree. Full reload, partial managed reload, and flat `Set` replace that snapshot. Reusing a configuration view must therefore see changed child keys, nulls, and empty containers even when `Set` does not raise a reload notification. This indexing does not refresh Microsoft options caches or change provider precedence, validation policy, or reload ownership.

Input plans and typed snapshots: `Monica.Configuration/Bootstrap/MonicaConfigurationInputPlan.cs`, `Monica.Configuration/Bootstrap/MonicaConfigurationInputPlanBuilder.cs`, `Monica.Configuration/Bootstrap/MonicaEffectiveOptionsSnapshot.cs`, and `Monica.Configuration/Bootstrap/MonicaEffectiveOptionsSnapshotLoader.cs`; EF Core plan: `Monica.Configuration.EfCore/Bootstrap/MonicaConfigurationInputPlanEfCoreBuilderExtensions.cs`. Basic composition: `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs`.

Behavioral checks:

- `tests/Test.Monica.Configuration/Bootstrap/MonicaConfigurationInputPlanTests.cs`: read-only input-plan snapshots and store overlays.
- `tests/Test.Monica.Configuration/Binding/MonicaConfigurationCollectionBindingTests.cs`: shared collection binding in bootstrap and runtime.
- `tests/Test.Monica.Configuration/Binding/ConfigurationObjectMaterializerTests.cs`: aliases, defaults, explicit shape, and precedence.
- `tests/Test.Monica.Core/Configuration/ConfigurationChildKeyIndexTests.cs`: Microsoft child-key ordering, duplicates, case-insensitive paths, and data replacement without notifications.
- `tests/Test.Monica.Core/Configuration/ConfigurationDocumentProjectionTests.cs` and `tests/Test.Monica.Configuration/Projection/MonicaConfigurationProviderPartialReloadTests.cs`: JSON, projection, and managed-provider index invalidation across full reload, partial reload, and flat writes.
- `tests/Test.Monica.Configuration/Bootstrap/StartupOptionsTests.cs`: eager Startup validation, default-name instance identity, unsupported options contributions, and no-store inspection.
- `tests/Test.Monica.Configuration/Services/ConfigurationStartupSourceTests.cs`: frozen versus candidate provenance and nested secret masking.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationStorePublishTests.Startup.cs`: conversion to Startup preserves old managed publication audit without publishing Startup metadata.
- `tests/Test.Monica.Configuration/Modules/ManagedConfigurationObjectValidationTests.cs`: runtime seed validation and policy boundaries.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationInputPlanTests.cs`: relational-store input plans.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationStorePublishTests.InitialPublication.cs`: migrated-store initial null prior snapshots, narrowly gated legacy empty-value projection without stored-row changes, and preservation of nonempty and later-schema history.
