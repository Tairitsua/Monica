using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Profiling.Localization;
using Monica.Profiling.Pages;
using Monica.Profiling.UIExecutionTiming.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleExecutionTimingUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the execution-timing UI module.
        /// </summary>
        public ModuleRegistration<ModuleExecutionTimingUI, ModuleExecutionTimingUIOption> AddExecutionTimingUI(
            Action<ModuleExecutionTimingUIOption>? action = null)
        {
            return builder.AddModule<ModuleExecutionTimingUI, ModuleExecutionTimingUIOption>(action);
        }
    }
}

/// <summary>
/// Execution-timing UI module.
/// </summary>
public class ModuleExecutionTimingUI : MonicaModule<ModuleExecutionTimingUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionTiming, ModuleExecutionTimingOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(static option =>
        {
            option.AddResource<ProfilingResource>();
            option.AddResource<ExecutionTimingResource>();
        });
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<ExecutionTimingResource>(
                    "Monica.Profiling.ExecutionTiming", "Navigation:Category", 70,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Timer);

                registry.RegisterLocalizedPage<UIExecutionTimingPage, ExecutionTimingResource>(
                    UIExecutionTimingPage.PAGE_URL,
                    "Pages:ExecutionTiming:Title",
                    MoIcons.Timer,
                    category,
                    addToNav: true,
                    navOrder: 30,
                    descriptionKey: "Page:Subtitle");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleExecutionTimingUIOption> context)
    {
        context.Services.AddScoped<ExecutionTimingPageStateFactory>();
    }
}

/// <summary>
/// Configuration options for the execution-timing UI module.
/// </summary>
public class ModuleExecutionTimingUIOption : ModuleOptions<ModuleExecutionTimingUI>
{
    /// <summary>
    /// Controls the automatic refresh interval of the execution-timing page in milliseconds.
    /// The default is 2000 milliseconds. A non-positive value disables timer-based refresh
    /// and makes the page manual-only. Pausing page refresh does not pause recording.
    /// </summary>
    public int AutoRefreshIntervalMs { get; set; } = 2000;
}
