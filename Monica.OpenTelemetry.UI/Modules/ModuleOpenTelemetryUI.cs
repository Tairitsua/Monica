using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.OpenTelemetry.UI.Localization;
using Monica.OpenTelemetry.UI.UIOpenTelemetry.Pages;
using Monica.OpenTelemetry.UI.UIOpenTelemetry.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions used to register the Monica OpenTelemetry dashboard UI module.
/// </summary>
public static class ModuleOpenTelemetryUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the OpenTelemetry dashboard UI module and applies optional module configuration.
        /// </summary>
        /// <param name="action">Optional module option configuration delegate.</param>
        /// <returns>The host-bound OpenTelemetry UI registration.</returns>
        public ModuleRegistration<ModuleOpenTelemetryUI, ModuleOpenTelemetryUIOption> AddOpenTelemetryUI(
            Action<ModuleOpenTelemetryUIOption>? action = null)
        {
            return builder.AddModule<ModuleOpenTelemetryUI, ModuleOpenTelemetryUIOption>(action);
        }
    }
}

/// <summary>
/// UI module that contributes the OpenTelemetry metrics dashboard and enables Monica's in-process collector.
/// </summary>
public class ModuleOpenTelemetryUI : MonicaModule<ModuleOpenTelemetryUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleOpenTelemetry, ModuleOpenTelemetryOption>(
            static option => option.EnableInProcessCollector = true);
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<OpenTelemetryUIResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<OpenTelemetryUIResource>(
                    "Monica.OpenTelemetry", "Navigation:Category", 40,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.ChartLine);

                registry.RegisterLocalizedPage<UIOpenTelemetryDashboardPage, OpenTelemetryUIResource>(
                    UIOpenTelemetryDashboardPage.PAGE_URL,
                    "Pages:OpenTelemetryMetrics:Title",
                    MoIcons.Radio,
                    category,
                    addToNav: true,
                    navOrder: 12,
                    descriptionKey: "Pages:OpenTelemetryMetrics:Description");
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleOpenTelemetryUIOption> context)
    {
        context.Services.AddScoped<OpenTelemetryDashboardPageStateFactory>();
    }
}

/// <summary>
/// Configuration options for the Monica OpenTelemetry dashboard UI module.
/// </summary>
public class ModuleOpenTelemetryUIOption : ModuleOptions<ModuleOpenTelemetryUI>
{
}
