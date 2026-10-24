# Startup inputs and local execution

Use the same host, domain/role declarations and application placement in deployment and local development. The [unified architecture](03-unified-architecture.md) owns code boundaries and the `Application:Deployments` map. This reference selects configuration ownership and the local profile; native capability references own their execution APIs.

## Give each setting one authority

Application configuration ProjectUnits use `Options{Purpose}` and the matching filename. Keep each option beside its consumer: domain persistence in the domain core, role integration in its role, reusable product technical policy in Infrastructure, and whole-host choices in the host. Protocol contains no options or environment files. Monica and Microsoft option types keep their native names.

| Kind | Use for | Authority and runtime behavior |
| --- | --- | --- |
| Startup | Values read by composition; connection strings, credentials, provider choices, endpoints and process/site identity | Standard host configuration, frozen until restart; visible read-only in configuration diagnostics, with no managed document, publication or history |
| Managed, the `[Configuration]` default | Business settings read by runtime services, such as thresholds, codes and matching windows | Monica's effective store and reviewed change flow, with the definition's declared reload behavior |

A runtime business setting that requires restart still belongs to Managed when deployment does not own it. Startup is an authority and lifecycle choice, not a synonym for `IOptions<T>`. Do not collect all provider settings, application identity and role selection into one replacement startup object.

Declare Startup explicitly with `[Configuration("Ordering:Database", Kind = ConfigurationKind.Startup)]`. Supply host sources before `AddMonica`, then use `monica.GetStartupOptions<OptionsOrderingDatabase>()` or `context.GetStartupOptions<OptionsOrderingDatabase>()` from `Monica.Configuration.Bootstrap` during native composition. The first read validates immediately; discovered Startup types are also validated before composition finishes. No temporary service provider or managed-store read is needed. Managed options are consumed after DI through their normal runtime options interfaces.

Default-name `IOptions<T>`, `IOptionsSnapshot<T>` and `IOptionsMonitor<T>` return the same frozen Startup object; consumers treat it as read-only. Startup monitor callbacks do not signal live changes. Named options and ordinary Configure/PostConfigure or replacement options accessors cannot establish a second value. Invalid Startup binding or validation fails composition. Diagnostics distinguish actual frozen values from live candidates and mask sensitive values. See [typed options and validation](../../monica-infra-configuration/references/options-and-validation.md) and the [configuration unit template](../../monica-application-project-unit-development/references/16-configuration-template.md).

Validate dependencies at their owner. Omitted implementation assemblies need no unused credentials. An included optional provider can have a valid unconfigured shape, but selecting it must validate its prerequisites. Passive and manually callable operations still need their actual dependencies. Holding background work does not make invalid required connection settings usable.

When renaming an existing Managed type to `Options*`, pin its previous `SectionPath` and `DefinitionKey` explicitly before changing the CLR name. Converting a setting to Startup also requires moving its values into deployment inputs. It stops using its old managed representation; it does not delete historical publications or documents. Retire legacy managed data only after its consumers have migrated, through the existing explicit maintenance flow.

## Select the connected environment with files

`Application:EnvironmentFiles` is the one ordered list of private JSON inputs describing the environment this process joins. Paths resolve against the host content root; every selected file must exist. Under the standard .NET host, priority from low to high is:

1. `appsettings.json` and `appsettings.{Environment}.json`.
2. Selected environment files, in list order.
3. Development user secrets, environment variables, then command-line arguments.

The host environment name still controls Development features. Selecting a UAT resource file does not require changing `ASPNETCORE_ENVIRONMENT` from Development. Supply the list in existing host inputs or a launch profile; environment files cannot redefine it or recursively include files.

