# Monica Ordering Reference

A runnable ordering application with an Interactive Server UI, normal ASP.NET Core hosting, and domain-first ownership.

The sample is intentionally more realistic than a hello-world endpoint:

- one composition-only AppHost and one ordering bounded context
- one product presentation library with its own layout, shared themes and dialogs, and no business UI modules
- stable request, response, endpoint, and event contracts in `Platform.Protocol`
- rich order invariants owned by the entity
- a replaceable in-memory repository for zero-infrastructure startup
- request-owned API contracts exposed by command and query `ApplicationService` units
- a schema-first `[Configuration]` ProjectUnit consumed through `IOptions<OrderingOptions>`
- an operation scope and unit-of-work boundary shared by Local and HTTP entry points
- an `EventOrderApproved` local event published after the demo's dictionary update
- an auto-discovered `LocalEventHandlerOrderApproved` reaction
- an auto-discovered recurring backlog report backed by the in-memory job scheduler
- Swagger, ProjectUnit inspection, Prometheus metrics, and a health response

No database, broker, cache, or other external service is required. Runtime configuration documents are stored under the application output directory, while the repository and scheduler metadata reset with the process or rebuild output.

## Run

From the Monica repository root:

```bash
dotnet run --project examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Monica.Reference.Api.csproj
```

The launch profile listens on <http://localhost:5275>. Open the product UI at <http://localhost:5275/orders>. This route is discovered from the presentation assembly without a Shell menu registration. It creates and approves orders through generated `IOrderingCommandApi` / `IOrderingQueryApi`, with a fresh operation scope per call. Release runs the same UI through Local RPC without business HTTP endpoints. Debug, or an explicitly enabled `MONICA_HTTP_ENDPOINTS` compilation symbol, additionally publishes the owned operations over HTTP. The former root JSON overview is at `/overview`.

To include the optional Monica operations cockpit in the same host, pass `--ReferenceUI:EnableDiagnostics=true`. Product pages retain `OrderingLayout`; `/module-system-dashboard` uses the default Monica Shell. Both share one theme and overlay provider owner. The demo's use cases intentionally have no authorization attributes and are public; production applications configure their real identity and policies. The process-local repository still has no transactional rollback.

`src/Presentation/Monica.Reference.UI` references only `Platform.Protocol` and `Monica.Application.UI`. It has no reference to domain implementation or Platform.Infrastructure. Layout and interaction models stay in presentation; use-case orchestration stays in `Domains.Ordering`. This example demonstrates the server-side profile and does not claim browser/WASM-compatible Protocol dependencies.

The repository starts with two draft orders. The following HTTP examples require the default Debug run or the explicit HTTP symbol:

```bash
curl --request POST http://localhost:5275/api/v1/Ordering/queries/GetOrders --header 'Content-Type: application/json' --data '{}'
```

Create an order:

```bash
curl --request POST http://localhost:5275/api/v1/Ordering/commands/CreateOrder \
  --header 'Content-Type: application/json' \
  --data '{"orderNumber":"MON-1003","customerName":"Adventure Works","total":1299.90}'
```

Approve it using the identifier returned by the create request:

```bash
curl --request POST http://localhost:5275/api/v1/Ordering/commands/ApproveOrder \
  --header 'Content-Type: application/json' \
  --data '{"orderId":"REPLACE_WITH_ORDER_ID"}'
```

Explore the running architecture:

- Swagger UI: <http://localhost:5275/swagger>
- Monica ProjectUnit catalog: <http://localhost:5275/framework/units>
- Prometheus metrics: <http://localhost:5275/metrics>
- Readiness response: <http://localhost:5275/health>
- Liveness response: <http://localhost:5275/alive>

`OrderingOptions.MaximumOrdersReturned` limits the collection response. The values in `appsettings.json` seed Monica's local effective-value store on first startup, and the query consumes the resulting configuration through `IOptions<OrderingOptions>`.

The backlog worker runs once per minute. Its execution history and normal application log report the draft count, escalating to a warning when `OrderingOptions.BacklogWarningThreshold` is reached.

## Request-owned API contracts

Published endpoints are declared beside their request contracts in `Platform.Protocol`, not on the handlers that execute them:

```csharp
[Monica.Application.Annotations.ApiEndpoint(
    Monica.Application.Annotations.ApiHttpMethod.Post, "commands/CreateOrder")]
public sealed record CommandCreateOrder(
    string OrderNumber,
    string CustomerName,
    decimal Total) : IResultRequest<OrderDto>;
```

The request generator emits stable domain interfaces without WebApi configuration. AppHost's `ApiGenerationConfig.cs` selects Local clients and explicitly owns the Ordering handler assembly; its conditional `WebApiGenerationConfig` adds HTTP receivers. Debug hosts can reference Release-built contracts and handlers. The single ApiEndpoint attribute explicitly declares the sample's POST/body routes under `api/v1/Ordering/commands` or `queries`; Local calls retain the same HTTP metadata. Query execution remains read-only regardless of its HTTP verb.

