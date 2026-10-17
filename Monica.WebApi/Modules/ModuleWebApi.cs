using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Bundles Monica's standard Web API infrastructure graph.
/// </summary>
public class ModuleWebApi : MonicaModule<ModuleWebApiOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleApplication, ModuleApplicationOption>();
        module.Require<ModuleAutoControllers, ModuleAutoControllersOption>();
        module.Require<ModuleAutoModel, ModuleAutoModelOption>();
        module.Require<ModuleDependencyInjection, ModuleDependencyInjectionOption>();
        module.Require<ModuleSwagger, ModuleSwaggerOption>();
        module.Require<ModuleAuthentication, ModuleAuthenticationOption>();
        module.Require<ModuleMediator, ModuleMediatorOption>();
        module.Require<ModuleObjectMapping, ModuleObjectMappingOption>();
        module.Require<ModuleRepository, ModuleRepositoryOption>();
    }
}

public static class ModuleWebApiBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers and configures the standard Web API infrastructure graph.
        /// </summary>
        public ModuleRegistration<ModuleWebApi, ModuleWebApiOption> AddWebApi(
            Action<ModuleWebApiOption>? configure = null)
        {
            return builder.AddModule<ModuleWebApi, ModuleWebApiOption>(configure);
        }
    }
}

/// <summary>
/// Configures the Web API infrastructure bundle.
/// </summary>
public class ModuleWebApiOption : ModuleOptions<ModuleWebApi>;
