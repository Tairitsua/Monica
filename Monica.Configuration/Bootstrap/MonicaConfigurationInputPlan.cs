using System.Collections.Immutable;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;
using Monica.Core.Configuration;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Modules;

namespace Monica.Configuration.Bootstrap;

/// <summary>Declares one managed store, its section convention, and ordered runtime JSON sources.</summary>
/// <remarks>
/// Creating a plan performs no I/O. Apply it through AddConfiguration after choosing deployment inputs with
/// GetStartupOptions&lt;T&gt;(); Managed values are available only through the runtime configuration boundary.
/// Captured store callbacks must remain deterministic and stable for the host lifetime.
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
}
