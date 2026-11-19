using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.EventBus.Kafka.Localization;
using Monica.EventBus.Kafka.Pages;
using Monica.EventBus.Kafka.UIEventBusKafka.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Kafka EventBus console UI module.
/// </summary>
public static class ModuleEventBusKafkaUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Kafka EventBus console UI.
        /// </summary>
        /// <param name="action">Optional UI module option configuration.</param>
        /// <returns>The host-bound Kafka EventBus UI registration.</returns>
        public ModuleRegistration<ModuleEventBusKafkaUI, ModuleEventBusKafkaUIOption> AddEventBusKafkaUI(
            Action<ModuleEventBusKafkaUIOption>? action = null)
        {
            return builder.AddModule<ModuleEventBusKafkaUI, ModuleEventBusKafkaUIOption>(action);
        }
    }
}

/// <summary>
/// Kafka EventBus management console UI module.
/// </summary>
public sealed class ModuleEventBusKafkaUI : MonicaModule<ModuleEventBusKafkaUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleEventBusKafka>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<EventBusKafkaResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<EventBusKafkaResource>(
                    "monica.kafka", "Pages:EventBusKafka:Title", 38,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Database,
                    descriptionKey: "Workbench:CategoryDescription");
                registry.RegisterLocalizedPage<UIEventBusKafkaPage, EventBusKafkaResource>(
                    UIEventBusKafkaPage.PAGE_URL,
                    "Workbench:Titles:Overview",
                    MoIcons.Dashboard,
                    category,
                    addToNav: true,
                    navOrder: 0,
                    descriptionKey: "Workbench:Descriptions:Overview");
                registry.RegisterLocalizedPage<UIEventBusKafkaClustersPage, EventBusKafkaResource>(
                    UIEventBusKafkaClustersPage.PAGE_URL, "Workbench:Titles:Clusters", MoIcons.Server,
                    category, addToNav: true, navOrder: 1, descriptionKey: "Workbench:Descriptions:Clusters");
                registry.RegisterLocalizedPage<UIEventBusKafkaTopicsPage, EventBusKafkaResource>(
                    UIEventBusKafkaTopicsPage.PAGE_URL, "Workbench:Titles:Topics", MoIcons.Folder,
                    category, addToNav: true, navOrder: 2, descriptionKey: "Workbench:Descriptions:Topics");
                registry.RegisterLocalizedPage<UIEventBusKafkaConsumerGroupsPage, EventBusKafkaResource>(
                    UIEventBusKafkaConsumerGroupsPage.PAGE_URL, "Workbench:Titles:ConsumerGroups", MoIcons.Users,
                    category, addToNav: true, navOrder: 3, descriptionKey: "Workbench:Descriptions:ConsumerGroups");
                registry.RegisterLocalizedPage<UIEventBusKafkaPerformancePage, EventBusKafkaResource>(
                    UIEventBusKafkaPerformancePage.PAGE_URL, "Workbench:Titles:Performance", MoIcons.HeartPulse,
                    category, addToNav: true, navOrder: 4, descriptionKey: "Workbench:Descriptions:Performance");
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleEventBusKafkaUIOption> context)
    {
        var services = context.Services;
        services.AddScoped<EventBusKafkaPageState>();
        services.AddScoped<KafkaPerformancePollingState>();
        services.AddScoped<KafkaConsumerMetricsPollingStateFactory>();
        services.AddScoped<KafkaWorkbenchSessionFactory>();
    }
}

/// <summary>
/// Configuration options for the Kafka EventBus console UI module.
/// </summary>
public sealed class ModuleEventBusKafkaUIOption : ModuleOptions<ModuleEventBusKafkaUI>
{
    /// <summary>
    /// Gets or sets whether a routed Kafka workspace starts live sampling after its selected broker is verified.
    /// </summary>
    /// <remarks>
    /// The page still provides a manual capture button when this is disabled. The default is
    /// enabled so a newly opened workspace with a verified broker establishes a rate baseline.
    /// Each composing workbench owns and retires its sampler; navigation does not borrow another route's loop.
    /// </remarks>
    public bool EnablePerformanceAutoRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets the default interval used by the live performance sampler.
    /// </summary>
    /// <remarks>
    /// The default is five seconds. Users can change the interval in the performance workspace for the
    /// current routed session; this option controls the initial value for new sessions.
    /// </remarks>
    public TimeSpan PerformanceRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
}
