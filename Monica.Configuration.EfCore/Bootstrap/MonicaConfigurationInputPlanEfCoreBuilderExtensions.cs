using Microsoft.EntityFrameworkCore;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EfCore.DbContext;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;

namespace Monica.Configuration.Bootstrap;

/// <summary>
/// Provides EF Core Configuration input-plan declarations.
/// </summary>
public static class MonicaConfigurationInputPlanEfCoreBuilderExtensions
{
    /// <summary>
    /// Selects the EF Core store bundle for runtime managed configuration.
    /// </summary>
    /// <param name="builder">The input-plan declaration builder.</param>
    /// <param name="configureDbContext">Configures the shared database provider and connection.</param>
    /// <returns>The same declaration builder.</returns>
    /// <remarks>
    /// Capture provider and connection values from frozen Startup options. The callback configures runtime contexts
    /// and must be deterministic and side-effect-free for the host lifetime. The host must apply
    /// <see cref="ConfigurationDbContext"/> migrations before runtime store access; this declaration never creates or
    /// upgrades database tables and never constructs an isolated service provider.
    /// </remarks>
    public static MonicaConfigurationInputPlanBuilder UseDbConfigurationStore(
        this MonicaConfigurationInputPlanBuilder builder,
        Action<DbContextOptionsBuilder> configureDbContext)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        return builder.UseConfigurationStore(
            new DatabaseConfigurationStoreComposition(configureDbContext));
    }

    private sealed class DatabaseConfigurationStoreComposition(
        Action<DbContextOptionsBuilder> configureDbContext)
        : IMonicaConfigurationStoreComposition
    {
        public string Name => "EF Core";

        public void ConfigureRuntime(
            ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
        {
            module.Include<ModuleConfigurationEfCore, ModuleConfigurationEfCoreOption>()
                .UseDbContext((_, options) => configureDbContext(options));
        }
    }
}
