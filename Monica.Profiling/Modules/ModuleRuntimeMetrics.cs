using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Metrics;
using Monica.Profiling.RuntimeMetrics.Providers.EventCounters;
using Monica.Profiling.RuntimeMetrics.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the runtime metrics module.
/// </summary>
public static class ModuleRuntimeMetricsBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the runtime metrics module.
        /// </summary>
        public ModuleRegistration<ModuleRuntimeMetrics, ModuleRuntimeMetricsOption> AddRuntimeMetrics(
            Action<ModuleRuntimeMetricsOption>? action = null)
        {
            return builder.AddModule<ModuleRuntimeMetrics, ModuleRuntimeMetricsOption>(action);
        }
    }
}

/// <summary>
/// Runtime metrics module.
/// </summary>
public class ModuleRuntimeMetrics : MonicaModule<ModuleRuntimeMetricsOption>, IWebModule
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleRuntimeMetricsOption> context)
    {
        var services = context.Services;
        services.AddSingleton(_ => new RuntimeMetricsCollector(
            maxHistoryPoints: Option.MaxHistoryPoints,
            sampleIntervalMs: Option.SampleIntervalMs));
        services.TryAddSingleton<RuntimeMetrics>();
        services.AddSingleton<RuntimeMetricsService>();
        services.AddHostedService<RuntimeMetricsActivationService>();
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)),
            typeof(RuntimeMetricsActivationService));
        services.AddScoped(sp => new RuntimeMetricsFacade(
            sp.GetRequiredService<RuntimeMetricsService>(),
            sp.GetRequiredService<ILogger<RuntimeMetricsFacade>>()));
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleRuntimeMetricsOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet(
                    "/runtime-metrics/latest",
                    ([FromServices] RuntimeMetricsFacade facade) => facade.GetLatestPoint().GetResponse())
                .WithName("GetLatestRuntimeMetrics")
                .WithTags(tagName)
                .WithSummary("Gets the latest runtime metrics sample.")
                .WithDescription("Returns the latest collected CPU, memory, GC, and allocation metrics for the current process.");
        });
    }
}

/// <summary>
/// Configuration options for the runtime metrics module.
/// </summary>
public class ModuleRuntimeMetricsOption : MinimalApiModuleOptions<ModuleRuntimeMetrics>
{
    /// <summary>
    /// Maximum number of runtime metric points retained in memory for trend displays.
    /// </summary>
    public int MaxHistoryPoints { get; set; } = 300;

    /// <summary>
    /// Sampling interval, in milliseconds, used by the runtime EventCounter collector.
    /// Smaller values improve freshness but increase collection overhead.
    /// </summary>
    public int SampleIntervalMs { get; set; } = 1000;
}
