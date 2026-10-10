# Managed options and validation

Register `AddConfiguration(plan)` using an [input plan and store](bootstrap-and-stores.md), then declare a concrete options owner with `[Configuration]`. The module discovers annotated types in the host's configured type-discovery scope and registers Microsoft options binding and validation for the default options name.

The root options owner must be a concrete closed class with a public instance parameterless constructor, matching production options creation; the CLR class itself need not be public. A missing construction contract fails local discovery with a safe Contract fault (`Stage="options-constructor"`) before options registration, definition publication, or effective-value seeding under both validation policies. Nested object binding retains its existing construction semantics.

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;

[Configuration("Orders", DefinitionKey = "orders",
    ReloadBehavior = ConfigurationReloadBehavior.RequiresRestart)]
public sealed class OrdersOptions : IValidatableObject
{
    [ConfigurationKeyName("minimum"), Range(1, 1000)]
    public int MinimumBatchSize { get; set; } = 10;

    [ConfigurationKeyName("maximum"), Range(1, 1000)]
    public int MaximumBatchSize { get; set; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinimumBatchSize > MaximumBatchSize)
        {
            yield return new ValidationResult(
                "Minimum batch size must not exceed maximum batch size.",
                [nameof(MinimumBatchSize), nameof(MaximumBatchSize)]);
        }
    }
}
```

The explicit section path `Orders` takes precedence over the default short CLR type-name convention. The definition key `orders` is a separate persisted identity; its default is the CLR full type name. Facade calls use the definition key. Configuration keys and logical paths use aliases: the first property's path is `minimum`, while `ValidationResult.MemberNames` must contain its immediate **CLR property name**, `MinimumBatchSize`. Return `nameof(...)` for each associated member; do not return aliases or dotted paths. A memberless result belongs to the object path. An unknown member is a contract fault. Each associated location produces an issue whose `LogicalPaths` includes all related locations.

## Rule ownership and binding

Use pure, synchronous `IValidatableObject` rules for relationships within the declared object. Rules receive a value-only `ValidationContext`; they have no DI service provider, asynchronous extension, or remote execution facility. Avoid I/O and service resolution. Monica walks nested declared objects, list items, and dictionary values, retaining each owned path even when objects share references. Undeclared derived shapes and recursive values are contract faults.

Portable constraints support the exact standard `Required`, `Range`, `RegularExpression`, `MaxLength`, `MinLength`, and `StringLength` attributes, plus enum allowed values. Unsupported or custom `ValidationAttribute` types are rejected during scanning; implement a pure object rule instead. When materialization succeeds, portable failures and independent object-rule failures can both be reported. Structural or conversion failures must not run rules against a partially converted object.

Binding uses configuration aliases and provider order, not JSON serializer property names. It preserves missing values, explicit nulls, empty containers, constructor defaults, and ordinary provider array-index precedence. Empty or null higher-priority containers can clear a lower contribution. Validation uses the same materializer as production managed options; [bootstrap snapshots](bootstrap-and-stores.md) share this binding behavior but have a separate validation boundary.

`[OptionSetting]` adds stable node identity, sensitivity, editor hints, and reload behavior. Sensitive code diagnostics suppress user rule messages whenever the owning object has a sensitive subtree or an associated path is sensitive. Code and execution diagnostics do not expose candidate values. Keep ordinary rule messages value-free as well.

## Coverage and local authority

Read coverage and scope before interpreting a report:

| Contract | Meaning |
| --- | --- |
| `Complete` | The complete applicable portable and locally owned object contract was evaluated; it can still be invalid. |
| `SchemaOnly` | Only a fragment or the available portable schema was evaluated. No issues do not prove the complete object is valid. |
| `Failed` | A structural, binding, contract, or execution problem prevented complete validation. |
| `Fragment` | A scope-only editor check without the full owner and physical target. |
| `CompleteAggregate` | A complete source or candidate aggregate. Candidate reports also distinguish stored and effective targets. |
| `ActualOptions` | One actual default-options instance after Configure and PostConfigure. |

`IsSchemaValid` means no schema issues and no Failed coverage; it may be true when code rules fail or only schema coverage is available. `IsValid` requires Complete coverage and no issues. Aggregate source reports also require every `DefinitionReports` entry to be valid. Never infer success from `IssueCount == 0` alone.

`ConfigurationDefinition.ValidationContract` publishes `Capability` (`Unknown`, `PortableOnly`, or `ObjectCode`) and an executable `Revision` independent of the structural `SchemaHash`. Local discovery owns executable types. Published `ClrTypeName` metadata never authorizes loading code. A published PortableOnly contract can be complete only with an affirmative revision and complete portable shape. Without a local descriptor, ObjectCode or Unknown metadata remains SchemaOnly and cannot authorize a complete write or rollback. Metadata that impersonates a local descriptor with a mismatched origin, schema, section, or executable contract causes a contract fault. There is no owner-validation transport. Legacy metadata defaults to Unknown with no revision.

## Startup and actual options policy

`ModuleConfigurationOption.RuntimeValidationBehavior` defaults to `DiagnosticOnly`:

| Boundary | DiagnosticOnly | FailFast |
| --- | --- | --- |
| First seed has ordinary schema/code failures | Persist the faithfully materialized seed and report a warning. | Reject before creating the effective-value document. |
| Existing effective values are invalid | Report the failure; do not rewrite the document or version during startup. | Reject startup; leave the existing document and version untouched. |
| An actual default-options instance is invalid after Configure/PostConfigure | Record the invalid attempt and allow resolution. | Record the attempt and throw `OptionsValidationException`. |
| Binding, contract, or validation execution faults | Fatal. | Fatal. |

`ConfigurationValidationExecutionException` identifies the owner, safe logical path, kind, and fixed stage. It discards user exception messages and causes, including exceptions raised while enumerating validation results. It is fatal under both policies. Actual-options diagnostics record a Failed attempt before rethrowing. Facade operations return safe failures; do not parse exception text to recover a report. Duplicate resolved section paths also fail by default; select Warning only for intentional overlap.

## Distinguish source reports from observed attempts

`IConfigurationRuntimeValidationService.GetReport()` and `ConfigurationFacade.GetRuntimeValidationReportAsync()` validate the current effective source stack. Source reports are cached until the root reload revision or successful Monica projection changes. A source report does not include consumer Configure/PostConfigure changes and does not create options.

`IConfigurationOptionsValidationDiagnostics.GetLatestReport(definitionKey)` and facade `GetOptionsValidationReportAsync(definitionKey)` return the latest observed default-options attempt, or null when none has been observed. `GetReports()` / `GetOptionsValidationReportsAsync()` list the latest attempt per observed local owner. Reads never resolve options to manufacture an observation. The bounded observer retains one immutable report per discovered owner, with `AttemptId`, `AttemptedAt`, revision, coverage, and safe issues; it retains neither the options object nor source JSON. Named options other than the default are not observed by this managed validator.

For example, PostConfigure can make the 10..20 source into an actual 10..5 instance. The source report remains valid while the observed actual-options report is invalid. A null actual report means unobserved, not valid. The latest attempt represents one creation, including a scope-specific attempt, rather than every cached consumer.

`IOptions<T>` retains its created value; diagnostics do not replace it. `IOptionsMonitor<T>` can create a new attempt after reload, and `IOptionsSnapshot<T>` follows its normal scope lifetime. DiagnosticOnly may expose newly invalid monitor values. This capability does not add a last-known-good cache, reversible root reload, or automatic process termination. Use `RequiresRestart` or `StaticAfterStartup` for values whose consumers cannot apply online changes; `OnlineReloadable` requires an actual consumer reload strategy.

## Validate a supplied instance

Use the singleton `IConfigurationOptionsInstanceValidationService.Validate<TOptions>(options)` when an application needs the complete native report for the exact object it will use. Register `AddConfiguration(plan)` and complete local discovery first. `TOptions` and the object's runtime type must exactly match a locally discovered owner; a base type, derived object, or published metadata alone does not authorize execution. An undiscovered owner produces a safe Contract fault at the root with `Stage="local-options-owner"`.

The synchronous call returns a `ConfigurationValidationReport` with ActualOptions scope, the local executable revision, and one owning `DefinitionReports` entry. It uses the same portable and pure object rules as managed options validation, without resolving or creating options, rebinding, applying Configure/PostConfigure callbacks, or modifying the object. Ordinary violations return an invalid report under either runtime policy; safe Contract and Execution faults propagate. The report excludes actual values and source provenance.

An application can explicitly enforce its own startup boundary after obtaining its configured value:

```csharp
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;

