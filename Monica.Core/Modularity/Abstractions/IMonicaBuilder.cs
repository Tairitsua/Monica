using Monica.Core.TypeDiscovery.Models;

namespace Monica.Core.Modularity.Abstractions;

/// <summary>
/// Composes the Monica module graph for one host application.
/// </summary>
/// <remarks>
/// A builder instance is valid inside the callback passed to <c>AddMonica</c> and the framework's subsequent owned
/// registration pass. Identity and discovery freeze before that pass; module declarations remain open until it ends.
/// Module registration and configuration are then validated and applied to the owning host.
/// </remarks>
public interface IMonicaBuilder : IMonicaCompositionContext
{
    /// <summary>
    /// Configures application identity defaults shared by modules in this host.
    /// </summary>
    /// <param name="configure">The application configuration callback.</param>
    /// <returns>The current Monica builder.</returns>
    IMonicaBuilder ConfigureApplication(Action<MonicaApplicationOptions> configure);

    /// <summary>
    /// Configures module-system behavior shared by modules in this host.
    /// </summary>
    /// <param name="configure">The module-system configuration callback.</param>
    /// <returns>The current Monica builder.</returns>
    IMonicaBuilder ConfigureModuleSystem(Action<MonicaModuleSystemOptions> configure);

    /// <summary>
    /// Replaces the host's business-type discovery configuration.
    /// </summary>
    /// <param name="configure">An optional callback that customizes type discovery.</param>
    /// <returns>The current Monica builder.</returns>
    IMonicaBuilder ConfigureTypeDiscovery(Action<TypeFinderOptions>? configure = null);

    /// <summary>
    /// Registers one module in the current host-bound module graph.
    /// </summary>
    /// <typeparam name="TModule">The module implementation type.</typeparam>
    /// <typeparam name="TModuleOption">The module option type.</typeparam>
    /// <param name="configure">An optional callback that configures the module options.</param>
    /// <returns>A host-bound declaration for additional fluent configuration.</returns>
    ModuleRegistration<TModule, TModuleOption> AddModule<TModule, TModuleOption>(
        Action<TModuleOption>? configure = null)
        where TModuleOption : ModuleOptions<TModule>, new()
        where TModule : MonicaModule<TModuleOption>, new();
}
