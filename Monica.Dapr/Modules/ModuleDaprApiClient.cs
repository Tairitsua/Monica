using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Dapr.Services;
using Monica.Dapr.Providers;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Selects Dapr as the HTTP delivery provider for application API clients.</summary>
public static class ModuleDaprApiClientBuilderExtensions
{
    /// <summary>Selects Dapr HTTP invocation and structured Dapr error classification for API clients.</summary>
    /// <remarks>Registers the Dapr client and sidecar health monitoring. Configure the complete call deadline on ModuleApiClientOption.</remarks>
    public static ModuleRegistration<ModuleDaprApiClient, ModuleDaprApiClientOption> UseDaprProvider(
        this ModuleRegistration<ModuleApiClient, ModuleApiClientOption> module,
        Action<ModuleDaprApiClientOption>? action = null)
    {
        return module.Include<ModuleDaprApiClient, ModuleDaprApiClientOption>(action);
    }
}

/// <summary>Composes Dapr clients and response classification with the shared API client module.</summary>
public class ModuleDaprApiClient : MonicaModule<ModuleDaprApiClientOption>
{

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDaprClient, ModuleDaprClientOption>();
        module.Require<ModuleApiClient, ModuleApiClientOption>(options =>
        {
            options.UseHttpClientProvider<DaprApiClientProvider>();
            options.UseResponseClassifier<DaprRemoteResponseClassifier>("dapr");
        });
    }
}



/// <summary>Options for Dapr API client composition; call limits are configured on ModuleApiClientOption.</summary>
public class ModuleDaprApiClientOption : ModuleOptions<ModuleDaprApiClient>
{
}
