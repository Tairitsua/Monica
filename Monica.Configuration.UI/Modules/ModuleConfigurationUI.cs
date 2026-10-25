using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.UI.Localization;
using Monica.Configuration.UI.Pages;
using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Monica.Configuration.UI module.
/// </summary>
public static class ModuleConfigurationUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the configuration operator console UI module.
        /// </summary>
        /// <param name="action">Optional module option configuration.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<ModuleConfigurationUI, ModuleConfigurationUIOption> AddConfigurationUI(
            Action<ModuleConfigurationUIOption>? action = null)
        {
            return builder.AddModule<ModuleConfigurationUI, ModuleConfigurationUIOption>(action);
        }
    }

}

/// <summary>
/// Operator console UI for Monica.Configuration.
/// </summary>
public sealed class ModuleConfigurationUI : MonicaModule<ModuleConfigurationUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleConfiguration, ModuleConfigurationOption>();
        module.Require<ModuleDiffHighlight, ModuleDiffHighlightOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<ConfigurationUIResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(
            static option => option.ConfigureNavigation(RegisterNavigation));
    }

    private static void RegisterNavigation(INavigationRegistryBuilder registry)
    {
        registry.RegisterLocalizedPage<ConfigurationStatePage, ConfigurationUIResource>(
            ConfigurationUiRoutes.STATE_ROUTE,
            "Pages:ConfigurationState:Title",
            MoIcons.Sliders,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 10);
        registry.RegisterLocalizedPage<ConfigurationHistoryPage, ConfigurationUIResource>(
            ConfigurationUiRoutes.HISTORY_ROUTE,
            "Pages:ConfigurationHistory:Title",
            MoIcons.History,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 20);
        registry.RegisterLocalizedPage<ConfigurationVersionsPage, ConfigurationUIResource>(
            ConfigurationUiRoutes.VERSIONS_ROUTE,
            "Pages:ConfigurationVersions:Title",
            MoIcons.Restore,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 25);
        registry.RegisterLocalizedPage<ConfigurationDebugPage, ConfigurationUIResource>(
            ConfigurationUiRoutes.DEBUG_ROUTE,
            "Pages:ConfigurationDebug:Title",
            MoIcons.Bug,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 30);
        registry.RegisterLocalizedPage<ConfigurationSourcesPage, ConfigurationUIResource>(
            ConfigurationUiRoutes.SOURCES_ROUTE,
            "Pages:ConfigurationSources:Title",
            MoIcons.Workflow,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 35);
        registry.RegisterLocalizedPage<ConfigurationStoragePage, ConfigurationUIResource>(
            ConfigurationUiRoutes.STORAGE_ROUTE,
            "Pages:ConfigurationStorage:Title",
            MoIcons.Database,
            BuiltInNavigationCategoryIds.Configuration,
            addToNav: true,
            navOrder: 40);
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleConfigurationUIOption> context)
    {
        var services = context.Services;
        services.AddScoped<ConfigurationStateStore>();
        services.AddScoped<ConfigurationPendingChangeCompactor>();
        services.AddScoped<ConfigurationJsonDraftService>();
        services.AddScoped<ConfigurationParameterPackageService>();
        services.AddSingleton<ConfigurationValueVisibility>();
    }
}

/// <summary>
/// Configuration options for Monica.Configuration.UI.
/// </summary>
public class ModuleConfigurationUIOption : ModuleOptions<ModuleConfigurationUI>
{
    /// <summary>
    /// Gets or sets whether the operator UI displays all configuration values without sensitive-value masking.
    /// </summary>
    /// <remarks>
    /// Disabled by default. Enabling this reveals annotated values, historical and unknown-schema values,
    /// JSON editors and previews, source inventories and files, exports, and raw diagnostics. It also allows
    /// copying sensitive text. This is a host configuration setting, not a per-user reveal preference.
    /// It does not alter stored values, sensitivity annotations, validation, or write authorization.
    /// </remarks>
    public bool ShowSensitiveValues { get; set; }

    /// <summary>
    /// Gets or sets whether saving a configuration mutation group requires an additional confirmation that lists
    /// the logical services affected by the changed definitions.
    /// </summary>
    /// <remarks>
    /// This option is intended for microservice architectures in which multiple logical services share a Monica
    /// configuration store. It is disabled by default because a single-process or modular-monolith host normally
    /// does not need a cross-service impact prompt. The displayed impact is a point-in-time view of publisher
    /// metadata, not proof of service liveness or reload delivery. If impact analysis is unavailable, the operator
    /// may explicitly choose to save anyway; normal validation and optimistic-concurrency checks still apply.
    /// </remarks>
    public bool EnableAffectedServiceConfirmation { get; set; }
}
