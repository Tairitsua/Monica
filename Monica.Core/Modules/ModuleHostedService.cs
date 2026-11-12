using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Abstractions.Internal;
using Monica.Core.HostedService.Metrics;
using Monica.Core.HostedService.Services;
using Monica.Core.HostedService.Services.Support;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleHostedServiceBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the HostedService module.
        /// </summary>
        public ModuleRegistration<ModuleHostedService, ModuleHostedServiceOption> AddHostedService(
            Action<ModuleHostedServiceOption>? action = null)
        {
            return builder.AddModule<ModuleHostedService, ModuleHostedServiceOption>(action);
        }
    }
}

/// <summary>
/// HostedService observability module.
/// Provides centralized HostedService state management, heartbeat monitoring, and coordination support.
/// </summary>
public class ModuleHostedService : MonicaModule<ModuleHostedServiceOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleObservableInstance, ModuleObservableInstanceOption>();
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>();
    }

    public override void ConfigureServices(ModuleContext<ModuleHostedServiceOption> context)
    {
        var services = context.Services;
        services.AddSingleton(new HostedServiceDescriptorCatalog(services));
        services.AddSingleton<IValidateOptions<HostedServiceRegistrationValidationOptions>, HostedServiceRegistrationValidator>();
        services.AddOptions<HostedServiceRegistrationValidationOptions>().ValidateOnStart();

        services.AddSingleton<HostedServiceRegistry>();
        services.AddSingleton<IHostedServiceRegistryWriter>(provider => provider.GetRequiredService<HostedServiceRegistry>());
        services.AddSingleton<IMoHostedServiceRegistry>(provider => provider.GetRequiredService<HostedServiceRegistry>());
        services.AddSingleton<HostedServiceCheckpointCoordinator>();
        services.AddSingleton<IHostedServiceRuntimeObserver>(provider => provider.GetRequiredService<HostedServiceCheckpointCoordinator>());
        services.AddSingleton<IMoHostedServiceCheckpointCoordinator>(provider => provider.GetRequiredService<HostedServiceCheckpointCoordinator>());
        services.TryAddSingleton<HostedServiceMetrics>();
        services.AddSingleton<IHostedServiceRuntimeObserver>(provider => provider.GetRequiredService<HostedServiceMetrics>());
        services.AddHostedService<HostedServiceRegistryLifecycle>();
        context.Composition.AdmitHostedLifecycle(services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(HostedServiceRegistryLifecycle)), typeof(HostedServiceRegistryLifecycle));
    }
}

/// <summary>
/// Configuration options for the HostedService observability module
/// </summary>
public class ModuleHostedServiceOption : ModuleOptions<ModuleHostedService>
{
    /// <summary>
    /// Gets or sets the default maximum history size for all services
    /// </summary>
    public int DefaultMaxHistorySize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the default heartbeat interval for BackgroundServices
    /// </summary>
    public TimeSpan DefaultHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets whether to fail fast (throw exception) when StartAsync encounters an error.
    /// When true, exceptions during service startup will be re-thrown, causing the application to fail fast.
    /// When false, exceptions are logged but not re-thrown, allowing the application to continue.
    /// Default is false for production stability.
    /// </summary>
    public bool FailFastOnStartupError { get; set; }
}
