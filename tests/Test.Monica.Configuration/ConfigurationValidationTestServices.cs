using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Configuration.Models;

namespace Test.Monica.Configuration;

/// <summary>Composes pure core seams; only explicitly scanned local types receive executable authority.</summary>
internal static class ConfigurationValidationTestServices
{
    internal static ConfigurationLocalDefinitionRegistry CreateLocalRegistry(params Type[] types)
    {
        var scanner = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher());
        var registry = new ConfigurationLocalDefinitionRegistry();
        registry.RegisterRange(types.Select(type => new ConfigurationDefinitionRegistration(type, scanner.Scan(type))));
        return registry;
    }

    internal static ConfigurationValidationCoordinator CreateCoordinator(params Type[] types)
    {
        var registry = CreateLocalRegistry(types);
        return new ConfigurationValidationCoordinator(new ConfigurationValueValidationEngine(), registry,
            new ConfigurationObjectMaterializer(registry), new ConfigurationObjectValidationWalker());
    }

    internal static ConfigurationEffectiveValueSeedFactory CreateSeedFactory(ConfigurationRuntimeContext context, params Type[] types)
    {
        var registry = CreateLocalRegistry(types);
        return new ConfigurationEffectiveValueSeedFactory(context, registry, new ConfigurationObjectMaterializer(registry));
    }

    internal static void AddCoreServices(IServiceCollection services)
    {
        var registry = CreateLocalRegistry();
        services.AddLogging();
        services.AddSingleton(registry);
        services.AddSingleton<ConfigurationObjectMaterializer>();
        services.AddSingleton<ConfigurationObjectValidationWalker>();
        services.AddSingleton<ConfigurationValueValidationEngine>();
        services.AddSingleton<ConfigurationValidationCoordinator>();
        services.AddSingleton(new ConfigurationValidationPolicy(ConfigurationRuntimeValidationBehavior.DiagnosticOnly));
        services.AddSingleton(provider => new ConfigurationEffectiveValueSeedFactory(
            provider.GetRequiredService<ConfigurationRuntimeContext>(), registry,
            provider.GetRequiredService<ConfigurationObjectMaterializer>()));
    }
}
