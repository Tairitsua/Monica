using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Stores.File;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;

namespace Monica.Configuration.Bootstrap;

/// <summary>
/// Provides built-in Configuration input-plan declarations.
/// </summary>
public static class MonicaConfigurationInputPlanBuilderExtensions
{
    /// <summary>
    /// Selects the file-backed store bundle for runtime managed configuration.
    /// </summary>
    /// <param name="builder">The input-plan declaration builder.</param>
    /// <param name="configure">Optional file-store configuration.</param>
    /// <returns>The same declaration builder.</returns>
    /// <remarks>
    /// Configuration is snapshotted immediately. Store instances remain lazy: plan creation and runtime application do
    /// not create directories or read files.
    /// </remarks>
    public static MonicaConfigurationInputPlanBuilder UseFileConfigurationStore(
        this MonicaConfigurationInputPlanBuilder builder,
        Action<ConfigurationFileStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ConfigurationFileStoreOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        return builder.UseConfigurationStore(
            new FileConfigurationStoreComposition(options.RootDirectory));
    }

    private sealed class FileConfigurationStoreComposition(string rootDirectory)
        : IMonicaConfigurationStoreComposition
    {
        public string Name => "File";

        public void ConfigureRuntime(
            ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
        {
            module.ConfigureServices(context =>
            {
                var services = context.Services;
                services.Replace(ServiceDescriptor.Singleton(
                    _ => CreateStore(rootDirectory)));
                services.Replace(ServiceDescriptor.Singleton<IConfigurationEffectiveValueStore>(provider =>
                    provider.GetRequiredService<FileConfigurationStore>()));
                services.Replace(ServiceDescriptor.Singleton<IConfigurationHistoryStore>(provider =>
                    provider.GetRequiredService<FileConfigurationStore>()));
                services.Replace(ServiceDescriptor.Singleton<IConfigurationMetadataStore>(provider =>
                    provider.GetRequiredService<FileConfigurationStore>()));
                services.Replace(ServiceDescriptor.Singleton<IConfigurationDefinitionMaintenanceStore>(provider =>
                    provider.GetRequiredService<FileConfigurationStore>()));
                services.Replace(ServiceDescriptor.Singleton<IConfigurationUnifiedVersionStore>(provider =>
                    provider.GetRequiredService<FileConfigurationStore>()));
            });
        }

        private static FileConfigurationStore CreateStore(string rootDirectory) =>
            new(Options.Create(new ConfigurationFileStoreOptions
            {
                RootDirectory = rootDirectory
            }));
    }
}
