using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Profiling.TypeAllocation.Facades;
using Monica.Profiling.TypeAllocation.Models;
using Monica.Profiling.TypeAllocation.Providers.ClrMd;
using Monica.Profiling.TypeAllocation.Providers.TraceEvent;
using Monica.Profiling.TypeAllocation.Services;
using Monica.Profiling.TypeAllocation.Services.Support;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the type allocation module.
/// </summary>
public static class ModuleTypeAllocationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the type allocation module.
        /// </summary>
        public ModuleRegistration<ModuleTypeAllocation, ModuleTypeAllocationOption> AddTypeAllocation(
            Action<ModuleTypeAllocationOption>? action = null)
        {
            return builder.AddModule<ModuleTypeAllocation, ModuleTypeAllocationOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleTypeAllocation, ModuleTypeAllocationOption> registration)
    {
        /// <summary>
        /// Starts allocation collection with the host and registers the hosted-service dependency required for it.
        /// </summary>
        /// <param name="samplingMode">The requested legacy mode when automatic collection starts. Both enabled modes use the same CLR allocation-tick stream.</param>
        /// <returns>The same host-bound registration.</returns>
        public ModuleRegistration<ModuleTypeAllocation, ModuleTypeAllocationOption> StartAutomatically(
            AllocationSamplingMode samplingMode = AllocationSamplingMode.High)
        {
            registration.Require<ModuleHostedService, ModuleHostedServiceOption>();
            return registration.Configure(options =>
            {
                options.AutoStartCollection = true;
                options.DefaultSamplingMode = samplingMode;
            });
        }
    }
}

/// <summary>
/// Type allocation tracking module.
/// </summary>
public class ModuleTypeAllocation : MonicaModule<ModuleTypeAllocationOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleTypeAllocationOption> context)
    {
        var services = context.Services;
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TraceEventTypeAllocationProvider>>();
            return new TraceEventTypeAllocationProvider(
                logger,
                maxTrackedTypes: Option.MaxTrackedTypes,
                autoStopAfter: Option.AutoStopAfter);
        });
        services.AddSingleton<ClrMdHeapSnapshotProvider>();
        services.AddSingleton<TypeAllocationTrackingService>();
        services.AddScoped(sp => new TypeAllocationFacade(
            sp.GetRequiredService<TypeAllocationTrackingService>(),
            sp.GetRequiredService<ILogger<TypeAllocationFacade>>()));

        if (Option.AutoStartCollection)
        {
            services.AddHostedService<TypeAllocationAutoStartHostedService>();
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)),
            typeof(TypeAllocationAutoStartHostedService));
        }
    }
}

/// <summary>
/// Configuration options for the type allocation module.
/// </summary>
public class ModuleTypeAllocationOption : ModuleOptions<ModuleTypeAllocation>
{
    /// <summary>
    /// Automatically starts type allocation collection when the module initializes.
    /// This is useful for unattended diagnostics, but it can increase startup overhead.
    /// </summary>
    public bool AutoStartCollection { get; internal set; }

    /// <summary>
    /// Selects the requested legacy mode used when automatic collection starts; defaults to High.
    /// Low and High use the same current-process CLR allocation-tick stream and do not imply distinct
    /// sampling percentages, event rates, or fidelity.
    /// Set this to <see cref="AllocationSamplingMode.Disabled" /> to keep auto-start registration without beginning collection.
    /// </summary>
    public AllocationSamplingMode DefaultSamplingMode { get; set; } = AllocationSamplingMode.High;

    /// <summary>
    /// Caps retained rows in the live type ranking; defaults to 500. Larger values keep more type rows
    /// at the cost of memory, without changing event sampling. Evicted rows remain in cumulative totals.
    /// Set this to zero for totals-only collection with no retained type rows. Negative values are invalid.
    /// </summary>
    public int MaxTrackedTypes { get; set; } = 500;

    /// <summary>
    /// Stops each started ETW session after the configured duration, whether started manually or with
    /// the host. Defaults to ten minutes. Set this to <see langword="null" /> to require explicit stop
    /// or host retirement. Non-null values must be from 1 through 4294967294 milliseconds, inclusive.
    /// </summary>
    public TimeSpan? AutoStopAfter { get; set; } = TimeSpan.FromMinutes(10);
}
