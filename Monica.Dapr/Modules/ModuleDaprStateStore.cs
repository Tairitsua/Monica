using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Dapr.Services;
using Monica.StateStore.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDaprStateStoreBuilderExtensions
{
    /// <summary>
    /// Selects Dapr as the host's common distributed state store provider.
    /// </summary>
    /// <param name="module">The StateStore registration that will use Dapr.</param>
    /// <param name="action">Optional Dapr state-store configuration.</param>
    /// <returns>The Dapr StateStore provider registration.</returns>
    /// <remarks>
    /// Typed values are serialized by the Dapr SDK with the canonical JSON contract configured for this Monica host.
    /// Changing that contract can make existing persisted state incompatible.
    /// </remarks>
    public static ModuleRegistration<ModuleDaprStateStore, ModuleDaprStateStoreOption> UseDaprStateStoreProvider(
        this ModuleRegistration<ModuleStateStore, ModuleStateStoreOption> module,
        Action<ModuleDaprStateStoreOption>? action = null)
    {
        module.SetCommonDistributedStateStoreProvider<DaprStateStoreProvider>();
        return module.Include<ModuleDaprStateStore, ModuleDaprStateStoreOption>(action);
    }
    
    /// <summary>
    /// Registers Dapr state store as a keyed state store provider.
    /// </summary>
    /// <param name="module">The StateStore registration being configured.</param>
    /// <param name="serviceKey">The service key that identifies this state store instance.</param>
    /// <param name="configureOptions">Delegate that configures the Dapr state store.</param>
    /// <returns>The same StateStore module registration for chaining.</returns>
    /// <remarks>
    /// The service key isolates Dapr store settings, but every keyed store uses the host's canonical JSON contract.
    /// </remarks>
    public static ModuleRegistration<ModuleStateStore, ModuleStateStoreOption> AddKeyedDaprStateStore(
        this ModuleRegistration<ModuleStateStore, ModuleStateStoreOption> module,
        string serviceKey,
        Action<ModuleDaprStateStoreOption> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var providerModule = module.Include<ModuleDaprStateStore, ModuleDaprStateStoreOption>()
            .ConfigureProfile(serviceKey, configureOptions);
        providerModule.ConfigureServices(context =>
        {
            var options = Options.Create(providerModule.GetProfile(serviceKey));
            context.Services.AddKeyedSingleton<IStateStore>(serviceKey, (sp, _) =>
            {
                return ActivatorUtilities.CreateInstance<DaprStateStoreProvider>(sp, options);
            });
        });

        module.RecordKeyedServiceKey(serviceKey);
        return module;
    }
}

public class ModuleDaprStateStore : MonicaModule<ModuleDaprStateStoreOption>,
      IStateStoreModuleProvider
{

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDaprClient>();
        module.Require<ModuleStateStore>();
    }

    #region IStateStoreModuleProvider Implementation

    public Type ProvidesFor => typeof(ModuleStateStore);

    public EStateStoreProviderType ProviderType => EStateStoreProviderType.Dapr;

    public EStateStoreCapabilities Capabilities =>
        EStateStoreCapabilities.RawStringRetrieval |
        EStateStoreCapabilities.QueryState |
        EStateStoreCapabilities.BulkOperations;

    public string DisplayName => "Dapr";

    #endregion
}



public class ModuleDaprStateStoreOption : ModuleOptions<ModuleDaprStateStore>
{
    /// <summary>
    /// Name of the Dapr state store. Must match the <c>name</c> field in the Dapr state store
    /// component YAML metadata.
    /// </summary>
    [Required]
    public string StateStoreName { get; set; } = null!;

    /// <summary>
    /// Number of concurrent get operations issued by the Dapr runtime to the state store. Values
    /// less than or equal to 0 remove the limit.
    /// </summary>
    public int? DefaultBulkParallelism { get; set; } 
}