Both the file list and `Application:Deployments` are complete structural inputs. The highest-priority provider that explicitly supplies either selects its entire list or map. A one-file override removes lower trailing entries; a local topology replaces every lower application and role array. Empty or invalid input does not request fallback. Use JSON for a complete topology and scalar environment variables for current AppId and AutonomousWork. The [deployment contract](03-unified-architecture.md#deployment-and-active-resources) defines validation and the alternative complete typed map.

Environment files may contain Startup settings, Application keys other than EnvironmentFiles, and non-Monica sections such as Logging. They must not contain paths owned by Managed definitions, even when a higher source would hide them. Keep Managed seed defaults in their normal host inputs or managed input plan. Environment files remain deployment-owned and read-only in the configuration UI.

Accepted file reloads can update live consumers and candidate diagnostics. Startup values, application identity, placement and the selected file list remain fixed until restart. A malformed, missing or ownership-invalid reload retains the last accepted contribution and reports a safe diagnostic. See [host input composition](../../monica-infra-hosting/references/host-composition.md#select-startup-inputs) for custom-builder insertion boundaries and the [store boundary](../../monica-infra-configuration/references/bootstrap-and-stores.md).

Commit only safe templates. Each repository documents its private, gitignored local file location and deployment mounts; do not copy credential-bearing files from Protocol into every consumer's output or add a silent development fallback for a missing mount.

## One local host profile

For a host containing both example Ordering roles, a launch profile can select a private file and hold unsolicited work:

```json
{
  "profiles": {
    "Product.Host (UAT)": {
      "commandName": "Project",
      "launchUrl": "swagger",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "Application__EnvironmentFiles__0": "Configurations/uat.local.json",
        "Application__AppId": "product-local",
        "Application__AutonomousWork": "false"
      }
    }
  }
}
```

The private file supplies resource settings and this complete placement:

```json
{
  "Application": {
    "DeploymentScope": "product-uat",
    "Deployments": {
      "product-local": {
        "AppName": "Product local development",
        "Roles": ["ordering", "ordering.reporting"]
      }
    }
  }
}
```

List the actual included roles explicitly. A template generator may derive that list from compiled metadata, but the resulting file uses the ordinary deployment schema. There is no runtime `all` role. Adding a role requires its placement assignment. A deliberately omitted implementation remains under an explicit remote application entry; the local host cannot expose it by configuration.

A copied cluster topology that lacks `product-local` fails instead of joining another application implicitly. Switching connected environments selects another private file. Debug and Release use the same runtime policy; diagnostics exposure is an explicit Startup option, independent of compiler symbols and of permission to execute background work.

Monica groups actual mapped API operations by role in Swagger. Framework endpoints retain their framework document, and unowned business endpoints use the default business document. A full local topology exposes its included role groups. Grouping neither grants authorization nor activates a remotely placed receiver. See [Swagger integration](../../monica-infra-web/references/api-documentation-and-visibility.md) for its native setup.

## Placement and automatic initiation are separate

`Application:AutonomousWork` defaults to true, including in a standalone Monica host. A local profile sets it to false. Monica derives these modes per autonomous resource; applications do not configure three more lists:

| Derived mode | Resource ownership | Behavior |
| --- | --- | --- |
| Disabled | Its role is placed elsewhere or unavailable locally | No local autonomous execution or manual ownership bypass |
| Manual | Its owner is eligible here and AutonomousWork is false | Retain supported definitions and explicit execution; hold unsolicited shared work |
| Automatic | Its owner is eligible here and AutonomousWork is true | Normal native lifecycle and scheduling |

Included ordinary handlers, repositories, passive services and in-operation domain/local events remain usable regardless of role placement. HTTP receivers require local implementation and placement. Autonomous initiation also requires Automatic mode, except the narrow initialization case below. `RunOnRole` and authority policies retain their existing meaning.

An included domain or role runs `ConfigurePassive`; a locally placed owner also runs `ConfigureActive` in Manual mode. Declare resources through native module APIs so Monica can retain them for diagnostics or explicit execution. Guard imperative shared participation, such as adding a backplane, with `context.IsAutonomous`. Constructors and registration callbacks register dependencies; they never open connections, start tasks or join an environment. See [native registration](03-unified-architecture.md#native-domain-and-role-registration).

`[ProcessLocal]` in `Monica.Core.TypeDiscovery.Annotations` is limited to finite Seeder/hosted initialization needed by included passive behavior and changing process-owned memory only. Read-only input access is allowed; distributed cache writes, publication, shared claims, service advertisement and continuous loops are not. It can run independently of placement and AutonomousWork. It does not apply to jobs, subscriptions, DataChannel or Outbox, and marking a registration class does not exempt its resources. A ProcessLocal initializer blocked by a Held or Disabled prerequisite cannot automatically start that prerequisite. Split the work or satisfy an eligible dependency explicitly; the dependent operation remains unready meanwhile.

## Use each capability's existing explicit operation

| Capability | Manual behavior and action |
| --- | --- |
| Jobs | A fresh process-local memory store holds definitions and explicit executions. The configured shared store is not resolved by this scheduler. Native run-now or typed/API triggers execute locally with their normal delay/retry behavior; no recurring generation, shared claims or automatic maintenance. See [jobs](../../monica-infra-jobs/references/scheduler.md). |
| Seeders | Eligible work is Held. Run the selected seeder and its unsatisfied eligible dependencies through native controls; Disabled prerequisites and overlapping runs fail admission. See [startup seeding](../../monica-infra-jobs/references/startup-seeding.md). |
| Distributed subscriptions | Subscriptions remain Held without broker consumption or offset changes. Deliver a test event to an explicit eligible local target set through the native receive pipeline, preserving transaction and Inbox behavior. See [event delivery](../../monica-infra-messaging/references/event-bus.md#manual-delivery-and-held-subscriptions). |
| Hosted services and DataChannel | Held without a generic run-once operation. Test bounded business steps through their existing operations. |
| Outbox | Background scan, claim, delivery and cleanup are held per DbContext. Transactional staging remains available. |
| Service discovery | Registry reads remain available; no self-registration or heartbeat. |
| Observability | Local diagnostics and explicit scraping remain available; Monica-configured OTLP export is held. |
| Managed configuration | Read existing documents without startup publication, creation, retirement or repair. Missing documents yield validated unpersisted values. Explicit edits require the native reviewed effective-store flow and matching persisted authority; external JSON, purge and saved-version deletion remain unavailable. See [Manual configuration changes](../../monica-infra-configuration/references/runtime-changes.md#manual-managed-values). |

Enqueue success is not job completion. Held is not seeder success or proof of resource readiness. Intentionally unrequested Held seeders do not block host startup readiness, but ProcessLocal requirements and explicitly admitted runs retain Required/Optional failure semantics. A Required manual failure remains unhealthy until a successful explicit retry. APIs depending on a held socket, channel or initializer report the unmet prerequisite; they must not silently start it or forward elsewhere to hide the condition.

The startup held-resource log and module workbench expose the same resource observations. Runtime modules may update those observations, so the workbench refreshes them independently of the immutable startup snapshot. Admission state does not establish successful execution; use the owning module's state and history for that evidence.

Manual mode controls unsolicited participation, not the side effects of an explicit operation. API calls, seeder runs, jobs and test-event handlers can write the selected environment's databases or publish events. Outbox rows they commit may be delivered by that environment's workers. Test-event delivery does not publish its input to the broker, but handler effects remain real; new deliveries use fresh message IDs and deliberate replay preserves the original ID for normal deduplication. Use isolated resource endpoints when strict isolation is needed. Keep shared lock/state identities compatible with the environment rather than changing only a local namespace and breaking coordination.

The repository specification records its actual local profile, selected resources, included roles and verified capability coverage. The [reference application](../../../examples/Monica.ReferenceApplication/README.md) supplies a minimal working host; it does not require every application to add a job, seeder or subscription merely to fill the template.