The current RPC registration methods remain unchanged. This host selects Ordering through `ConfigDomainInfoProvider` and `UseLocalTransport`; it needs no remote destination. Application UI captures the circuit caller and creates a scope per outer call. Nested application calls reuse that scope and UnitOfWork. The reference repository is process-local and cannot demonstrate persistence rollback; framework integration tests exercise that contract with SQLite.

## Host-bound module graph

The AppHost declares its Configuration inputs once, then records its runtime capabilities in one `AddMonica` callback. The immutable plan keeps the selected store, source order, and section-path convention consistent wherever startup configuration and runtime composition need them:

```csharp
var configurationInputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
    .UseFileConfigurationStore());

builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options =>
    {
        options.ProjectName = "Monica.Reference.Api";
        options.AppName = "Monica Ordering Reference";
        options.AppId = "monica-ordering-reference";
        options.DomainName = "Ordering";
    });
    monica.ConfigureModuleSystem(options =>
    {
        options.DefaultApiGroupName = "Ordering";
        options.EnableMinimalApiByDefault = true;
    });
    monica.ConfigureTypeDiscovery(options =>
        options.Add("Domains.Ordering", "Platform.Protocol"));

    monica.AddHealthCheck();
    monica.AddConfiguration(configurationInputPlan);
    monica.AddEventBus()
        .UseNoOpDistributedEventBus();
    monica.AddResultEnvelope();
#if DEBUG || MONICA_HTTP_ENDPOINTS
    monica.AddWebApi();
#endif
    monica.AddRpcClient().ConfigDomainInfoProvider(new OrderingRpcDomains()).UseLocalTransport();
    monica.AddApplicationUI();
    monica.AddUIHost(options =>
    {
        options.DefaultLayout = typeof(OrderingLayout);
        options.AddPageAssembly(typeof(OrderingLayout).Assembly);
    });
    monica.AddLocalization(options => options.AddResource<OrderingUIResource>());
    if (builder.Configuration.GetValue<bool>("ReferenceUI:EnableDiagnostics"))
    {
        monica.AddModuleSystemUI();
    }
#if DEBUG || MONICA_HTTP_ENDPOINTS
    monica.AddSwagger(options =>
    {
        options.AppName = "Monica Ordering Reference";
        options.ApiVersion = "v1";
    });
#endif
    monica.AddUnitOfWork();
    monica.AddProjectUnits(options =>
    {
        options.ConventionOptions.EnableNameConvention = true;
        options.ConventionOptions.NameConventionMode = ENameConventionMode.Strict;
    });
    monica.AddJobScheduler(options =>
        {
            options.MaxWorkerExecutionThreads = 1;
        })
        .UseInMemoryStore()
        .UseSchedulerScope("monica-reference-ordering");
    monica.AddOpenTelemetry()
        .UsePrometheusEndpoint();
});
```

The no-op distributed provider makes the external integration boundary explicit without introducing a broker. `EventOrderApproved` is dispatched through the real in-process local bus. The sample publishes after its process-local dictionary update. Durable applications use the transactional outbox; the sample does not provide durable publication or rollback.

JobScheduler UI is not composed by this sample. The optional operations cockpit currently exposes the module-system dashboard. The host synchronizes its local scheduler owner snapshot and schedules and executes the recurring job; its activity remains observable through scheduler execution history and logs.

Production stores recover abandoned leases with at-least-once execution and fence stale scheduler mutations. A job must still make its own external side effects idempotent or transactional because lease fencing cannot provide exactly-once business effects.

## Architecture

```text
src/
├── AppHost/Monica.Reference.Api/       # composition only
├── Presentation/Monica.Reference.UI/  # product layout, pages, forms, localization
├── Domains/Ordering/                   # use cases, events, job, entity, repository
└── Shared/
    ├── Platform.Infrastructure/        # solution-owned infrastructure boundary
    ├── Platform.Protocol/              # stable business language
    └── Platform.BuildingBlocks/        # project-common Monica capabilities
```

The project-reference direction is deliberately one-way:

```text
AppHost -> Ordering -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks
AppHost -> Monica.Reference.UI -> Platform.Protocol
```

The presentation project also references `Monica.Application.UI` for hosting and shared UI infrastructure. It does not reference the Ordering implementation. That shape gives humans and coding agents a predictable answer to “where does this change belong?” while keeping the small deployment in one process.

## Package maturity

This reference stays entirely on Monica's Stable path:

| Tier | Used here | Guidance |
| --- | --- | --- |
| Stable | Core composition, Configuration, EventBus, Web API, UnitOfWork, ProjectUnits, JobScheduler, repository abstractions, OpenTelemetry | Appropriate defaults for an application foundation |
| Integrations | None | Add a database, broker, cache, or service provider only when the deployment has chosen it |
| Labs | None | Evaluate runtime AI, RAG, MCP, DataChannel, DevOps, Office, and profiling packages explicitly |

`RepositoryOrder` is the intentional persistence seam. A production application can replace it with an Integration-tier provider without moving order rules out of the domain entity or changing the public requests. Because this sample repository is process-local rather than transactional, UnitOfWork demonstrates scoped request coordination only; it cannot roll back in-memory mutations. See the repository migration guide for a relational context and outbox integration.
