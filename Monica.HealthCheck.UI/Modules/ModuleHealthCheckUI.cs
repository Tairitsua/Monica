using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.HealthCheck.UI.Localization;
using Monica.HealthCheck.UI.Pages;
using Monica.HealthCheck.UI.State;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides registration helpers for Monica's unified Health Check UI module.
/// </summary>
public static class ModuleHealthCheckUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the current-host Health Check dashboard, its localized navigation entry, and its access boundary.
        /// </summary>
        /// <param name="action">Optional dashboard access configuration.</param>
        /// <returns>The host-bound Health Check UI module registration.</returns>
        public ModuleRegistration<ModuleHealthCheckUI, ModuleHealthCheckUIOption> AddHealthCheckUI(
            Action<ModuleHealthCheckUIOption>? action = null)
        {
            return builder.AddModule<ModuleHealthCheckUI, ModuleHealthCheckUIOption>(action);
        }
    }
}

/// <summary>
/// Registers the presentation services for the current-host Health Check dashboard.
/// </summary>
public sealed class ModuleHealthCheckUI : MonicaModule<ModuleHealthCheckUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHealthCheck, ModuleHealthCheckOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<HealthCheckResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<HealthCheckResource>(
                    "Monica.HealthCheck", "Navigation:Category", 10,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.HeartPulse);

                registry.RegisterLocalizedPage<UIHealthCheckPage, HealthCheckResource>(
                    UIHealthCheckPage.PAGE_URL,
                    "Navigation:Title",
                    MoIcons.HeartPulse,
                    category,
                    addToNav: true,
                    navOrder: 10,
                    accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleHealthCheckUIOption>));
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleHealthCheckUIOption> context)
    {
        context.Services.TryAddScoped<OperationalPageAccessPolicy<ModuleHealthCheckUIOption>>();
        context.Services.TryAddSingleton<HealthCheckPageSessionFactory>();
    }
}

/// <summary>
/// Configures access to the Health Check dashboard.
/// </summary>
public sealed class ModuleHealthCheckUIOption : ModuleOptions<ModuleHealthCheckUI>, IOperationalPageAccessOptions
{
    /// <summary>
    /// Gets or sets the host authorization policy that overrides the shell-wide operational policy for this page.
    /// The default is <see langword="null"/>. A missing or blank value inherits
    /// <see cref="OperationalPageAccessOption.AuthorizationPolicy"/>. Configure an override only when this page needs
    /// a different policy; Development access and operational Debug mode ignore it.
    /// </summary>
    public string? AuthorizationPolicyOverride { get; set; }
}
