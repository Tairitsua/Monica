using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UIBuildingBlocks.Services;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleBuildingBlocksUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the building-blocks UI module.
        /// </summary>
        public ModuleRegistration<ModuleBuildingBlocksUI, ModuleBuildingBlocksUIOption> AddBuildingBlocksUI(
            Action<ModuleBuildingBlocksUIOption>? action = null)
        {
            return builder.AddModule<ModuleBuildingBlocksUI, ModuleBuildingBlocksUIOption>(action);
        }
    }
}

/// <summary>
/// Building-blocks UI module.
/// </summary>
public class ModuleBuildingBlocksUI : MonicaModule<ModuleBuildingBlocksUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleBuildingBlocks, ModuleBuildingBlocksOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<BuildingBlocksResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<BuildingBlocksResource>(
                    "Monica.Framework.UI.BuildingBlocks",
                    "Pages:BuildingBlocks:Title",
                    order: 20,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Workflow,
                    descriptionKey: "Pages:BuildingBlocks:Description");

                registry.RegisterLocalizedPage<UIBuildingBlocksPage, BuildingBlocksResource>(
                    UIBuildingBlocksPage.PAGE_URL,
                    "Pages:BuildingBlocks:Overview:Title",
                    MoIcons.Dashboard,
                    category,
                    addToNav: true,
                    navOrder: 0,
                    descriptionKey: "Pages:BuildingBlocks:Overview:Description");
                registry.RegisterLocalizedPage<UIBuildingBlocksCatalogPage, BuildingBlocksResource>(
                    UIBuildingBlocksCatalogPage.PAGE_URL,
                    "Pages:BuildingBlocks:Catalog:Title",
                    MoIcons.Workflow,
                    category,
                    addToNav: true,
                    navOrder: 10,
                    descriptionKey: "Pages:BuildingBlocks:Catalog:Description");
                registry.RegisterLocalizedPage<UIBuildingBlocksGraphPage, BuildingBlocksResource>(
                    UIBuildingBlocksGraphPage.PAGE_URL,
                    "Pages:BuildingBlocks:Graph:Title",
                    MoIcons.Network,
                    category,
                    addToNav: true,
                    navOrder: 20,
                    descriptionKey: "Pages:BuildingBlocks:Graph:Description");
                registry.RegisterLocalizedPage<UIBuildingBlocksEventsPage, BuildingBlocksResource>(
                    UIBuildingBlocksEventsPage.PAGE_URL,
                    "Pages:BuildingBlocks:Events:Title",
                    MoIcons.Calendar,
                    category,
                    addToNav: true,
                    navOrder: 30,
                    descriptionKey: "Pages:BuildingBlocks:Events:Description");
                registry.RegisterLocalizedPage<UIBuildingBlocksRequestFiltersPage, BuildingBlocksResource>(
                    UIBuildingBlocksRequestFiltersPage.PAGE_URL,
                    "Pages:BuildingBlocks:RequestFilters:Title",
                    MoIcons.Filter,
                    category,
                    addToNav: true,
                    navOrder: 40,
                    descriptionKey: "Pages:BuildingBlocks:RequestFilters:Description");
                registry.RegisterLocalizedPage<UIBuildingBlocksEnumsPage, BuildingBlocksResource>(
                    UIBuildingBlocksEnumsPage.PAGE_URL,
                    "Pages:BuildingBlocks:Enums:Title",
                    MoIcons.Braces,
                    category,
                    addToNav: true,
                    navOrder: 50,
                    descriptionKey: "Pages:BuildingBlocks:Enums:Description");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleBuildingBlocksUIOption> context)
    {
        context.Services.AddScoped<IBuildingBlocksUiDataSource, BuildingBlocksUiDataSource>();
    }
}

/// <summary>
/// Building-blocks UI module options.
/// </summary>
public class ModuleBuildingBlocksUIOption : ModuleOptions<ModuleBuildingBlocksUI>
{
}
