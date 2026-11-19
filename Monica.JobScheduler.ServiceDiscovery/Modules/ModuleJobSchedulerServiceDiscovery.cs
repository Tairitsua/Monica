using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.JobScheduler.UI.UIJobScheduler.Abstractions;
using Monica.JobScheduler.ServiceDiscovery.Providers;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleJobSchedulerServiceDiscoveryBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Enriches the JobScheduler runtime workspace with ServiceDiscovery-backed worker-instance insight.
        /// </summary>
        /// <remarks>
        /// Requires <c>AddServiceDiscovery(...)</c> with a storage visible to every host that should observe the
        /// instances — <c>UseDistributedStorage()</c> for a shared cross-service view, <c>UseMemoryStorage()</c>
        /// for a single-host development view. Without this module the runtime workspace hides its worker table
        /// behind a setup hint instead of failing.
        /// </remarks>
        public ModuleRegistration<ModuleJobSchedulerServiceDiscovery, ModuleJobSchedulerServiceDiscoveryOption>
            AddJobSchedulerServiceDiscoveryInsight()
        {
            return builder.AddModule<ModuleJobSchedulerServiceDiscovery, ModuleJobSchedulerServiceDiscoveryOption>();
        }
    }
}

/// <summary>
/// Bridges ServiceDiscovery heartbeats into the JobScheduler runtime workspace so the worker-instance table can
/// show live replica counts, capacities, and liveness alongside the scheduler's durable owner footprint.
/// </summary>
public sealed class ModuleJobSchedulerServiceDiscovery : MonicaModule<ModuleJobSchedulerServiceDiscoveryOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleServiceDiscovery>();
        module.Require<ModuleJobSchedulerUI>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleJobSchedulerServiceDiscoveryOption> context)
    {
        context.Services.TryAddScoped<IJobSchedulerWorkerInsightProvider, ServiceDiscoveryWorkerInsightProvider>();
    }
}

/// <summary>
/// Configures the ServiceDiscovery integration for the JobScheduler runtime workspace. Reserved for future
/// integration-specific settings; it currently adds no behavior.
/// </summary>
public sealed class ModuleJobSchedulerServiceDiscoveryOption : ModuleOptions<ModuleJobSchedulerServiceDiscovery>;
