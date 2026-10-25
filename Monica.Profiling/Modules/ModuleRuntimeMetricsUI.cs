using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Profiling.Localization;
using Monica.Profiling.Pages;
using Monica.Profiling.UIRuntimeMetrics.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the runtime metrics UI module.
/// </summary>
public static class ModuleRuntimeMetricsUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the runtime metrics UI module.
        /// </summary>
        public ModuleRegistration<ModuleRuntimeMetricsUI, ModuleRuntimeMetricsUIOption> AddRuntimeMetricsUI(
            Action<ModuleRuntimeMetricsUIOption>? action = null)
        {
            return builder.AddModule<ModuleRuntimeMetricsUI, ModuleRuntimeMetricsUIOption>(action);
        }
    }
}

/// <summary>
/// Runtime metrics UI module.
/// </summary>
public class ModuleRuntimeMetricsUI : MonicaModule<ModuleRuntimeMetricsUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleRuntimeMetrics, ModuleRuntimeMetricsOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(static option =>
        {
            option.AddResource<ProfilingResource>();
            option.AddResource<RuntimeMetricsResource>();
        });
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<RuntimeMetricsResource>(
                    "Monica.Profiling.RuntimeMetrics", "Navigation:Category", 50,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Gauge);

                registry.RegisterLocalizedPage<UIRuntimeMetricsPage, RuntimeMetricsResource>(
                    UIRuntimeMetricsPage.PAGE_URL,
                    "Pages:RuntimeMetrics:Title",
                    MoIcons.Gauge,
                    category,
                    addToNav: true,
                    navOrder: 10,
                    descriptionKey: "Pages:RuntimeMetrics:Description");
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleRuntimeMetricsUIOption> context)
    {
        context.Services.AddScoped<RuntimeMetricsPageStateFactory>();
    }
}

/// <summary>
/// Configuration options for the runtime metrics UI module.
/// </summary>
public class ModuleRuntimeMetricsUIOption : ModuleOptions<ModuleRuntimeMetricsUI>
{
    /// <summary>
    /// Controls the automatic refresh interval of the runtime metrics page in milliseconds.
    /// Defaults to 2,000 milliseconds. Non-positive values disable view polling and require manual refresh.
    /// This option does not stop or change the host-owned runtime collector's sampling interval.
    /// </summary>
    public int AutoRefreshIntervalMs { get; set; } = 2000;
}
