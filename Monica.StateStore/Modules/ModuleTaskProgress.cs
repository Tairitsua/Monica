using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.StateStore.Abstractions;
using Monica.StateStore.Cancellation.Abstractions;
using Monica.StateStore.Cancellation.Services;
using Monica.StateStore.TaskProgress.Abstractions;
using Monica.StateStore.TaskProgress.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleTaskProgressBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the task progress module.
        /// </summary>
        public ModuleRegistration<ModuleTaskProgress, ModuleTaskProgressOption> AddTaskProgress(Action<ModuleTaskProgressOption>? action = null)
        {
            return builder.AddModule<ModuleTaskProgress, ModuleTaskProgressOption>(action);
        }
    }
}

public class ModuleTaskProgress : MonicaModule<ModuleTaskProgressOption>
{
    /// <inheritdoc />
    public override void ValidateOptions(ModuleTaskProgressOption options, string? profileName)
    {
        if (!Enum.IsDefined(options.StorageMode))
        {
            throw new InvalidOperationException(
                $"Unsupported {nameof(TaskProgressStorageMode)} value '{options.StorageMode}'.");
        }
    }

    public override void ConfigureServices(ModuleContext<ModuleTaskProgressOption> context)
    {
        var services = context.Services;
        switch (Option.StorageMode)
        {
            case TaskProgressStorageMode.Memory:
                services.AddKeyedSingleton<IStateStore>(nameof(ModuleTaskProgress), static (provider, _) =>
                    provider.GetRequiredService<IMemoryStateStore>());
                services.AddKeyedSingleton<ICancellationManager, InMemoryCancellationManager>(nameof(ModuleTaskProgress));
                break;
            case TaskProgressStorageMode.Distributed:
                services.AddKeyedSingleton<IStateStore>(nameof(ModuleTaskProgress), static (provider, _) =>
                    provider.GetRequiredService<IDistributedStateStore>());
                services.AddKeyedSingleton<ICancellationManager>(nameof(ModuleTaskProgress), static (provider, _) =>
                {
                    var stateStore = provider.GetRequiredKeyedService<IStateStore>(nameof(ModuleTaskProgress));
                    return ActivatorUtilities.CreateInstance<DistributedCancellationManager>(provider, stateStore);
                });
                break;
            default:
                throw new InvalidOperationException("Task progress storage validation did not run.");
        }

        services.AddSingleton<ITaskProgressService, TaskProgressService>();
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleCancellationManager>();
        module.Require<ModuleStateStore>();
    }

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleTaskProgressOption> contracts)
    {
        if (contracts.Options.StorageMode == TaskProgressStorageMode.Memory)
        {
            contracts.RequireService<IMemoryStateStore>();
            return;
        }

        contracts.RequireService<IDistributedStateStore>();
    }
}

/// <summary>
/// Configures task-progress provider features.
/// </summary>
public static class ModuleTaskProgressRegistrationExtensions
{
    /// <summary>
    /// Stores progress and cancellation state inside the current process. This is the default mode.
    /// </summary>
    /// <param name="module">The TaskProgress registration being configured.</param>
    /// <returns>The same host-bound registration.</returns>
    public static ModuleRegistration<ModuleTaskProgress, ModuleTaskProgressOption> UseMemoryStorage(
        this ModuleRegistration<ModuleTaskProgress, ModuleTaskProgressOption> module)
    {
        return module.Configure(options => options.StorageMode = TaskProgressStorageMode.Memory);
    }

    /// <summary>
    /// Stores progress and cancellation state in the distributed StateStore provider selected for this host.
    /// </summary>
    /// <param name="module">The TaskProgress registration being configured.</param>
    /// <returns>The same host-bound registration.</returns>
    public static ModuleRegistration<ModuleTaskProgress, ModuleTaskProgressOption> UseDistributedStorage(
        this ModuleRegistration<ModuleTaskProgress, ModuleTaskProgressOption> module)
    {
        module.Require<ModuleStateStore, ModuleStateStoreOption>()
            .RequireFeature(ModuleStateStore.DISTRIBUTED_PROVIDER_FEATURE);
        return module.Configure(options => options.StorageMode = TaskProgressStorageMode.Distributed);
    }
}


/// <summary>
/// Configures the persistence boundary for task progress and cancellation signals.
/// </summary>
public class ModuleTaskProgressOption : ModuleOptions<ModuleTaskProgress>
{
    /// <summary>
    /// Gets the storage boundary for task progress and its cancellation signals. The default is
    /// <see cref="TaskProgressStorageMode.Memory"/>. Select distributed storage when progress must be shared across
    /// instances or recovered by another process.
    /// </summary>
    public TaskProgressStorageMode StorageMode { get; internal set; } = TaskProgressStorageMode.Memory;
}

/// <summary>
/// Defines where task progress and its cancellation signals are persisted.
/// </summary>
public enum TaskProgressStorageMode
{
    /// <summary>
    /// Keeps task progress and cancellation state inside the current process.
    /// </summary>
    Memory,

    /// <summary>
    /// Persists task progress and cancellation state through the distributed StateStore provider.
    /// </summary>
    Distributed
}
