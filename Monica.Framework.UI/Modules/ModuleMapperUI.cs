using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleMapperUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the MapperUI module
        /// </summary>
        public ModuleRegistration<ModuleMapperUI, ModuleMapperUIOption> AddMapperUI(
            Action<ModuleMapperUIOption>? action = null)
        {
            return builder.AddModule<ModuleMapperUI, ModuleMapperUIOption>(action);
        }
    }
}

/// <summary>
/// Mapper UI module
/// </summary>
public class ModuleMapperUI : MonicaModule<ModuleMapperUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleObjectMapping, ModuleObjectMappingOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<MapperResource>());
        module.Require<ModuleUI, ModuleUIOption>(options => options.EnableMarkdown = true);
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
        {
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<MapperResource>(
                    "Monica.Framework.UI.Mapper", "Navigation:Category", 40,
                    parentId: BuiltInNavigationCategoryIds.Debug,
                    icon: MoIcons.Workflow);

                registry.RegisterLocalizedPage<UIMapperDebugPage, MapperResource>(
                    UIMapperDebugPage.PAGE_URL,
                    "Pages:MapperDebug:Title",
                    MoIcons.Code,
                    category,
                    addToNav: true,
                    navOrder: 10);
            });
        });
    }
}

/// <summary>
/// MapperUI module options
/// </summary>
public class ModuleMapperUIOption : ModuleOptions<ModuleMapperUI>
{
}
