# Startup inputs and managed stores

`ConfigurationDbContext` marks its tables for [common infrastructure layout](../../monica-infra-persistence/references/infrastructure-table-layout.md). Configure `UseMonicaTableLayout` and `UseMonicaMigrationHistory` in the `UseDbConfigurationStore` callback and matching migration factory. The context preserves repository model variants and interception while opting out of implicit sensitive logging. Layout does not move existing configuration values or initialize the database.

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

Resolve `ConfigurationFacade` and `ConfigurationEffectiveStateReader` from the host. Direct callers must now supply `IServiceProvider` rather than the former eagerly supplied store collaborators: the facade resolves each capability on use, and the state reader resolves the effective store only for Managed reads. This constructor change lets Startup-only hosts compose without placeholder stores. An Automatic configured managed host with an empty local catalog still publishes its empty inventory so ordinary removal/retirement semantics remain intact.

Global catalog reads include remote Managed definitions when a metadata store is explicitly configured; scoped Startup reads remain independent of it, and a metadata failure does not make local Startup definitions unavailable. Runtime reload-status inspection also works without a store. `ConfigurationReloadStatusReport.EffectiveValueStore` is null when there are no local Managed owners to compare; consumers must handle that absence instead of displaying a fabricated store.

## Managed input plans

`MonicaConfigurationInputPlan.Create` declares exactly one store composition, an optional section-path convention, and ordered managed JSON files. Creating the plan performs no store I/O. `AddConfiguration(plan)` applies it to runtime services; the plan's path convention cannot be overridden later. The file store is a simple local choice:

```csharp
var plan = MonicaConfigurationInputPlan.Create(inputs => inputs
    .UseFileConfigurationStore(options => options.RootDirectory = "configuration-store")
    .AddManagedJsonFile("appsettings.Managed.json", optional: true));

builder.AddMonica(monica => monica.AddConfiguration(plan));
```

Composition reads Startup owners only through `GetStartupOptions<T>()`. Managed owners are consumed after DI is available; they must not select providers, connections or process topology. The former `GetMonicaBootstrapConfiguration`, `BuildBootstrapConfiguration`, and `LoadEffectiveOptionsSnapshot` APIs and their snapshot types have been removed. Migrate composition inputs to explicitly classified Startup definitions and consume their one validated frozen instance. Keep operator-owned settings Managed and read them through runtime options or `ConfigurationFacade`.

`AddManagedJsonFile` resolves paths from the host content root and defaults to `optional: true`, `reloadOnChange: true`. The sources are applied during runtime module composition, after the managed effective-value provider and in declaration order. They cannot change the earlier frozen Startup snapshot. Avoid adding the same managed JSON path twice.

For a relational store, reference `Monica.Configuration.EfCore` and select `UseDbConfigurationStore(db => db.UseSqlite(connectionString))` in the plan, capturing the connection from validated Startup options. The callback configures runtime contexts and must remain deterministic and side-effect-free. The host owns `ConfigurationDbContext` migrations; neither input plan nor runtime module creates/upgrades the schema. The file store uses identity-addressed files under `RootDirectory`; legacy key-named files require migration or recreation. Custom `IMonicaConfigurationStoreComposition` implementations expose a diagnostic `Name` and `ConfigureRuntime` only; recording registrations must not perform store I/O. There is no separate startup reader or isolated service provider to keep aligned with runtime.

An initial `Created` event exposes `PreviousSchemaVersion`, `PreviousSchemaHash`, and `PreviousSchemaJson` as `null` in the public publication-history DTO. The EF Core read projection recognizes legacy exact-empty (`""`) prior hash/JSON values only for `Created` rows whose `PreviousSchemaVersion` is `null`, and projects those fields as `null`. Nonempty prior hash/JSON bytes, including whitespace, and every other history row retain their recorded values. This is read projection only: it does not rewrite stored rows, require a SQL migration, or change repository-wide string defaults.

Runtime activation is a separate boundary. In Automatic mode, a missing document is seeded from the actual declared defaults and source-faithful binder, then checked before creation. DiagnosticOnly permits ordinary invalid values with diagnostics; FailFast refuses an invalid first seed before persistence, and operational validation faults are fatal in both modes. Existing documents are not rewritten as a startup repair. See the [policy and observed-options contract](options-and-validation.md) before selecting runtime enforcement.

## Manual hosts and shared environments

Set `monica.ConfigureApplication(options => options.AutonomousWork = false)` for a local host that must not participate automatically in a shared environment. Configuration consumes this frozen application policy globally. Its admitted projection controller still reads and validates configuration, but it does not publish, withdraw or repair metadata, create missing effective documents, or migrate a database. The activation catalog records this participation as Manual. Startup options retain their ordinary frozen behavior.

