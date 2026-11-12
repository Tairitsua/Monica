using Monica.Core.Modularity.Abstractions;
using Monica.Modules;

namespace Monica.Configuration.Abstractions;

/// <summary>Describes one managed configuration store for host-owned runtime composition.</summary>
/// <remarks>
/// Composition records registrations without opening or initializing the store. Captured inputs and topology must
/// remain stable after plan creation. Provider and connection inputs belong to frozen Startup definitions, not to
/// values read from the managed store being configured.
/// </remarks>
public interface IMonicaConfigurationStoreComposition
{
    /// <summary>Gets a stable, developer-facing name used in composition diagnostics.</summary>
    string Name { get; }

    /// <summary>Applies this store to the runtime Configuration module registration without performing store I/O.</summary>
    /// <param name="module">The host-bound Configuration module registration.</param>
    void ConfigureRuntime(ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module);
}
