using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.DevOps.FileOps.Pages;
using Monica.DevOps.FileOps.State;
using Monica.DevOps.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Composes Files with a same-visit host policy dialog and a hidden compatibility route.</summary>
/// <remarks>Requires FileOps infrastructure, localization and the shell. The scoped return context contains
/// passive identities only; each routed workspace owns and retires its controller and native resources.</remarks>
public class ModuleFileOpsUI : MonicaModule<ModuleFileOpsUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleFileOpsUIOption> context)
    {
        context.Services.AddScoped<FileOpsNavigationState>();
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleFileOps, ModuleFileOpsOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<FileOpsResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<FileOpsResource>(
                    "Monica.DevOps.FileOps", "Navigation:Category", 50,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Folder);

                registry.RegisterLocalizedPage<UIFileOpsPage, FileOpsResource>(
                    UIFileOpsPage.PAGE_URL,
                    "Pages:FileOps:Title",
                    MoIcons.Folder,
                    category,
                    addToNav: true,
                    navOrder: 36);
                registry.RegisterLocalizedPage<UIFileOpsPolicyPage, FileOpsResource>(
                    UIFileOpsPolicyPage.PAGE_URL,
                    "Pages:FileOpsPolicy:Title",
                    MoIcons.Sliders,
                    category,
                    addToNav: false,
                    navOrder: 37);
            }));
    }
}

/// <summary>Registers the localized FileOps route family.</summary>
public static class ModuleFileOpsUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Adds Files and runtime-policy UI using the host's existing access configuration.</summary>
        /// <param name="action">Optional UI module configuration; omitted configuration uses module defaults.</param>
        /// <returns>The UI module registration.</returns>
        public ModuleRegistration<ModuleFileOpsUI, ModuleFileOpsUIOption> AddFileOpsUI(Action<ModuleFileOpsUIOption>? action = null)
        {
            return builder.AddModule<ModuleFileOpsUI, ModuleFileOpsUIOption>(action);
        }
    }
}



/// <summary>FileOps UI module options; this route family adds no independent authorization policy.</summary>
public class ModuleFileOpsUIOption : ModuleOptions<ModuleFileOpsUI>;
