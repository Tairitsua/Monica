using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Profiling.Localization;
using Monica.Profiling.Pages;
using Monica.Profiling.UIMemoryAnalysis.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the memory analysis UI module.
/// </summary>
public static class ModuleMemoryAnalysisUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the memory analysis UI module.
        /// </summary>
        public ModuleRegistration<ModuleMemoryAnalysisUI, ModuleMemoryAnalysisUIOption> AddMemoryAnalysisUI(
            Action<ModuleMemoryAnalysisUIOption>? action = null)
        {
            return builder.AddModule<ModuleMemoryAnalysisUI, ModuleMemoryAnalysisUIOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleMemoryAnalysisUI, ModuleMemoryAnalysisUIOption> registration)
    {
        /// <summary>
        /// Enables the optional allocation workspace and its host-owned allocation backend.
        /// </summary>
        /// <returns>The same host-bound memory-analysis registration.</returns>
        public ModuleRegistration<ModuleMemoryAnalysisUI, ModuleMemoryAnalysisUIOption> EnableTypeAllocationTab()
        {
            registration.Require<ModuleTypeAllocation>();
            return registration.Configure(option => option.EnableTypeAllocationTab = true);
        }
    }
}

/// <summary>
/// Memory analysis UI module.
/// </summary>
public class ModuleMemoryAnalysisUI : MonicaModule<ModuleMemoryAnalysisUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleMemoryDiagnostics>();
        module.Require<ModuleRuntimeMetrics>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(static option =>
        {
            option.AddResource<ProfilingResource>();
            option.AddResource<MemoryAnalysisResource>();
            option.AddResource<RuntimeMetricsResource>();
        });
        module.Require<ModuleShellUI, ModuleShellUIOption>(option =>
            option.ConfigureNavigation(registry =>
            {
                var includeAllocations = Option.EnableTypeAllocationTab;
                var category = registry.RegisterLocalizedCategory<MemoryAnalysisResource>(
                    "Monica.Profiling.MemoryAnalysis", "Pages:MemoryAnalysis:Title", 60,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Cpu,
                    descriptionKey: "Pages:MemoryAnalysis:Description");
                registry.RegisterLocalizedPage<UIMemoryAnalysisPage, MemoryAnalysisResource>(
                    UIMemoryAnalysisPage.PAGE_URL, "Pages:MemoryAnalysis:Overview:Title",
                    MoIcons.Cpu, category, addToNav: true, navOrder: 10,
                    descriptionKey: "Pages:MemoryAnalysis:Overview:Description");
                registry.RegisterLocalizedPage<UIMemoryGcPage, MemoryAnalysisResource>(
                    UIMemoryGcPage.PAGE_URL, "Pages:MemoryAnalysis:Gc:Title",
                    MoIcons.Layers, category, addToNav: true, navOrder: 20,
                    descriptionKey: "Pages:MemoryAnalysis:Gc:Description");
                registry.RegisterLocalizedPage<UIMemoryHistoryPage, MemoryAnalysisResource>(
                    UIMemoryHistoryPage.PAGE_URL, "Pages:MemoryAnalysis:History:Title",
                    MoIcons.ChartLine, category, addToNav: true, navOrder: 30,
                    descriptionKey: "Pages:MemoryAnalysis:History:Description");
                registry.RegisterLocalizedPage<UIMemoryAllocationsPage, MemoryAnalysisResource>(
                    UIMemoryAllocationsPage.PAGE_URL, "Pages:MemoryAnalysis:Allocations:Title",
                    MoIcons.Shapes, category, addToNav: includeAllocations, navOrder: 40,
                    descriptionKey: "Pages:MemoryAnalysis:Allocations:Description");
                registry.RegisterLocalizedPage<UIMemoryDiagnosticsPage, MemoryAnalysisResource>(
                    UIMemoryDiagnosticsPage.PAGE_URL, "Pages:MemoryAnalysis:Diagnostics:Title",
                    MoIcons.Wrench, category, addToNav: true, navOrder: 50,
                    descriptionKey: "Pages:MemoryAnalysis:Diagnostics:Description");
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleMemoryAnalysisUIOption> context)
    {
        context.Services.AddScoped<MemoryAnalysisPageStateFactory>();
        if (Option.EnableTypeAllocationTab)
        {
            context.Services.AddScoped<TypeAllocationPanelStateFactory>();
        }
    }
}

/// <summary>
/// Configuration options for the memory analysis UI module.
/// </summary>
public class ModuleMemoryAnalysisUIOption : ModuleOptions<ModuleMemoryAnalysisUI>
{
    /// <summary>
    /// Controls overview, GC and history view reads in milliseconds; defaults to 2000.
    /// Non-positive values require manual reads. Diagnostics always starts in manual mode.
    /// This does not configure or stop the host-owned runtime metrics collector.
    /// </summary>
    public int AutoRefreshIntervalMs { get; set; } = 2000;

    /// <summary>
    /// Allows the diagnostics page to request manual garbage collection; defaults to true.
    /// The page state enforces this admission policy as well as disabling the control.
    /// This is not authorization for separate backend facade or HTTP callers.
    /// </summary>
    public bool AllowManualGC { get; set; } = true;

    /// <summary>
    /// Allows GC dump creation from the diagnostics page; defaults to true.
    /// The page state enforces this admission policy. The native tool and server write access
    /// are still required; a returned path is a server file, not a browser download.
    /// </summary>
    public bool AllowGcDump { get; set; } = true;

    /// <summary>
    /// Gets whether the optional allocations route is exposed in navigation; defaults to false.
    /// Use <c>EnableTypeAllocationTab()</c> on the registration to include its backend dependency.
    /// Opening or leaving the workspace does not start or stop host-owned allocation collection.
    /// </summary>
    public bool EnableTypeAllocationTab { get; internal set; }
}
