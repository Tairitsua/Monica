using Monica.Configuration.Bootstrap;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.Reference.UI.Layout;
using Monica.Reference.UI.Localization;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

app.UseMonica();

app.MapGet("/overview", () => Results.Ok(new
{
    application = "Monica Ordering Reference",
    architecture = "Domain-first modular monolith",
    endpoints = new
    {
        product = "/orders",
#if DEBUG || MONICA_HTTP_ENDPOINTS
        orders = "/api/v1/Ordering/queries/GetOrders",
        approveOrder = "/api/v1/Ordering/commands/ApproveOrder",
        swagger = "/swagger",
#endif
        metrics = "/metrics",
        projectUnits = "/framework/units",
        readiness = "/health",
        liveness = "/alive"
    },
    runtime = new
    {
        configuration = "Ordering options are projected from the local Monica configuration store.",
        consistency = "Requests use operation scopes; the process-local demo repository has no transactional rollback.",
        approvalEvent = "The demo publishes locally after updating its dictionary; durable applications use the transactional outbox.",
        scheduler = "WorkerOrderBacklogReport runs every minute with in-memory scheduler state."
    }
}));

app.MapMonica();
app.Run();
