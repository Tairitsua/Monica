using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Pages;
using Monica.ServiceDiscovery.UIServiceDiscovery.State;
using Monica.ServiceDiscovery.UIServiceDiscovery.Support;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleServiceDiscoveryUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Service Discovery workspaces and their circuit-scoped UI state.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscoveryUI, ModuleServiceDiscoveryUIOption> AddServiceDiscoveryUI(
            Action<ModuleServiceDiscoveryUIOption>? configure = null)
        {
            return builder.AddModule<ModuleServiceDiscoveryUI, ModuleServiceDiscoveryUIOption>(configure);
        }
    }
}

public class ModuleServiceDiscoveryUI : MonicaModule<ModuleServiceDiscoveryUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleServiceDiscovery, ModuleServiceDiscoveryOption>();
        module.Require<ModuleServiceEndpointDiscoveryUI, ModuleServiceEndpointDiscoveryUIOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<ServiceDiscoveryResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static navigation =>
            {
                var category = navigation.RegisterLocalizedCategory<ServiceDiscoveryResource>(
                    "Monica.ServiceDiscovery", "Navigation:Category", 100,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Cloud);

                navigation.RegisterLocalizedPage<UIServiceDiscoveryPage, ServiceDiscoveryResource>(
                    UIServiceDiscoveryPage.SERVICE_DISCOVERY_DEBUG_URL,
                    "Pages:ServiceDiscovery:Title",
                    MoIcons.Cloud,
                    category,
                    addToNav: true,
                    navOrder: 40,
                    descriptionKey: "Workbench:Views:Registry:Description");
                navigation.RegisterLocalizedPage<UIServiceDiscoveryAtlasPage, ServiceDiscoveryResource>(
                    UIServiceDiscoveryAtlasPage.PAGE_URL, "Workbench:Views:Atlas:Title", MoIcons.ChartScatter,
                    category, addToNav: true, navOrder: 41, descriptionKey: "Workbench:Views:Atlas:Description");
                navigation.RegisterLocalizedPage<UIServiceDiscoveryDependenciesPage, ServiceDiscoveryResource>(
                    UIServiceDiscoveryDependenciesPage.PAGE_URL, "Workbench:Views:Dependencies:Title", MoIcons.Workflow,
                    category, addToNav: true, navOrder: 42, descriptionKey: "Workbench:Views:Dependencies:Description");
                navigation.RegisterLocalizedPage<UIServiceDiscoveryDomainsPage, ServiceDiscoveryResource>(
                    UIServiceDiscoveryDomainsPage.PAGE_URL, "Workbench:Views:Domains:Title", MoIcons.Building,
                    category, addToNav: true, navOrder: 43, descriptionKey: "Workbench:Views:Domains:Description");
                navigation.RegisterLocalizedPage<UIServiceDiscoveryHostPage, ServiceDiscoveryResource>(
                    UIServiceDiscoveryHostPage.PAGE_URL, "Workbench:Views:Host:Title", MoIcons.Monitor,
                    category, addToNav: true, navOrder: 44, descriptionKey: "Workbench:Views:Host:Description");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleServiceDiscoveryUIOption> context)
    {
        context.Services.AddScoped<ServiceDiscoveryDomainColorResolver>();
        context.Services.AddScoped<ServiceInstanceEvictionTracker>();
        context.Services.AddScoped<ServiceDiscoveryPageState>();
        context.Services.AddScoped<CurrentInstanceInfoState>();
    }
}

/// <summary>
/// Configures Service Discovery monitoring presentation.
/// </summary>
public class ModuleServiceDiscoveryUIOption : ModuleOptions<ModuleServiceDiscoveryUI>
{
    /// <summary>
    /// Gets metadata keys displayed directly in the service list.
    /// </summary>
    public List<string> DisplayMetadataKeys { get; set; } = [];

    /// <summary>
    /// Gets or sets whether listening addresses are hidden from the service list.
    /// </summary>
    public bool DisableListeningAddressDisplay { get; set; }

    /// <summary>
    /// Gets or sets the maximum evicted instances retained per service.
    /// </summary>
    public int MaxEvictedServiceRetentionCount { get; set; } = 10;
}
