using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Registers the explicit Dapr binding integration for DataChannel pipelines.</summary>
public sealed class ModuleDataChannelDapr : MonicaModule<ModuleDataChannelDaprOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDataChannel, ModuleDataChannelOption>();
        module.Require<ModuleDaprClient, ModuleDaprClientOption>();
    }
}

/// <summary>
/// Identifies Dapr binding integration. Configure the host's Dapr SDK and sidecar health policy through
/// <see cref="ModuleDaprClientOption"/>; configure each component and route through binding endpoint options.
/// </summary>
public sealed class ModuleDataChannelDaprOption : ModuleOptions<ModuleDataChannelDapr>;

/// <summary>Provides explicit Dapr binding selection for a host's DataChannel registration.</summary>
public static class ModuleDataChannelDaprBuilderExtensions
{
    /// <summary>
    /// Includes the Dapr binding integration and its host-owned Dapr client and sidecar health monitoring.
    /// </summary>
    /// <param name="module">The DataChannel registration whose setup declares binding endpoints.</param>
    /// <returns>The same DataChannel registration for setup composition.</returns>
    /// <remarks>
    /// Requires the Monica.DataChannel.Dapr package and an ASP.NET Core host. This selection does not configure
    /// Dapr components or start a sidecar. The host must select at least one setup with <c>UseSetup&lt;TSetup&gt;()</c>.
    /// Call <c>AddDaprClient(...)</c> separately when the default SDK or health policy needs adjustment.
    /// </remarks>
    public static ModuleRegistration<ModuleDataChannel, ModuleDataChannelOption> UseDaprBindings(
        this ModuleRegistration<ModuleDataChannel, ModuleDataChannelOption> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Include<ModuleDataChannelDapr, ModuleDataChannelDaprOption>();
        return module;
    }
}
