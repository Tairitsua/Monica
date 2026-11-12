using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.UI.Shell.Models;
using Monica.UI.Localization;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Framework.UI.UIEventBus.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleEventBusUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the EventBusUI module
        /// </summary>
        public ModuleRegistration<ModuleEventBusUI, ModuleEventBusUIOption> AddEventBusUI(
            Action<ModuleEventBusUIOption>? action = null)
        {
            return builder.AddModule<ModuleEventBusUI, ModuleEventBusUIOption>(action);
        }
    }
}

/// <summary>
/// Event bus UI module
/// </summary>
public class ModuleEventBusUI : MonicaModule<ModuleEventBusUIOption>, IWebHostRequiredModule, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(static option =>
        {
            option.AddResource<EventBusResource>();
            option.AddResource<ModuleSystemResource>();
        });
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<EventBusResource>(
                    "Monica.Framework.UI.EventBus", "Navigation:Category", 90,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Network);

                registry.RegisterLocalizedPage<UIEventBusMonitorPage, EventBusResource>(
                    UIEventBusMonitorPage.PAGE_URL, "Pages:EventBusMonitor:Title", MoIcons.Network,
                    category, addToNav: true, navOrder: 40, descriptionKey: "Workbench:OverviewDescription");
                registry.RegisterLocalizedPage<UIEventBusProvidersPage, EventBusResource>(
                    UIEventBusProvidersPage.PAGE_URL, "Pages:EventBusProviders:Title", MoIcons.Cloud,
                    category, addToNav: true, navOrder: 41, descriptionKey: "Workbench:ProvidersDescription");
                registry.RegisterLocalizedPage<UIEventBusSubscriptionsPage, EventBusResource>(
                    UIEventBusSubscriptionsPage.PAGE_URL, "Pages:EventBusSubscriptions:Title", MoIcons.Route,
                    category, addToNav: true, navOrder: 42, descriptionKey: "Workbench:SubscriptionsDescription");
                registry.RegisterLocalizedPage<UIEventBusChangesPage, EventBusResource>(
                    UIEventBusChangesPage.PAGE_URL, "Pages:EventBusChanges:Title", MoIcons.History,
                    category, addToNav: true, navOrder: 43, descriptionKey: "Workbench:ChangesDescription");
                registry.RegisterLocalizedPage<UIEventBusTestToolsPage, EventBusResource>(
                    UIEventBusTestToolsPage.PAGE_URL, "Pages:EventBusTestTools:Title", MoIcons.Flask,
                    category, addToNav: true, navOrder: 44, descriptionKey: "Workbench:TestToolsDescription");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleEventBusUIOption> context)
    {
        var services = context.Services;
        // Register as Singleton to maintain real-time subscription status
        services.AddSingleton<EventBusMonitorService>();

        // Register a test service for distributed event bus testing
        services.AddScoped<EventBusTestService>();

        // Register the Provider Discovery Service
        services.AddScoped<EventBusProviderDiscoveryService>();
        services.AddScoped<EventBusWorkbenchState>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleEventBusUIOption> context)
    {
        var app = context.ApplicationBuilder;
        var localizer = app.ApplicationServices.GetRequiredService<IStringLocalizer<EventBusResource>>();

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            // Get all subscriptions
            endpoints.MapGet("/eventbus-ui/subscriptions",
                async ([FromServices] EventBusMonitorService service) =>
                {
                    var result = await service.GetAllSubscriptionsAsync();
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:List:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:List:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:List:Description"].Value);

            // Get subscription details
            endpoints.MapGet("/eventbus-ui/subscriptions/{id:guid}",
                async ([FromRoute] Guid id,
                       [FromServices] EventBusMonitorService service) =>
                {
                    var subscriptionId = new EventBus.Models.EventSubscriptionId(id);
                    var result = await service.GetSubscriptionByIdAsync(subscriptionId);
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:Detail:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:Detail:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:Detail:Description"].Value);

            // Get statistics
            endpoints.MapGet("/eventbus-ui/statistics",
                async ([FromServices] EventBusMonitorService service) =>
                {
                    var result = await service.GetStatisticsAsync();
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:Statistics:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:Statistics:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:Statistics:Description"].Value);

            // Get runtime status of all topic subscriptions
            endpoints.MapGet("/eventbus-ui/topic-status",
                ([FromServices] EventBusMonitorService service) =>
                {
                    var result = service.GetAllTopicStatuses();
                    return result.GetResponse();
                })
                .WithName(localizer["Api:TopicStatus:List:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:TopicStatus:List:Summary"].Value)
                .WithDescription(localizer["Api:TopicStatus:List:Description"].Value);

            // Activate subscription
            endpoints.MapPost("/eventbus-ui/subscriptions/{id:guid}/activate",
                async ([FromRoute] Guid id,
                       [FromServices] EventBusMonitorService service) =>
                {
                    var subscriptionId = new EventBus.Models.EventSubscriptionId(id);
                    var result = await service.ActivateSubscriptionAsync(subscriptionId);
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:Activate:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:Activate:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:Activate:Description"].Value);

            // Deactivate subscription
            endpoints.MapPost("/eventbus-ui/subscriptions/{id:guid}/deactivate",
                async ([FromRoute] Guid id,
                       [FromServices] EventBusMonitorService service) =>
                {
                    var subscriptionId = new EventBus.Models.EventSubscriptionId(id);
                    var result = await service.DeactivateSubscriptionAsync(subscriptionId);
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:Deactivate:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:Deactivate:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:Deactivate:Description"].Value);

            // Remove subscription
            endpoints.MapDelete("/eventbus-ui/subscriptions/{id:guid}",
                async ([FromRoute] Guid id,
                       [FromServices] EventBusMonitorService service) =>
                {
                    var subscriptionId = new EventBus.Models.EventSubscriptionId(id);
                    var result = await service.UnsubscribeAsync(subscriptionId);
                    return result.GetResponse();
                })
                .WithName(localizer["Api:Subscriptions:Remove:Name"].Value)
                .WithTags(tagName)
                .WithSummary(localizer["Api:Subscriptions:Remove:Summary"].Value)
                .WithDescription(localizer["Api:Subscriptions:Remove:Description"].Value);
        });
    }
}

/// <summary>
/// EventBusUI module options
/// </summary>
public class ModuleEventBusUIOption : MinimalApiModuleOptions<ModuleEventBusUI>
{
    /// <summary>
    /// Maximum number of recent messages retained by each active row-level test listener.
    /// The default is 20. Increase this when developers need a longer listener history during
    /// manual diagnostics; the limit is applied per active listener and older messages are
    /// discarded as new messages arrive.
    /// </summary>
    public int TestListenerMessageLimit { get; set; } = 20;
}
