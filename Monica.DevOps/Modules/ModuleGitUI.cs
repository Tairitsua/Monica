using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.DevOps.Git.Pages;
using Monica.DevOps.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Git dashboard UI module.
/// </summary>
public static class ModuleGitUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Git dashboard UI module.
        /// </summary>
        public ModuleRegistration<ModuleGitUI, ModuleGitUIOption> AddGitUI(Action<ModuleGitUIOption>? action = null)
        {
            return builder.AddModule<ModuleGitUI, ModuleGitUIOption>(action);
        }
    }
}

/// <summary>
/// Git dashboard UI module.
/// </summary>
public class ModuleGitUI : MonicaModule<ModuleGitUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleGit, ModuleGitOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<GitResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<GitResource>(
                    "Monica.DevOps.Git", "Navigation:Category", 40,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.FileCode);

                registry.RegisterLocalizedPage<UIGitRepositoriesPage, GitResource>(
                    UIGitRepositoriesPage.PAGE_URL,
                    "Pages:GitRepositories:Title",
                    MoIcons.FileCode,
                    category,
                    addToNav: true,
                    navOrder: 55);
                registry.RegisterLocalizedPage<UIGitCredentialsPage, GitResource>(
                    UIGitCredentialsPage.PAGE_URL,
                    "Pages:GitCredentials:Title",
                    MoIcons.Key,
                    category,
                    addToNav: true,
                    navOrder: 56);
            }));
    }
}

/// <summary>
/// Registration extensions for the Git dashboard UI module.
/// </summary>


/// <summary>
/// Options for the Git dashboard UI module.
/// </summary>
public class ModuleGitUIOption : ModuleOptions<ModuleGitUI>;
