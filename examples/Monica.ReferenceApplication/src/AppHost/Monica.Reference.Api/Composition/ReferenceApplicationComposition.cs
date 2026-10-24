using Microsoft.Extensions.Options;
using Monica.Configuration.Bootstrap;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Reference.Api.Configurations;
using Monica.Reference.UI.Layout;
using Monica.Reference.UI.Localization;

namespace Monica.Reference.Api.Composition;

/// <summary>Owns the reference executable's native module and provider choices.</summary>
public static class ReferenceApplicationComposition
{
    /// <summary>Composes the real application inside the host's AddMonica callback.</summary>
    /// <param name="monica">The current host's native module builder.</param>
    /// <param name="configurationInputs">
    /// Optional native store declaration. The default uses the local file store; host tests select an isolated
    /// directory through the same provider without replacing application services or the generated manifest.
    /// </param>
    public static void ConfigureReferenceApplication(this IMonicaBuilder monica,
        MonicaConfigurationInputPlan? configurationInputs = null)
    {
        var diagnostics = monica.GetStartupOptions<OptionsReferenceUI>();
        monica.ConfigureApplication(options =>
        {
            options.ProjectName = "Monica.Reference.Api";
            options.DomainName = "Ordering";
        });
        monica.ConfigureModuleSystem(options =>
        {
            options.DefaultApiGroupName = "Ordering";
            options.EnableMinimalApiByDefault = true;
        });
        monica.ConfigureTypeDiscovery(options =>
        {
            options.Add(typeof(ReferenceApplicationComposition).Assembly);
            options.Add("Domains.", "Roles.", "Platform.Protocol");
        });
        monica.AddApplicationModel();
        monica.AddApplicationInvocation();
        monica.AddHealthCheck();
        monica.AddConfiguration(configurationInputs ?? MonicaConfigurationInputPlan.Create(inputs => inputs.UseFileConfigurationStore()));
        monica.AddEventBus().UseNoOpDistributedEventBus();
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
            monica.AddSwagger(options => options.ApiVersion = "v1");
        }
        monica.AddUnitOfWork();
        monica.AddProjectUnits(options =>
        {
            options.ConventionOptions.EnableNameConvention = true;
            options.ConventionOptions.NameConventionMode = ENameConventionMode.Strict;
        });
        monica.AddOpenTelemetry().UsePrometheusEndpoint();
    }

    /// <summary>Maps the executable's informational endpoint after native middleware configuration.</summary>
    public static void MapReferenceOverview(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/overview", (MonicaApplication application, IOptions<OptionsReferenceUI> diagnostics) => Results.Ok(new
        {
            application = application.Application.ResolveAppName(),
            architecture = "One complete application with domain-owned roles",
            endpoints = new
            {
                product = "/orders",
                orders = "/api/v1/Ordering/queries/GetOrders",
                approveOrder = "/api/v1/Ordering/commands/ApproveOrder",
                swagger = diagnostics.Value.EnableDiagnostics ? "/swagger" : null,
                metrics = "/metrics",
                projectUnits = "/framework/units",
                readiness = "/health",
                liveness = "/alive"
            },
            runtime = new
            {
                autonomousWork = application.Application.AutonomousWork,
                configuration = "Ordering values are Managed; ReferenceUI is Startup and remains fixed until restart.",
                consistency = "Requests use operation scopes; the process-local demo repository has no transactional rollback.",
                approvalEvent = "The demo publishes locally after updating its dictionary; durable applications use the transactional outbox.",
                scheduler = "The reporting owner keeps the native backlog job available. Manual mode requires an explicit run; Automatic mode schedules it every minute."
            }
        }));
    }
}