A missing document for a locally discovered Managed owner is represented by a temporary, fully validated local value from constructor defaults and current input sources. Complete portable and object validation must succeed even under `DiagnosticOnly`; missing executable authority or a validation fault is fatal. Temporary values carry `IsUnpersisted=true` and no persisted version in state/value/source diagnostics. Reloads repeat this read-or-temporary-value decision and exclude the provider's previous projection from seed inputs, so stale values cannot seed themselves. When the deployed owner creates a document, a subsequent reload can adopt it. None of these reads initializes the store.

File-store metadata, value and version reads do not create directories or repair interrupted deletions. Deferred version-deletion cleanup runs only during an explicit version append/delete. Relational reads require an already migrated database and do not create tables. Custom stores must keep their read operations free of initialization and repair writes. Manual external JSON inspection does not probe write permission and reports those sources as read-only; the [reviewed mutation boundary](runtime-changes.md#manual-managed-values) permits only guarded effective-store edits.

For slow startup, measure `monica.configuration.startup.stage.duration` from the `Monica.Configuration` meter. Values are milliseconds with bounded `stage` and `result` tags. `provider-activation` contains `metadata-publication` and `projection-reload`; `runtime-validation` is separate. Web activation emits these during `UseMonica`, so a listener must already be active to receive them; a later exporter cannot recover an earlier histogram measurement. Preserve both source validation and materialized-object rules when investigating validation cost.

Built-in JSON and managed providers index child-key traversal per data snapshot to avoid repeatedly scanning the whole configuration tree. Full reload, partial managed reload, and flat `Set` replace that snapshot. Reusing a configuration view must therefore see changed child keys, nulls, and empty containers even when `Set` does not raise a reload notification. This indexing does not refresh Microsoft options caches or change provider precedence, validation policy, or reload ownership.

Frozen Startup access and managed input plans: `Monica.Configuration/Bootstrap/MonicaStartupOptionsExtensions.cs`, `Monica.Configuration/Bootstrap/MonicaConfigurationInputPlan.cs`, and `Monica.Configuration/Bootstrap/MonicaConfigurationInputPlanBuilder.cs`; EF Core plan: `Monica.Configuration.EfCore/Bootstrap/MonicaConfigurationInputPlanEfCoreBuilderExtensions.cs`. Basic composition: `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs`.

Behavioral checks:

- `tests/Test.Monica.Configuration/Bootstrap/MonicaConfigurationInputPlanTests.cs`: input declaration invariants, host-isolated runtime source precedence, and lazy store access.
- `tests/Test.Monica.Configuration/Binding/MonicaConfigurationCollectionBindingTests.cs`: shared collection binding through frozen Startup access and runtime options.
- `tests/Test.Monica.Configuration/Binding/ConfigurationObjectMaterializerTests.cs`: aliases, defaults, explicit shape, and precedence.
- `tests/Test.Monica.Core/Configuration/ConfigurationChildKeyIndexTests.cs`: Microsoft child-key ordering, duplicates, case-insensitive paths, and data replacement without notifications.
- `tests/Test.Monica.Core/Configuration/ConfigurationDocumentProjectionTests.cs` and `tests/Test.Monica.Configuration/Projection/MonicaConfigurationProviderPartialReloadTests.cs`: JSON, projection, and managed-provider index invalidation across full reload, partial reload, and flat writes.
- `tests/Test.Monica.Configuration/Bootstrap/StartupOptionsTests.cs`: eager Startup validation, default-name instance identity, unsupported options contributions, and no-store inspection.
- `tests/Test.Monica.Configuration/Services/ConfigurationStartupSourceTests.cs`: frozen versus candidate provenance and nested secret masking.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationStorePublishTests.Startup.cs`: conversion to Startup preserves old managed publication audit without publishing Startup metadata.
- `tests/Test.Monica.Configuration/Modules/ManagedConfigurationObjectValidationTests.cs`: runtime seed validation and policy boundaries.
- `tests/Test.Monica.Configuration/Modules/ManualConfigurationTests.cs`: read-only activation, fully validated temporary values, self-seed prevention, guarded edits, and external-source ownership.
- `tests/Test.Monica.Configuration/Stores/File/FileConfigurationManualWriteTests.cs`: pure reads and publication checks within the file-store save gate.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationManualWriteTests.cs`: transactional active-publication checks before value and audit writes.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationInputPlanTests.cs`: frozen Startup access remains independent of relational Managed activation and seeding.
- `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationStorePublishTests.InitialPublication.cs`: migrated-store initial null prior snapshots, narrowly gated legacy empty-value projection without stored-row changes, and preservation of nonempty and later-schema history.
