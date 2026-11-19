using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EfCore.DbContext;
using Monica.Configuration.EfCore.Stores;
using Monica.Configuration.EfCore.Stores.Support;
using Monica.Configuration.Models;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Services;
using Monica.Repository;
using Monica.Repository.Entity.Abstractions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services.Support;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// EF Core module for Monica.Configuration.
/// </summary>
public sealed class ModuleConfigurationEfCore : MonicaModule<ModuleConfigurationEfCoreOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleConfiguration>();
        module.RequireFeature(nameof(ModuleConfigurationEfCoreRegistrationExtensions.UseDbContext));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleConfigurationEfCoreOption> context)
    {
        AddDatabaseConfigurationStores(context.Services);
    }

    internal static void AddDatabaseConfigurationStores(IServiceCollection services)
    {
        services.TryAddSingleton<ConfigurationDatabase>(serviceProvider =>
            new ConfigurationDatabase(
                serviceProvider.GetRequiredService<IDbContextOperation<ConfigurationDbContext>>()));
        services.TryAddSingleton<DatabaseConfigurationEffectiveValueStore>(serviceProvider =>
            new DatabaseConfigurationEffectiveValueStore(
                serviceProvider.GetRequiredService<ConfigurationDatabase>()));
        services.TryAddSingleton<DatabaseConfigurationHistoryStore>(serviceProvider =>
            new DatabaseConfigurationHistoryStore(
                serviceProvider.GetRequiredService<ConfigurationDatabase>()));
        services.TryAddSingleton<DatabaseConfigurationMetadataStore>(serviceProvider =>
            new DatabaseConfigurationMetadataStore(
                serviceProvider.GetRequiredService<ConfigurationDatabase>()));
        services.TryAddSingleton<DatabaseConfigurationUnifiedVersionStore>(serviceProvider =>
            new DatabaseConfigurationUnifiedVersionStore(
                serviceProvider.GetRequiredService<ConfigurationDatabase>()));
        services.TryAddSingleton<DatabaseConfigurationMutationBatchStore>(serviceProvider =>
            new DatabaseConfigurationMutationBatchStore(
                serviceProvider.GetRequiredService<ConfigurationDatabase>()));

        services.Replace(ServiceDescriptor.Singleton<IConfigurationEffectiveValueStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationEffectiveValueStore>()));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationHistoryStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationHistoryStore>()));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationMetadataStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationMetadataStore>()));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationDefinitionMaintenanceStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationMetadataStore>()));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationUnifiedVersionStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationUnifiedVersionStore>()));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationMutationBatchStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseConfigurationMutationBatchStore>()));
    }
}

/// <summary>
/// Registration extensions for EF Core configuration persistence.
/// </summary>
public static class ModuleConfigurationEfCoreRegistrationExtensions
{
    /// <summary>
    /// Registers the configuration DbContext.
    /// </summary>
    /// <remarks>
    /// The supplied options should select the host-owned migrations assembly and migrations-history table when they
    /// differ from the runtime assembly defaults. This module does not apply migrations at runtime.
    /// </remarks>
    /// <param name="module">The Configuration EF Core registration being configured.</param>
    /// <param name="optionsAction">DbContext configuration.</param>
    /// <returns>The EF Core configuration module registration.</returns>
    public static ModuleRegistration<ModuleConfigurationEfCore, ModuleConfigurationEfCoreOption> UseDbContext(this ModuleRegistration<ModuleConfigurationEfCore, ModuleConfigurationEfCoreOption> module, Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
    {
        module.Require<ModuleRepository, ModuleRepositoryOption>()
            .AddRepositoryDbContext<ConfigurationDbContext>(optionsAction, DbContextProviderType.Default);
        module.SatisfyFeature(nameof(UseDbContext));
        return module;
    }

}

/// <summary>
/// Module options for EF Core configuration persistence.
/// </summary>
/// <remarks>
/// The host owns the <see cref="ConfigurationDbContext"/> schema through EF Core migrations. This module never creates or
/// upgrades database schemas; store operations report missing schema objects with an actionable exception.
/// </remarks>
public sealed class ModuleConfigurationEfCoreOption : ModuleOptions<ModuleConfigurationEfCore>
{
}
