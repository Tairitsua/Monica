using Microsoft.Extensions.Hosting;

namespace Monica.Core.Modularity.Abstractions;

/// <summary>Defines the single application authority that prepares inputs before declaring owned registrations.</summary>
/// <remarks>
/// Core finalizes this module's options once after the initial dependency descriptions. Preparation may project
/// application identity; Core then freezes identity and discovery before calling registration. Registrations may
/// add modules and services, but cannot alter the prepared authority or execute another composition pass.
/// At most one enabled module per host may implement this contract. Neither callback may resolve services.
/// </remarks>
public interface IApplicationCompositionModule
{
    /// <summary>Validates and prepares the application using its finalized options and final discovery selection.</summary>
    void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder);

    /// <summary>Declares owned registrations after application inputs are frozen and before the module graph is sealed.</summary>
    void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder);
}
