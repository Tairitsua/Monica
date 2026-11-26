using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Pages;
using Monica.UI.Icons;
using Monica.UI.Shell.Models;

namespace Monica.Modules;

/// <summary>Endpoint diagnostics page, usable without registry membership or a state store.</summary>
public sealed class ModuleServiceEndpointDiscoveryUI : MonicaModule<ModuleServiceEndpointDiscoveryUIOption>, IUIModule
{
    internal static NavigationCategoryId NavigationCategory { get; } = NavigationCategoryId.Create("Monica.ServiceDiscovery");

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleServiceEndpointDiscovery>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(o => o.AddResource<ServiceDiscoveryResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(o => o.ConfigureNavigation(n =>
        {
            var category = n.RegisterLocalizedCategory<ServiceDiscoveryResource>(
                NavigationCategory.Value, "Navigation:Category", 100,
                parentId: BuiltInNavigationCategoryIds.Monitor, icon: MoIcons.Cloud);
            n.RegisterLocalizedPage<UIServiceDiscoveryEndpointsPage, ServiceDiscoveryResource>(
                UIServiceDiscoveryEndpointsPage.PAGE_URL, "Workbench:Views:Endpoints:Title", MoIcons.Network,
                category, addToNav: true, navOrder: 45,
                descriptionKey: "Workbench:Views:Endpoints:Description");
        }));
    }
}

/// <summary>Endpoint page registration; runtime limits belong to ModuleServiceEndpointDiscoveryOption.</summary>
public sealed class ModuleServiceEndpointDiscoveryUIOption : ModuleOptions<ModuleServiceEndpointDiscoveryUI>;

/// <summary>Registration of the optional endpoint diagnostics page.</summary>
public static class ModuleServiceEndpointDiscoveryUIBuilderExtensions
{
    /// <summary>Adds the endpoint diagnostics page without enabling instance registration.</summary>
    public static ModuleRegistration<ModuleServiceEndpointDiscoveryUI, ModuleServiceEndpointDiscoveryUIOption>
        AddServiceEndpointDiscoveryUI(this IMonicaBuilder builder) =>
        builder.AddModule<ModuleServiceEndpointDiscoveryUI, ModuleServiceEndpointDiscoveryUIOption>();
}
