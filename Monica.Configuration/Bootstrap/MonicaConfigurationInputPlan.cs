using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Binding;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Modules;

namespace Monica.Configuration.Bootstrap;

/// <summary>
/// Defines an immutable Configuration input declaration that is projected consistently into bootstrap configuration,
/// startup effective-options loading, and runtime module composition.
/// </summary>
/// <remarks>
/// Custom store compositions and captured callbacks must project the same logical store in every phase. Built-in file
/// store declarations snapshot their scalar settings immediately.
/// </remarks>
public sealed class MonicaConfigurationInputPlan
{
    private readonly IMonicaConfigurationStoreComposition _storeComposition;
    private readonly ImmutableArray<ManagedJsonConfigurationSourceRegistration> _managedJsonSources;

    internal MonicaConfigurationInputPlan(
        IMonicaConfigurationStoreComposition storeComposition,
        ImmutableArray<ManagedJsonConfigurationSourceRegistration> managedJsonSources,
        ConfigurationSectionPathConvention sectionPathConvention)
    {
        _storeComposition = storeComposition;
        _managedJsonSources = managedJsonSources;
        SectionPathConvention = sectionPathConvention;
    }

    internal ConfigurationSectionPathConvention SectionPathConvention { get; }

    /// <summary>
    /// Creates an immutable input plan from one bounded declaration callback.
    /// </summary>
    /// <param name="configure">Declares exactly one store and any ordered managed JSON sources.</param>
    /// <returns>The immutable input plan.</returns>
    /// <remarks>
    /// The callback builder is sealed when this method returns. Retaining it and attempting later mutation throws.
    /// Plan creation performs no file, database, dependency-injection, or host mutation work.
    /// </remarks>
    public static MonicaConfigurationInputPlan Create(
        Action<MonicaConfigurationInputPlanBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new MonicaConfigurationInputPlanBuilder();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Builds bootstrap configuration from the host's current configuration followed by the plan's managed JSON sources.
    /// </summary>
    /// <param name="hostBuilder">The host builder that supplies the base configuration and content root.</param>
    /// <returns>A caller-owned configuration root.</returns>
    /// <remarks>
    /// Later managed JSON declarations have higher priority. Managed JSON files are not watched by this short-lived
    /// bootstrap root. The caller should dispose the returned root after bootstrap and effective-options loading complete.
    /// </remarks>
    public MonicaBootstrapConfiguration BuildBootstrapConfiguration(IHostApplicationBuilder hostBuilder)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        ShapeAwareJsonConfigurationExtensions.PreserveJsonShapes(hostBuilder.Configuration);

        var builder = CreateConfigurationBuilder(hostBuilder);
        builder.AddConfiguration(hostBuilder.Configuration);
        AddManagedJsonSources(builder);
        return new MonicaBootstrapConfiguration(
            this,
            hostBuilder.Configuration,
            hostBuilder.Environment.ContentRootPath,
            builder.Build());
    }

    /// <summary>
    /// Loads several effective options types as one synchronous point-in-time startup snapshot.
    /// </summary>
    /// <param name="bootstrapConfiguration">The bootstrap configuration created by this plan.</param>
    /// <param name="optionsTypes">The annotated options types to load.</param>
    /// <param name="configure">Optional startup diagnostics configuration.</param>
    /// <param name="logger">Optional startup diagnostic logger.</param>
    /// <returns>The loaded effective-options snapshot.</returns>
    /// <remarks>
    /// This operation never publishes definitions or writes effective values. Missing documents use transient in-memory
    /// seeds. The returned values may differ from later runtime configuration if the store changes or runtime activation
    /// persists a different seed. Topology-driving settings should declare
    /// <see cref="ConfigurationReloadBehavior.StaticAfterStartup"/> or
    /// <see cref="ConfigurationReloadBehavior.RequiresRestart"/> as appropriate. The isolated startup reader is disposed
    /// before returning. Use <see cref="LoadEffectiveOptionsSnapshotAsync"/> when the surrounding startup flow is asynchronous.
    /// </remarks>
    public MonicaEffectiveOptionsSnapshot LoadEffectiveOptionsSnapshot(
        MonicaBootstrapConfiguration bootstrapConfiguration,
        IReadOnlyCollection<Type> optionsTypes,
        Action<MonicaEffectiveOptionsSnapshotOptions>? configure = null,
        ILogger? logger = null)
    {
        ValidateSnapshotRequest(bootstrapConfiguration, optionsTypes);

        using var loader = CreateSnapshotLoader(
            bootstrapConfiguration,
            configure,
            logger);
        return loader.Load(optionsTypes);
    }

