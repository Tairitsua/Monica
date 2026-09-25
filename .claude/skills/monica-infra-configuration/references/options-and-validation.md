# Managed options and validation

Register `AddConfiguration(plan)` with the host using an [input plan and store](bootstrap-and-stores.md) before consuming managed options.

Annotate concrete options with `[Configuration("Orders")]`; the explicit path is the highest-priority binding path. Without it, the default convention is the short CLR type name. The definition key is a separate persisted identity: by default it is the CLR full type name, so set `DefinitionKey` when it must survive a rename. `ConfigurationFacade` methods take that definition key, not the binding section path.

```csharp
[Configuration("Orders", DefinitionKey = "orders")]
public sealed class OrdersOptions
{
    [Range(1, 1000)]
    [OptionSetting(ReloadBehavior = ConfigurationReloadBehavior.OnlineReloadable)]
    public int BatchSize { get; set; } = 100;
}
```

`[OptionSetting]` adds node metadata such as stable `NodeKey`, sensitivity, editor hint, and reload behavior; ordinary .NET validation attributes define value constraints. The module discovers annotated types in the host's configured type-discovery scope and registers Microsoft options binding. Consumers use `IOptions<T>` for a stable value or `IOptionsMonitor<T>` when they need to observe reloads. The example declares `OnlineReloadable` only for a consumer that actually responds to reload; use `RequiresRestart` or `StaticAfterStartup` when that is the real lifecycle.

At runtime, `ModuleConfiguration` appends its effective-value provider after the host's bootstrap providers, activates that provider, validates effective values, and exposes source, history, mutation, rollback, and reload operations through `ConfigurationFacade`. A [bootstrap snapshot](bootstrap-and-stores.md) is a separate point-in-time read and does not publish metadata or write effective values.

`ModuleConfigurationOption.RuntimeValidationBehavior` defaults to `DiagnosticOnly`: invalid effective values produce a warning and report but do not prevent startup or managed options resolution. `FailFast` rejects invalid values at startup and subsequent options resolution. Store access, provider activation, registration, and binding errors remain fatal in both modes. Duplicate resolved section paths fail by default; change to `Warning` only when overlap is intentional. `IConfigurationRuntimeValidationService` and the facade expose diagnostic reports.

Definition identity and metadata: `Monica.Configuration/Annotations/ConfigurationAttribute.cs`, `OptionSettingAttribute.cs`, and `Monica.Configuration/Services/ConfigurationDefinitionScanner.cs`. Runtime options and activation: `Monica.Configuration/Modules/ModuleConfiguration.cs` and `Monica.Configuration/Services/Support/MonicaConfigurationProviderActivationCoordinator.cs`. Validation and activation checks: `tests/Test.Monica.Configuration/Services/ConfigurationRuntimeValidationServiceCacheTests.cs` and `tests/Test.Monica.Configuration/Services/Support/MonicaConfigurationProviderActivationCoordinatorTests.cs`.
