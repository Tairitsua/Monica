using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Repository.UI.Localization;
using Monica.Repository.UI.Pages;
using Monica.Repository.UI.UIRepository.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Repository UI module.
/// </summary>
public static class ModuleRepositoryUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Repository diagnostics UI module.
        /// </summary>
        /// <param name="action">Optional module option configuration.</param>
        /// <returns>The host-bound Repository UI registration.</returns>
        public ModuleRegistration<ModuleRepositoryUI, ModuleRepositoryUIOption> AddRepositoryUI(
            Action<ModuleRepositoryUIOption>? action = null)
        {
            return builder.AddModule<ModuleRepositoryUI, ModuleRepositoryUIOption>(action);
        }
    }
}

/// <summary>
/// Repository diagnostics UI module.
/// </summary>
public sealed class ModuleRepositoryUI : MonicaModule<ModuleRepositoryUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleRepository, ModuleRepositoryOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<RepositoryUIResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<RepositoryUIResource>(
                    "Monica.Repository", "Navigation:Category", 110,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Database);

                registry.RegisterLocalizedPage<UIRepositoryDashboardPage, RepositoryUIResource>(
                    UIRepositoryDashboardPage.PAGE_URL,
                    "Workbench:Views:Overview:Label",
                    MoIcons.Database,
                    category,
                    addToNav: true,
                    navOrder: 35);
                registry.RegisterLocalizedPage<UIRepositoryActivityPage, RepositoryUIResource>(
                    UIRepositoryActivityPage.PAGE_URL,
                    "Workbench:Views:Activity:Label",
                    MoIcons.ChartTrend,
                    category,
                    addToNav: true,
                    navOrder: 36);
                registry.RegisterLocalizedPage<UIRepositoryMigrationsPage, RepositoryUIResource>(
                    UIRepositoryMigrationsPage.PAGE_URL,
                    "Workbench:Views:Migrations:Label",
                    MoIcons.Workflow,
                    category,
                    addToNav: true,
                    navOrder: 37);
                registry.RegisterLocalizedPage<UIRepositoryOptionsPage, RepositoryUIResource>(
                    UIRepositoryOptionsPage.PAGE_URL,
                    "Workbench:Views:Options:Label",
                    MoIcons.Sliders,
                    category,
                    addToNav: true,
                    navOrder: 38);
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleRepositoryUIOption> context)
    {
        context.Services.AddScoped<RepositoryDashboardState>();
    }
}

/// <summary>
/// Configuration options for the Repository diagnostics UI module.
/// </summary>
public sealed class ModuleRepositoryUIOption : ModuleOptions<ModuleRepositoryUI>
{
    /// <summary>
    /// Gets or sets a value indicating whether migration update actions are allowed while the host runs in Production.
    /// The default is <c>false</c>, so Production dashboards remain read-only unless the host explicitly opts in.
    /// </summary>
    public bool AllowMigrationUpdateInProduction { get; set; }

    /// <summary>
    /// Gets or sets the command timeout used while the dashboard applies EF Core migrations.
    /// The default is one hour, matching the legacy migration endpoint behavior.
    /// </summary>
    public TimeSpan MigrationCommandTimeout { get; set; } = TimeSpan.FromMinutes(60);
}