    /// <summary>
    /// Loads several effective options types as one asynchronous point-in-time startup snapshot.
    /// </summary>
    /// <param name="bootstrapConfiguration">The bootstrap configuration created by this plan.</param>
    /// <param name="optionsTypes">The annotated options types to load.</param>
    /// <param name="configure">Optional startup diagnostics configuration.</param>
    /// <param name="logger">Optional startup diagnostic logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded effective-options snapshot.</returns>
    /// <remarks>
    /// This operation never publishes definitions or writes effective values. Missing documents use transient in-memory
    /// seeds. The returned values may differ from later runtime configuration if the store changes or runtime activation
    /// persists a different seed. Topology-driving settings should declare
    /// <see cref="ConfigurationReloadBehavior.StaticAfterStartup"/> or
    /// <see cref="ConfigurationReloadBehavior.RequiresRestart"/> as appropriate. The isolated startup reader is
    /// asynchronously disposed on success, cancellation, or failure.
    /// </remarks>
    public async Task<MonicaEffectiveOptionsSnapshot> LoadEffectiveOptionsSnapshotAsync(
        MonicaBootstrapConfiguration bootstrapConfiguration,
        IReadOnlyCollection<Type> optionsTypes,
        Action<MonicaEffectiveOptionsSnapshotOptions>? configure = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshotRequest(bootstrapConfiguration, optionsTypes);

        await using var loader = CreateSnapshotLoader(
            bootstrapConfiguration,
            configure,
            logger);
        return await loader.LoadAsync(optionsTypes, cancellationToken);
    }

    internal void ConfigureRuntime(
        ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
    {
        ArgumentNullException.ThrowIfNull(module);

        _storeComposition.ConfigureRuntime(module);
        foreach (var source in _managedJsonSources)
        {
            module.ConfigureBuilder(context =>
            {
                context.HostApplicationBuilder.Configuration.AddShapeAwareJsonFile(
                    source.Path,
                    source.Optional,
                    source.ReloadOnChange);
                ManagedJsonConfigurationSourceRegistry.Add(
                    context.HostApplicationBuilder.Configuration,
                    source);
            }, ModuleRegistrationOrder.AfterModule);
        }
    }

    private MonicaEffectiveOptionsSnapshotLoader CreateSnapshotLoader(
        MonicaBootstrapConfiguration bootstrapConfiguration,
        Action<MonicaEffectiveOptionsSnapshotOptions>? configure,
        ILogger? logger)
    {
        var options = new MonicaEffectiveOptionsSnapshotOptions();
        configure?.Invoke(options);
        var reader = _storeComposition.CreateStartupReader()
            ?? throw new InvalidOperationException(
                $"Configuration store composition '{_storeComposition.Name}' returned no startup reader.");

        try
        {
            return new MonicaEffectiveOptionsSnapshotLoader(
                bootstrapConfiguration,
                SectionPathConvention,
                options,
                reader,
                _managedJsonSources,
                logger ?? NullLogger.Instance);
        }
        catch
        {
            DisposeReader(reader);
            throw;
        }
    }

    private static ConfigurationBuilder CreateConfigurationBuilder(IHostApplicationBuilder hostBuilder)
    {
        var builder = new ConfigurationBuilder();
        if (!string.IsNullOrWhiteSpace(hostBuilder.Environment.ContentRootPath))
        {
            builder.SetBasePath(hostBuilder.Environment.ContentRootPath);
        }

        return builder;
    }

    private void AddManagedJsonSources(IConfigurationBuilder builder)
    {
        foreach (var source in _managedJsonSources)
        {
            builder.AddShapeAwareJsonFile(source.Path, source.Optional, reloadOnChange: false);
        }
    }

    private void ValidateSnapshotRequest(
        MonicaBootstrapConfiguration bootstrapConfiguration,
        IReadOnlyCollection<Type> optionsTypes)
    {
        ArgumentNullException.ThrowIfNull(bootstrapConfiguration);
        ArgumentNullException.ThrowIfNull(optionsTypes);
        bootstrapConfiguration.EnsureOwnedBy(this);
        if (optionsTypes.Count == 0)
        {
            throw new ArgumentException("At least one effective options type is required.", nameof(optionsTypes));
        }
    }

    private static void DisposeReader(IConfigurationEffectiveValueReader reader)
    {
        if (reader is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        if (reader is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
