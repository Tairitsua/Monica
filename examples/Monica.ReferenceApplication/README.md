# Monica reference application

This example contains one Ordering domain, two domain-owned roles, the product UI and one native executable. It uses the same compiled artifact for combined and split placement. Its repository is an in-memory dictionary; it does not demonstrate shared persistence or transactional rollback.

Run from the repository root:

```bash
dotnet run --project examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api --launch-profile Manual
```

`Manual` is the first and default local profile. It sets `Application__AutonomousWork=false` and enables the existing Startup diagnostics option. Open [Ordering](http://localhost:5275/orders) or [Swagger](http://localhost:5275/swagger). Both role APIs remain available, and the reporting job waits for an explicit native run. The `Automatic` profile uses the same application and permits its once-per-minute report:

```bash
dotnet run --project examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api --launch-profile Automatic
```

The UI uses generated `IOrderingCommandApi` and `IOrderingQueryApi` contracts, operation scopes, its Ordering layout, Monica themes and localization. The overview is at `/overview`. This demo has public use cases without authorization attributes; production hosts configure identity and policies.

## Ownership and composition

```text
src/
  Domains/Ordering/
    Domains.Ordering/                 # Entities, repository, OptionsOrdering
    Roles.Ordering/                   # Commands, collection query, local event handler
    Roles.Ordering.Reporting/         # Summary query, native role registration, backlog job
  Shared/Platform.Protocol/           # Requests, DTOs, events and stable identifiers
  Presentation/Monica.Reference.UI/   # Product layout, pages, forms and localization
  AppHost/Monica.Reference.Api/
    Composition/                     # Host modules, providers and overview endpoint
    Configurations/                  # OptionsReferenceUI
    Program.cs                       # Native host lifecycle
```

Create shared projects only when they contain real shared code. Protocol directly references Monica's application and event contract types and the API generator. The core, roles and host directly reference the capabilities they use. This example has no reusable product infrastructure, independent BuildingBlocks, shared testing library or migration project to extract. Four allowed Platform responsibilities do not require four empty projects.

Each core/role assembly declares `ApplicationDomain` or `ApplicationRole`. The host's `[assembly: ApplicationHost]` generates its manifest and operation callers/receivers from the referenced implementations. Protocol independently generates the stable Ordering API interfaces. The host includes both roles; each role references only its own core and published contracts. The UI references Protocol and Monica.Application.UI.

`Program.cs` keeps `AddMonica`, `Build`, `UseMonica`, `MapMonica` and `Run`. Its `Composition/ReferenceApplicationComposition.cs` owns the executable's module/provider choices and is reused by the host tests. Monica selects `Application:AppId`, validates the complete `Deployment:Applications` map and projects the selected `AppName`. The reporting assembly's `RoleRegistration.ConfigureActive` contributes JobScheduler before the module graph closes. No host role-registration table is maintained. Scheduler scope defaults to `Deployment:Scope`; the issued workload owner remains `ordering.reporting` when placement changes.

## Startup and Managed configuration

`OptionsReferenceUI` is Startup configuration under `ReferenceUI`. `EnableDiagnostics` selects Swagger, module diagnostics and configuration diagnostics before service registration. Changing it requires restart; its value never enters the managed store. The Manual and Automatic launch profiles enable it equally in Debug and Release. Without a launch profile, use `--ReferenceUI:EnableDiagnostics=true` when diagnostics are wanted.

`OptionsOrdering` is Managed configuration. Its section remains `Ordering` and its definition key remains `reference.ordering`, preserving the stored identity despite the CLR naming change. `MaximumOrdersReturned` limits collection queries and `BacklogWarningThreshold` controls reporting severity. An Automatic host publishes and seeds missing definitions into the local file store. A Manual host reads existing documents or uses validated, unpersisted appsettings defaults when they are absent; it does not create or publish a store just by starting. Configuration diagnostics explain read-only authority when an Automatic owner has not established the managed publication.

For a private environment input, select `Application:EnvironmentFiles` before `AddMonica`. One file may supply Startup sections, the complete `Deployment:Applications` map and connection data. It must not contain the Managed `Ordering` section. A higher-priority topology replaces the whole map, including names and roles. A listed file must exist; missing roles, unknown roles and an absent current AppId stop startup. The reference needs no external resources, so its ordinary local profile uses the complete topology already in appsettings.

## Combined and split placement

The default topology assigns `ordering` and `ordering.reporting` to `monica-ordering-reference`. To inspect split activation, replace `Deployment:Applications` in appsettings with:

```json
{
  "monica-ordering-reference": { "AppName": "Ordering API", "Roles": ["ordering"] },
  "monica-ordering-reports": { "AppName": "Ordering reports", "Roles": ["ordering.reporting"] }
}
```

Run the ordinary host, then run the same project on another port:

```bash
dotnet run --project examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api --launch-profile Manual -- --Application:AppId=monica-ordering-reports --urls http://localhost:5276
```

Both processes contain the implementations. Placement controls HTTP ingress and autonomous ownership; the ordinary host still serves the UI's `GetOrderSummary` call locally while its reporting role is remote. Only the reporting owner composes that role's scheduler and exposes its summary receiver. In Manual mode its job stays available for explicit execution; Automatic mode permits scheduling. These demo processes have independent dictionaries. A real split requires shared domain stores appropriate for ordinary local calls.

No demo operation depends on a process-owned resource, so none uses `RunOnRole` or needs a remote provider. A constrained operation declares `RunOnRole` on its published request and uses the native invocation provider. This example does not demonstrate selective host artifacts or an ownership catalog.

## APIs and explicit work

Swagger derives role documents from the actual mapped endpoints: `role.ordering` contains the ordinary API and `role.ordering.reporting` the summary query. The native framework document remains separate. The diagnostic switch does not change receiver placement, authorization or execution policy.

```bash
curl --request POST http://localhost:5275/api/v1/Ordering/queries/GetOrders --header 'Content-Type: application/json' --data '{}'
curl --request POST http://localhost:5275/api/v1/Ordering/commands/CreateOrder --header 'Content-Type: application/json' --data '{"orderNumber":"MON-1003","customerName":"Adventure Works","total":1299.90}'
curl --request POST http://localhost:5275/api/v1/Ordering/commands/ApproveOrder --header 'Content-Type: application/json' --data '{"orderId":"REPLACE_WITH_ORDER_ID"}'
```

Generated Local callers use the real operation pipeline. `EventOrderApproved` invokes the real local handler after the dictionary mutation; durable applications use a transactional outbox. The no-op distributed provider keeps this example free of broker prerequisites. Manual mode does not turn explicit API writes into dry runs.

The reporting job uses the native `JobSchedulerFacade.RunRecurringNowAsync` operation, with owner `ordering.reporting` and job key `typeof(WorkerOrderBacklogReport).FullName`. Its execution history records the real backlog result. This example does not add JobScheduler UI; the framework's native UI can expose the same operation when selected by a host. No replacement manual job engine or application debug switch is introduced.

## Verification

`tests/Test.Monica.Reference.Api` uses `MonicaTestApplicationFactory`, the production composition and the host's generated manifest. It verifies the Manual API create/list/approve flow and local event, configuration identity and read-only Startup behavior, and explicit execution of the real backlog job through the native local scheduler. The File provider receives an isolated test directory; business services and the job are not replaced. These tests belong to the root standard runner:

```bash
dotnet test tests/Test.Monica.Reference.Api -c Release
```

Diagnostics include [Swagger](http://localhost:5275/swagger), [ProjectUnits](http://localhost:5275/framework/units), [metrics](http://localhost:5275/metrics), [readiness](http://localhost:5275/health) and [liveness](http://localhost:5275/alive). See the [unified application specification](../../skills/monica-application/references/03-unified-architecture.md) for project boundaries and the shared local-development contract.
