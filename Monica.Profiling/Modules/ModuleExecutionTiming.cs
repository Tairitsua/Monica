using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Execution;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Profiling.ExecutionTiming.Abstractions;
using Monica.Profiling.ExecutionTiming.Abstractions.Internal;
using Monica.Profiling.ExecutionTiming.Facades;
using Monica.Profiling.ExecutionTiming.Models;
using Monica.Profiling.ExecutionTiming.Services;
using Monica.Profiling.ExecutionTiming.Services.Behaviors;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleExecutionTimingBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the execution-timing diagnostics module.
        /// </summary>
        public ModuleRegistration<ModuleExecutionTiming, ModuleExecutionTimingOption> AddExecutionTiming(
            Action<ModuleExecutionTimingOption>? action = null)
        {
            return builder.AddModule<ModuleExecutionTiming, ModuleExecutionTimingOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleExecutionTiming, ModuleExecutionTimingOption> registration)
    {
        /// <summary>
        /// Aggregates timing samples immediately on the caller thread.
        /// Use this mode when deterministic immediate statistics visibility matters more than write-path cost.
        /// </summary>
        public ModuleRegistration<ModuleExecutionTiming, ModuleExecutionTimingOption> UseInlineAggregation()
        {
            return registration.Configure(option => option.AggregationMode = ExecutionTimingAggregationMode.Inline);
        }

        /// <summary>
        /// Aggregates timing samples on a dedicated background service.
        /// This reduces hot-path write overhead and is closer to the historical MoTimekeeper behavior.
        /// </summary>
        /// <param name="flushInterval">
        /// Optional flush interval for draining the background queue into aggregated statistics.
        /// When omitted, the module option value is used.
        /// </param>
        public ModuleRegistration<ModuleExecutionTiming, ModuleExecutionTimingOption> UseBackgroundBatchAggregation(
            TimeSpan? flushInterval = null)
        {
            registration.Require<ModuleHostedService, ModuleHostedServiceOption>();
            return registration.Configure(option =>
            {
                option.AggregationMode = ExecutionTimingAggregationMode.BackgroundBatch;
                if (flushInterval.HasValue)
                {
                    option.BackgroundFlushInterval = flushInterval.Value;
                }
            });
        }
    }
}

/// <summary>
/// Execution-timing module.
/// </summary>
public class ModuleExecutionTiming : MonicaModule<ModuleExecutionTimingOption>, IWebModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(option =>
            option.AddBehavior(
                typeof(ExecutionTimingBehavior<,>),
                ExecutionBehaviorOrder.Diagnostics + 100,
                static descriptor => descriptor.IsBusinessOperation));
    }

    public override void ConfigureServices(ModuleContext<ModuleExecutionTimingOption> context)
    {
        var services = context.Services;
        services.AddSingleton<ExecutionTimingCollector>();

        if (Option.AggregationMode == ExecutionTimingAggregationMode.BackgroundBatch)
        {
            services.AddSingleton<BackgroundExecutionTimingCoordinator>();
            services.AddSingleton<IExecutionTimingCoordinator>(sp => sp.GetRequiredService<BackgroundExecutionTimingCoordinator>());
            services.AddSingleton<IExecutionTimingQuery>(sp => sp.GetRequiredService<BackgroundExecutionTimingCoordinator>());
            services.AddHostedService(sp => sp.GetRequiredService<BackgroundExecutionTimingCoordinator>());
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)),
            typeof(BackgroundExecutionTimingCoordinator));
        }
        else
        {
            services.AddSingleton<InlineExecutionTimingCoordinator>();
            services.AddSingleton<IExecutionTimingCoordinator>(sp => sp.GetRequiredService<InlineExecutionTimingCoordinator>());
            services.AddSingleton<IExecutionTimingQuery>(sp => sp.GetRequiredService<InlineExecutionTimingCoordinator>());
        }

        services.AddSingleton<IExecutionTimingFactory, ExecutionTimingFactory>();
        services.AddSingleton<ExecutionTimingService>();
        services.AddScoped(sp => new ExecutionTimingFacade(
            sp.GetRequiredService<ExecutionTimingService>(),
            sp.GetRequiredService<ILogger<ExecutionTimingFacade>>()));
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleExecutionTimingOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet(
                    "/execution-timing/statistics",
                    ([FromServices] ExecutionTimingFacade facade) => facade.GetStatistics().GetResponse())
                .WithName("GetExecutionTimingStatistics")
                .WithTags(tagName)
                .WithSummary("Gets aggregated execution-timing statistics.")
                .WithDescription("Returns completed execution-timing statistics ordered by average duration.");

            endpoints.MapGet(
                    "/execution-timing/running",
                    ([FromServices] ExecutionTimingFacade facade) => facade.GetRunningOperations().GetResponse())
                .WithName("GetRunningExecutionTimingOperations")
                .WithTags(tagName)
                .WithSummary("Gets currently running execution-timing operations.")
                .WithDescription("Returns currently running execution-timing operations ordered by elapsed duration.");
        });
    }
}

/// <summary>
/// Configuration options for the execution-timing module.
/// </summary>
/// <remarks>
/// Use the inherited <see cref="MinimalApiModuleOptions{ModuleExecutionTiming}.EnableMinimalApi" /> switch to control
/// the diagnostic endpoints. Generic Hosts retain collection and aggregation while skipping Web-only endpoints.
/// </remarks>
public class ModuleExecutionTimingOption : MinimalApiModuleOptions<ModuleExecutionTiming>
{
    /// <summary>
    /// Controls whether execution-timing samples are aggregated inline or by a background batching service.
    /// Inline aggregation is the default. Select background batching through
    /// <see cref="ModuleExecutionTimingBuilderExtensions.UseBackgroundBatchAggregation"/> so Monica also declares the
    /// hosted-service dependency required by that mode.
    /// </summary>
    public ExecutionTimingAggregationMode AggregationMode { get; internal set; } = ExecutionTimingAggregationMode.Inline;

    /// <summary>
    /// Controls how often the background aggregation service drains queued timing samples.
    /// This value is used only when <see cref="AggregationMode" /> is <see cref="ExecutionTimingAggregationMode.BackgroundBatch" />.
    /// Set a positive value; invalid values fall back to an internal default.
    /// </summary>
    public TimeSpan BackgroundFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}
