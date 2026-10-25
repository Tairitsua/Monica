using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UISeeder.State;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides host-bound registration for current-host Seeder diagnostics and explicit execution.
/// </summary>
public static class ModuleSeederUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the current-host Seeder dashboard, its localized navigation entry, and its access boundary.
        /// </summary>
        /// <param name="configure">Optional dashboard access configuration.</param>
        /// <returns>The host-bound Seeder UI module registration.</returns>
        public ModuleRegistration<ModuleSeederUI, ModuleSeederUIOption> AddSeederUI(
            Action<ModuleSeederUIOption>? configure = null)
        {
            return builder.AddModule<ModuleSeederUI, ModuleSeederUIOption>(configure);
        }
    }
}

/// <summary>
/// Registers presentation services for the current-host Seeder operations dashboard.
/// </summary>
public sealed class ModuleSeederUI : MonicaModule<ModuleSeederUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleSeeder, ModuleSeederOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<SeederResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<SeederResource>(
                    "Monica.Framework.UI.Seeder", "Navigation:Category", 25,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Sprout);

                registry.RegisterLocalizedPage<UISeederPage, SeederResource>(
                    UISeederPage.PAGE_URL,
                    "Navigation:Title",
                    MoIcons.ListChecks,
                    category,
                    addToNav: true,
                    navOrder: 15,
                    accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleSeederUIOption>));
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleSeederUIOption> context)
    {
        context.Services.TryAddScoped<OperationalPageAccessPolicy<ModuleSeederUIOption>>();
        context.Services.TryAddSingleton<SeederPageSessionFactory>();
    }
}

/// <summary>
/// Configures access to the Seeder operations dashboard.
/// </summary>
public sealed class ModuleSeederUIOption : ModuleOptions<ModuleSeederUI>, IOperationalPageAccessOptions
{
    /// <summary>
    /// Gets or sets the host authorization policy that overrides the shell-wide operational policy for this page.
    /// The default is <see langword="null"/>. A missing or blank value inherits
    /// <see cref="OperationalPageAccessOption.AuthorizationPolicy"/>. Configure an override only when this page needs
    /// a different policy; Development access and operational Debug mode ignore it.
    /// </summary>
    public string? AuthorizationPolicyOverride { get; set; }
}
