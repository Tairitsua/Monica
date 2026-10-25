using Monica.UI.Icons;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Localization;
using Monica.UI.Pages;
using Monica.UI.Shell.Models;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDiffHighlightUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the DiffHighlightUI module
        /// </summary>
        public ModuleRegistration<ModuleDiffHighlightUI, ModuleDiffHighlightUIOption> AddDiffHighlightUI(
            Action<ModuleDiffHighlightUIOption>? action = null)
        {
            return builder.AddModule<ModuleDiffHighlightUI, ModuleDiffHighlightUIOption>(action);
        }
    }
}

/// <summary>
/// Text difference contrast highlighting UI module
/// </summary>
public class ModuleDiffHighlightUI : MonicaModule<ModuleDiffHighlightUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDiffHighlight, ModuleDiffHighlightOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<SharedResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<SharedResource>(
                    "Monica.UI.TextDiff", "Navigation:Category", 50,
                    parentId: BuiltInNavigationCategoryIds.Debug,
                    icon: MoIcons.Columns);

                registry.RegisterLocalizedPage<DiffHighlightPage, SharedResource>(
                    DiffHighlightPage.DIFF_HIGHLIGHT_URL,
                    "Pages:DiffHighlight:Title",
                    MoIcons.Columns,
                    category,
                    addToNav: true,
                    navOrder: 60);
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleDiffHighlightUIOption> context)
    {
        // The mixed module already registers the diff facade and infrastructure services.
    }
}

/// <summary>
/// DiffHighlightUI module options
/// </summary>
public class ModuleDiffHighlightUIOption : ModuleOptions<ModuleDiffHighlightUI>
{
}
