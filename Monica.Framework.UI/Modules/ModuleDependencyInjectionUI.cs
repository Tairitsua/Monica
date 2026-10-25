using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDependencyInjectionUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the dependency-injection diagnostics UI module.
        /// </summary>
        public ModuleRegistration<ModuleDependencyInjectionUI, ModuleDependencyInjectionUIOption> AddDependencyInjectionUI(
            Action<ModuleDependencyInjectionUIOption>? action = null)
        {
            return builder.AddModule<ModuleDependencyInjectionUI, ModuleDependencyInjectionUIOption>(action);
        }
    }
}

/// <summary>
/// Dependency-injection diagnostics UI module.
/// </summary>
public class ModuleDependencyInjectionUI : MonicaModule<ModuleDependencyInjectionUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDependencyInjection, ModuleDependencyInjectionOption>(
            static option => option.EnableAutoRegistrationDiagnostics = true);
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<DependencyInjectionResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<DependencyInjectionResource>(
                    "Monica.Framework.UI.DependencyInjection", "Navigation:Category", 10,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Workflow);

                registry.RegisterLocalizedPage<UIDependencyInjectionPage, DependencyInjectionResource>(
                    UIDependencyInjectionPage.PAGE_URL,
                    "Pages:DependencyInjection:Title",
                    MoIcons.Workflow,
                    category,
                    addToNav: true,
                    navOrder: 10,
                    descriptionKey: "Pages:DependencyInjection:Description");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleDependencyInjectionUIOption> context)
    {
        // UI components inject the infrastructure facade directly.
    }
}

/// <summary>
/// Options for the dependency-injection diagnostics UI module.
/// </summary>
public class ModuleDependencyInjectionUIOption : ModuleOptions<ModuleDependencyInjectionUI>
{
}
