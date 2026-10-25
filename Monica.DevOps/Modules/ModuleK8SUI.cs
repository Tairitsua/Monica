using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.DevOps.K8S.Pages;
using Monica.DevOps.K8S.State;
using Monica.DevOps.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Composes localized Inventory and process runtime-configuration workbenches.</summary>
/// <remarks>Requires K8S infrastructure, localization and the shell. Uses the host's existing access
/// configuration. Each routed visit owns and drains its actual provider and dialog work; circuit state
/// retains only nonsecret namespace, kind and search preferences.</remarks>
public class ModuleK8SUI : MonicaModule<ModuleK8SUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleK8SUIOption> context) =>
        context.Services.AddScoped<K8SNavigationState>();

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleK8S, ModuleK8SOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<K8SResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<K8SResource>(
                    "Monica.DevOps.Kubernetes", "Navigation:Category", 30,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Server);

                registry.RegisterLocalizedPage<UIK8SPage, K8SResource>(
                    UIK8SPage.PAGE_URL,
                    "Pages:K8S:Title",
                    MoIcons.Server,
                    category,
                    addToNav: true,
                    navOrder: 35,
                    descriptionKey: "Workbench:Inventory:Subtitle");
                registry.RegisterLocalizedPage<UIK8SPage, K8SResource>(
                    UIK8SRuntimeConfigPage.PAGE_URL,
                    "Workbench:Config:Title",
                    MoIcons.Sliders,
                    category,
                    addToNav: true,
                    navOrder: 35,
                    descriptionKey: "Workbench:Config:Subtitle");
            }));
    }
}

/// <summary>Registers the localized K8S workbench route family.</summary>
public static class ModuleK8SUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Adds Inventory and runtime configuration UI with the host's existing access semantics.</summary>
        /// <param name="action">Optional UI module configuration; omission uses module defaults.</param>
        /// <returns>The host-bound UI module registration.</returns>
        public ModuleRegistration<ModuleK8SUI, ModuleK8SUIOption> AddK8SUI(Action<ModuleK8SUIOption>? action = null)
        {
            return builder.AddModule<ModuleK8SUI, ModuleK8SUIOption>(action);
        }
    }
}



/// <summary>Options for the K8S route family; adds no independent authorization policy.</summary>
public class ModuleK8SUIOption : ModuleOptions<ModuleK8SUI>;
