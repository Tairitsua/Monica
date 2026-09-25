# Bootstrap input plan and stores

`MonicaConfigurationInputPlan.Create` declares exactly one store composition, an optional section-path convention, and ordered managed JSON files. Creating the plan performs no store I/O. `AddConfiguration(plan)` applies its runtime half; the plan's path convention cannot be overridden later. For a host that does not need a startup options snapshot, the file store is a simple local choice:

```csharp
var plan = MonicaConfigurationInputPlan.Create(inputs => inputs
    .UseFileConfigurationStore(options => options.RootDirectory = "configuration-store")
    .AddManagedJsonFile("appsettings.Managed.json", optional: true));

builder.AddMonica(monica => monica.AddConfiguration(plan));
```

When managed settings choose startup topology, use this sequence instead: load them before `AddMonica` and pass that same plan to runtime registration.

```csharp
using var bootstrap = plan.BuildBootstrapConfiguration(builder);
var snapshot = plan.LoadEffectiveOptionsSnapshot(bootstrap, [typeof(OrdersOptions)]);
var startupOrders = snapshot.Get<OrdersOptions>();
builder.AddMonica(monica => monica.AddConfiguration(plan));
```

Use `startupOrders` while composing host services or modules; the async snapshot variant is available for async startup. The bootstrap root is caller-owned and disposed here. The read uses the host's current configuration, then the plan's managed JSON files in declaration order; later files have higher precedence. It overlays effective values from the selected store without writing definitions or values. Treat it as a point-in-time read: runtime activation or another writer may later change the store. Keep topology-driving options static after startup or require restart. See [options and validation](options-and-validation.md) for declaring `OrdersOptions` and its binding path.

`AddManagedJsonFile` resolves paths from the host content root and defaults to `optional: true`, `reloadOnChange: true` at runtime. Bootstrap and startup-snapshot reads do not watch files. Avoid adding the same managed JSON path twice.

For a relational store, reference `Monica.Configuration.EfCore` and select `UseDbConfigurationStore(db => db.UseSqlite(connectionString))` in the plan. The callback can run separately for bootstrap and runtime: keep it deterministic, side-effect-free, and stable across both. The host owns `ConfigurationDbContext` migrations; neither input plan nor runtime module creates/upgrades the schema. The file store uses identity-addressed files under `RootDirectory`; legacy key-named files require migration or recreation. A custom store composition must provide the same logical store in startup and runtime phases.

Input plans and typed snapshots: `Monica.Configuration/Bootstrap/MonicaConfigurationInputPlan.cs`, `MonicaConfigurationInputPlanBuilder.cs`, and `MonicaEffectiveOptionsSnapshot.cs`; EF Core plan: `Monica.Configuration.EfCore/Bootstrap/MonicaConfigurationInputPlanEfCoreBuilderExtensions.cs`. Basic composition: `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs`. Tests: `tests/Test.Monica.Configuration/Bootstrap/MonicaConfigurationInputPlanTests.cs` and `tests/Test.Monica.Configuration.EfCore/DatabaseConfigurationInputPlanTests.cs`.