public sealed class OrdersStartupGate(
    IOptions<OrdersOptions> options,
    IConfigurationOptionsInstanceValidationService validation)
{
    public void EnsureValid()
    {
        var report = validation.Validate(options.Value);
        if (!report.IsValid)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(OrdersOptions),
                report.Issues.Select(issue => issue.Problem));
        }
    }
}
```

Here `IOptions.Value` runs the ordinary Configure/PostConfigure pipeline when creating its value, or supplies its already cached value. Invoke the gate before the application registers or uses resources that depend on it. The explicit service validates that supplied value directly; it does not change DiagnosticOnly into FailFast for the host or register an always-enforcing Microsoft options validator.

Explicit checks do not write the creation observer, create an `AttemptId`, or turn NoObserved into an observed creation. A concurrent or later options-creation attempt may replace the latest diagnostic without changing the report returned by this call. Consequently, do not use `GetLatestReport` as proof that a particular cached instance is valid. This instance report also does not authorize persistence: use the complete [mutation preview and apply contract](runtime-changes.md) for a managed write.

Consumers migrating from schema-only behavior must ensure root options owners have public parameterless constructors, update fragment success checks from `IsValid` to the explicitly limited `IsSchemaValid`, use complete [mutation previews](runtime-changes.md) before saving, handle incomplete authority and typed operational faults, and display coverage separately from validity. Review existing object rules before adopting FailFast across an entire host.

Source contracts: `Monica.Configuration/Annotations/ConfigurationAttribute.cs`, `Monica.Configuration/Annotations/OptionSettingAttribute.cs`, `Monica.Configuration/Models/ConfigurationValidationContract.cs`, `Monica.Configuration/Models/ConfigurationValidationReport.cs`, `Monica.Configuration/Models/ConfigurationOptionsValidationReport.cs`, `Monica.Configuration/Abstractions/IConfigurationOptionsValidationDiagnostics.cs`, `Monica.Configuration/Abstractions/IConfigurationOptionsInstanceValidationService.cs`, and `Monica.Configuration/Modules/ModuleConfiguration.cs`.

Behavioral checks:

- `tests/Test.Monica.Configuration/Services/Support/ConfigurationObjectValidationWalkerTests.cs`: aliases, multiple members, nested ownership, sensitive diagnostics, faults, and remote authority.
- `tests/Test.Monica.Configuration/Binding/ConfigurationObjectMaterializerTests.cs`: shape and provider precedence.
- `tests/Test.Monica.Configuration/Modules/ManagedConfigurationObjectValidationTests.cs`: root-owner construction rejection before persistence, first seed, actual PostConfigure, policies, cached options, and reload.
- Its `ValidateSuppliedInstance_WhenItDiffersFromSourceAndPostConfigure_ShouldReportWithoutPolicyOrCreationSideEffects` and `ValidateSuppliedInstance_WhenItsClrTypeHasNoLocalOwner_ShouldRejectWithoutExecutingPublishedCode` checks cover explicit instance validation under both policies, observer/source preservation, and refusal of unavailable local authority.
- `tests/Test.Monica.Configuration/Services/ConfigurationRuntimeValidationServiceCacheTests.cs`: source cache.
