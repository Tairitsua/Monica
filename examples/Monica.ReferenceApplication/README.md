# Monica reference application

This application demonstrates a business host containing both example roles, a domain core, two domain-owned roles, and the existing Ordering business UI. This example uses the same compiled artifact for combined and split role placement; products can use selective artifacts for distinct runtime dependencies. The repository is intentionally process-local and in-memory; it does not demonstrate shared persistence or rollback across processes.

Run from the repository root:

```bash
dotnet run --project examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api --urls http://localhost:5275
```

Open [Ordering](http://localhost:5275/orders). The UI uses generated IOrderingCommandApi and IOrderingQueryApi interfaces, application operation scopes, its own OrderingLayout, Monica themes and localization. Pass `--ReferenceUI:EnableDiagnostics=true` to add the optional module and configuration diagnostic pages. This switch is a typed Startup configuration read before service registration: its running value is fixed until restart and never copied into the managed store. Configuration diagnostics show that running value separately from any changed source candidate. The root JSON overview is at `/overview`. This demo has public use cases without authorization attributes; production hosts configure real identity and policies.

## Ownership and project graph

```text
src/
  Domains/Ordering/
    Domains.Ordering/                 # Entity, repository, shared options
    Roles.Ordering/                   # Commands, collection query, local event handler
    Roles.Ordering.Reporting/         # Summary query and autonomous backlog worker
  Shared/
    Platform.Protocol/                # Requests, DTOs, events and stable identifiers
    Platform.BuildingBlocks/          # Shared Monica capabilities
    Platform.Hosting/                 # Host/provider composition dependencies
  Presentation/Monica.Reference.UI/   # Product layout, pages, forms and localization
  AppHost/Monica.Reference.Api/        # Complete native host
```

The Host references both roles, their core, Platform.Hosting and UI. Each role references only its own core and shared contracts through that core. UI references Platform.Protocol and Monica.Application.UI. Domain implementation does not reference Platform.Hosting or the role projects. There is no empty migration project for the in-memory repository.

Each core/role assembly declares ApplicationDomain or ApplicationRole. The Host's `[assembly: ApplicationHost]` generates the full manifest and per-operation callers/HTTP receivers from referenced handlers. Protocol independently generates stable domain interfaces from its ApiEndpoint requests. A role split does not change the published Ordering namespace or create a second query interface.

The native Program.cs composes AddApplicationModel, AddApplicationInvocation, AddWebApi and AddApplicationUI inside AddMonica. Monica reads the canonical Application section, selects the complete Deployments map, and projects the current entry's AppName before module configuration. The reporting assembly names its RoleRegistration on ApplicationRole; its active callback contributes the scheduler before the module graph closes. No host role-registration table is maintained. The host finishes with UseMonica, MapMonica and Run.

## Combined and split placement

The default appsettings assigns `ordering` and `ordering.reporting` to `monica-ordering-reference`. The reporting worker runs once per minute with in-memory scheduler state and the stable `ordering.reporting` workload owner, independently of AppId. The scheduler scope remains `monica-reference-ordering`.

To inspect split activation, replace the Application:Deployments map in appsettings.json with:

```json
{
  "monica-ordering-reference": { "AppName": "Ordering API", "Roles": ["ordering"] },
  "monica-ordering-reports": { "AppName": "Ordering reports", "Roles": ["ordering.reporting"] }
}
```

Run the ordinary host as above, then run the same project on another port with `--Application:AppId=monica-ordering-reports --urls http://localhost:5276`. Each AppId has the entire implementation; role selection controls autonomous work and HTTP ingress. The ordinary host still serves the UI's GetOrderSummary call locally while its reporting role is inactive. Only the reports owner composes the reporting scheduler, starts WorkerOrderBacklogReport and exposes the summary receiver. These demo processes have separate dictionaries, so their order data is independent. Production role placement requires the shared domain stores and credentials appropriate for ordinary local calls.

For a private environment input, set `Application:EnvironmentFiles` to its JSON path before AddMonica. A selected file can supply the complete topology and resource inputs; it must not contain the managed Ordering section. A higher-priority Deployments value replaces the whole map, including names and role lists. Missing roles, unknown roles, conflicting typed/file maps and a current AppId absent from the map fail startup.

No demo operation requires a process-owned resource, so none has RunOnRole and no remote provider is needed. A real constrained operation declares RunOnRole on its published request and configures the existing HTTP provider for remote owners. Product authority policy is evaluated before the Local decision, which also requires an included implementation. This example does not demonstrate selective host artifacts or the generated shared catalog. gRPC is unsupported.

## HTTP and operation scopes

Owned receivers are available in Release as well as Debug. Swagger is an optional Debug or MONICA_HTTP_ENDPOINTS diagnostic. ApiEndpoint owns the same HTTP mapping regardless of how an application caller is routed.

```bash
curl --request POST http://localhost:5275/api/v1/Ordering/queries/GetOrders --header 'Content-Type: application/json' --data '{}'
curl --request POST http://localhost:5275/api/v1/Ordering/commands/CreateOrder --header 'Content-Type: application/json' --data '{"orderNumber":"MON-1003","customerName":"Adventure Works","total":1299.90}'
curl --request POST http://localhost:5275/api/v1/Ordering/commands/ApproveOrder --header 'Content-Type: application/json' --data '{"orderId":"REPLACE_WITH_ORDER_ID"}'
```

An independent outer Local call captures the trusted caller and creates an operation scope; nested Local calls reuse it. The query is read-only independently of its HTTP verb. The entity protects its approval transition and RepositoryOrder materializes snapshots. The dictionary has no transactional rollback. EventOrderApproved is dispatched through the real in-process bus after the demo dictionary mutation; durable applications use the transactional outbox. The no-op distributed provider makes the external boundary explicit without adding a broker.

OrderingOptions.MaximumOrdersReturned limits collection queries and BacklogWarningThreshold controls worker warning logs. appsettings values seed Monica's local effective-value store on first startup. The worker reports through logs and scheduler execution history; this sample does not compose JobScheduler UI. Durable providers offer lease recovery and fencing, while jobs still make external effects idempotent or transactional.

Available diagnostics include [Swagger in Debug](http://localhost:5275/swagger), [ProjectUnits](http://localhost:5275/framework/units), [metrics](http://localhost:5275/metrics), [readiness](http://localhost:5275/health), and [liveness](http://localhost:5275/alive). The reference uses stable Monica capabilities with no external provider prerequisite. See the [unified application specification](../../skills/monica-application/references/03-unified-architecture.md) for boundaries, migration ownership and required execution roles.
