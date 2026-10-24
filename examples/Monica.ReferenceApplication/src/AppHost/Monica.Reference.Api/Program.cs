using Monica.ApplicationModel.Models;
using Monica.Configuration.Bootstrap;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.Reference.Api.Configurations;
using Monica.Reference.UI.Layout;
using Monica.Reference.UI.Localization;

var builder = WebApplication.CreateBuilder(args);
var appId = builder.Configuration["ReferenceAppId"] ?? "monica-ordering-reference";
var deployments = builder.Configuration.GetSection("Deployment:Applications")
    .Get<Dictionary<string, ApplicationPlacement>>()
    ?? throw new InvalidOperationException("Configure Deployment:Applications before starting the reference host.");

var configurationInputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
    .UseFileConfigurationStore());

builder.AddMonica(monica =>
{
    var diagnostics = monica.GetStartupOptions<ReferenceUIOptions>();

    monica.ConfigureApplication(options =>
    {
        options.ProjectName = "Monica.Reference.Api";
        options.AppName = "Monica Ordering Reference";
        options.AppId = appId;
        options.DomainName = "Ordering";
    });

    monica.ConfigureModuleSystem(options =>
    {
        options.DefaultApiGroupName = "Ordering";
        options.EnableMinimalApiByDefault = true;
    });

    monica.ConfigureTypeDiscovery(options =>
        options.Add("Domains.", "Roles.", "Platform.Protocol"));

    monica.AddApplicationModel(options => options.Deployments = deployments);
    monica.AddApplicationInvocation();

    monica.AddHealthCheck();
    monica.AddConfiguration(configurationInputPlan);
    monica.AddEventBus()
        .UseNoOpDistributedEventBus();
    monica.AddResultEnvelope();
    monica.AddWebApi();
    monica.AddApplicationUI();
    monica.AddUIHost(options =>
    {
        options.DefaultLayout = typeof(OrderingLayout);
        options.AddPageAssembly(typeof(OrderingLayout).Assembly);
    });
    monica.AddLocalization(options => options.AddResource<OrderingUIResource>());
    if (diagnostics.EnableDiagnostics)
    {
        monica.AddModuleSystemUI();
        monica.AddConfigurationUI();
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
    architecture = "One complete application with domain-owned roles",
    endpoints = new
    {
        product = "/orders",
        orders = "/api/v1/Ordering/queries/GetOrders",
        approveOrder = "/api/v1/Ordering/commands/ApproveOrder",
#if DEBUG || MONICA_HTTP_ENDPOINTS
        swagger = "/swagger",
#endif
        metrics = "/metrics",
        projectUnits = "/framework/units",
        readiness = "/health",
        liveness = "/alive"
    },
    runtime = new
    {
        configuration = "Ordering options use the local Monica store; ReferenceUI startup options remain fixed until restart.",
        consistency = "Requests use operation scopes; the process-local demo repository has no transactional rollback.",
        approvalEvent = "The demo publishes locally after updating its dictionary; durable applications use the transactional outbox.",
        scheduler = "WorkerOrderBacklogReport runs every minute only on the ordering.reporting owner, with in-memory scheduler state."
    }
}));

app.MapMonica();
app.Run();
