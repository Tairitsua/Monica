# Configuration Template

Use a Configuration ProjectUnit for actual configurable behavior. Name the application-authored type `Options{Purpose}` and its file `Options{Purpose}.cs`; Monica and Microsoft option types retain their native names. Keep options narrow and beside the consuming domain, role, reusable infrastructure capability or host. Protocol contains no configuration definitions.

## Select ownership before the accessor

- Use `ConfigurationKind.Startup` for values read during composition or selecting connections, providers, endpoints and process/site identity. Declare an explicit section path. Values come from standard host configuration and remain frozen until restart.
- Managed is the default for business settings consumed after DI. Its store and declared reload behavior control changes. A business setting can remain Managed even when applying a change requires restart.
- Runtime consumers use the appropriate `IOptions<T>`, `IOptionsSnapshot<T>` or `IOptionsMonitor<T>`. Those default-name interfaces all return the same frozen object for Startup; the accessor does not determine configuration ownership.
- ProjectUnit diagnostics report Startup values as requiring restart even with snapshot/monitor consumers or no discovered consumer. Accessor-based reload inference applies only to Managed definitions.
- Composition reads only Startup options through `GetStartupOptions<T>()` on its Monica builder or registration context. Do not resolve options from a temporary service provider or use a managed store as startup authority.

Use the shared [startup and local execution contract](../../monica-application/references/04-startup-and-local-execution.md) for environment files and local profiles. The infrastructure [options reference](../../monica-infra-configuration/references/options-and-validation.md) owns binding, validation, sensitive values and reload semantics.

## Managed business settings

This example belongs in `Domains.Ordering/Configurations/OptionsOrderProcessing.cs` and uses stable explicit identities. Add only applicable actual requirement annotations under the [metadata rules](03-project-unit-context-metadata.md).

```csharp
using System.ComponentModel.DataAnnotations;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.ProjectUnits.Annotations;

namespace Domains.Ordering.Configurations;

[Configuration("Ordering:Processing", DefinitionKey = "ordering.processing",
    ReloadBehavior = ConfigurationReloadBehavior.OnlineReloadable)]
[ProjectUnitMetadata("Order processing settings",
    Description = "Controls order approval behavior and batch size.",
    Tags = ["ordering", "approval"])]
public sealed class OptionsOrderProcessing
{
    public bool AutoApproveEnabled { get; set; }

    [Range(1, 1000)]
    public int ApprovalBatchSize { get; set; } = 100;
}
```

An operation-scoped consumer can use `IOptionsSnapshot<OptionsOrderProcessing>` for a consistent value during its scope. A long-lived consumer that supports live changes uses `IOptionsMonitor<T>`; a fixed-value consumer uses `IOptions<T>` and requires restart for changes to reach that instance. Keep the definition's declared behavior compatible with its consumers. Reading a setting does not justify a forwarding DomainService by itself; consume it where the actual domain rule or orchestration lives.

## Startup persistence settings

The domain owns this connection setting because its passive repository needs it whenever included. An optional provider instead needs a valid unconfigured shape and checks its prerequisites when selected; do not make every possible connection globally required.

```csharp
using System.ComponentModel.DataAnnotations;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.ProjectUnits.Annotations;

namespace Domains.Ordering.Configurations;

[Configuration("Ordering:Database", Kind = ConfigurationKind.Startup)]
[ProjectUnitMetadata("Ordering database connection",
    Description = "Selects the database used by Ordering persistence.",
    Tags = ["ordering", "persistence"])]
public sealed class OptionsOrderingDatabase
{
    [Required, OptionSetting(IsSensitive = true)]
    public string ConnectionString { get; set; } = "";
}
```

Inside the existing domain's `ConfigurePassive(ApplicationRegistrationContext context)`, use:

```csharp
using Monica.Configuration.Bootstrap;
using Domains.Ordering.Configurations;

var database = context.GetStartupOptions<OptionsOrderingDatabase>();
// Use database.ConnectionString in this domain's existing provider registration.
```

The value is validated immediately and shared with later default-name options injection. Consumers must not mutate it. Every discovered Startup definition is validated before composition completes, even when not explicitly read there. File reloads produce candidates for inspection, not a new running connection object. Keep canonical AppId, AppName, DeploymentScope and role placement in `Application` rather than duplicating them in an options class.

## Preserve persisted identity during a rename

Before renaming a Managed type or namespace, determine its previously resolved section path and definition key. Pin both explicitly using `[Configuration("<existing-section>", DefinitionKey = "<existing-key>")]`, then rename the type and file to the Options prefix. If the old key defaulted to the CLR full name, keep that old full name as the explicit key. Do not replace an issued identity with a cleaner new string as part of a naming change.

Converting Managed to Startup is a separate authority migration: move values into deployment inputs, migrate consumers, and preserve old metadata/history until the existing explicit maintenance flow can retire it. An Options rename alone changes neither value ownership nor reload behavior. Validate the same section and definition identity through configuration discovery, not only through a successful compile.
