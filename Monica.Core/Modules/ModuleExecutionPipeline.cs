using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Execution;
using Monica.Core.Execution.Abstractions;
using Monica.Core.Execution.Facades;
using Monica.Core.Execution.Models.Internal;
using Monica.Core.Execution.Services;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides host-bound registration for Monica's typed execution pipeline.
/// </summary>
public static class ModuleExecutionPipelineBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the shared execution-pipeline kernel for the current Monica host.
        /// </summary>
        /// <param name="configure">Optional module configuration.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<ModuleExecutionPipeline, ModuleExecutionPipelineOption> AddExecutionPipeline(
            Action<ModuleExecutionPipelineOption>? configure = null)
        {
            return builder.AddModule<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(configure);
        }
    }

    extension(ModuleRegistration<ModuleExecutionPipeline, ModuleExecutionPipelineOption> module)
    {
        /// <summary>
        /// Adds an execution behavior to the current host.
        /// </summary>
        public ModuleRegistration<ModuleExecutionPipeline, ModuleExecutionPipelineOption> AddBehavior(
            Type behaviorType,
            int order = ExecutionBehaviorOrder.Application,
            Func<ExecutionDescriptor, bool>? descriptorFilter = null,
            ServiceLifetime lifetime = ServiceLifetime.Transient)
        {
            return module.Configure(options =>
                options.AddBehavior(behaviorType, order, descriptorFilter, lifetime));
        }

        /// <summary>
        /// Adds a closed execution behavior to the current host.
        /// </summary>
        public ModuleRegistration<ModuleExecutionPipeline, ModuleExecutionPipelineOption> AddBehavior<TBehavior>(
            int order = ExecutionBehaviorOrder.Application,
            Func<ExecutionDescriptor, bool>? descriptorFilter = null,
            ServiceLifetime lifetime = ServiceLifetime.Transient)
            where TBehavior : class
        {
            return module.AddBehavior(typeof(TBehavior), order, descriptorFilter, lifetime);
        }
    }
}

/// <summary>
/// Registers the shared typed execution pipeline and its host-owned behavior catalog.
/// </summary>
public sealed class ModuleExecutionPipeline : MonicaModule<ModuleExecutionPipelineOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleExecutionPipelineOption> context)
    {
        var services = context.Services;
        var registrations = Option.BehaviorRegistrations.ToArray();

        foreach (var registration in registrations)
        {
            services.Add(registration.CreateServiceDescriptor());
        }

        services.TryAddSingleton<IReadOnlyList<ExecutionBehaviorRegistration>>(registrations);
        services.TryAddSingleton(_ =>
            new ExecutionBehaviorServiceRegistrationValidator(registrations, services));
        services.TryAddSingleton<ExecutionBehaviorPlanCache>();
        services.TryAddSingleton<ExecutionScopeContext>();
        services.TryAddSingleton<IExecutionPipelineCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecutionBehaviorPlanCache>());
        services.TryAddSingleton<ExecutionPipelineCatalogFacade>();
        services.TryAddScoped<IExecutionPipeline, ExecutionPipeline>();
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleExecutionPipelineOption> context)
    {
        foreach (var registration in Option.BehaviorRegistrations)
        {
            registration.ValidateExclusiveServiceRegistration(context.Services);
        }
    }
}

/// <summary>
/// Configures the shared execution-pipeline module for one Monica host.
/// </summary>
public sealed class ModuleExecutionPipelineOption : ModuleOptions<ModuleExecutionPipeline>
{
    private readonly Dictionary<Type, ExecutionBehaviorRegistration> _behaviorRegistrations = [];

    internal IReadOnlyCollection<ExecutionBehaviorRegistration> BehaviorRegistrations =>
        _behaviorRegistrations.Values;

    /// <summary>
    /// Adds one ordered behavior implementation to the host-owned execution pipeline.
    /// </summary>
    public void AddBehavior(
        Type behaviorType,
        int order = ExecutionBehaviorOrder.Application,
        Func<ExecutionDescriptor, bool>? descriptorFilter = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        var registration = ExecutionBehaviorRegistration.Create(
            behaviorType,
            order,
            descriptorFilter,
            lifetime,
            sourceModuleKey: null);

        if (!_behaviorRegistrations.TryAdd(registration.ImplementationType, registration))
        {
            throw new InvalidOperationException(
                $"Execution behavior '{registration.ImplementationType.FullName}' is already registered for this host. " +
                "Register each behavior implementation type only once.");
        }
    }
}
