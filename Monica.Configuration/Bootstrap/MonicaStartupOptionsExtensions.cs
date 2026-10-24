using Monica.Configuration.Services.Support;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Configuration.Bootstrap;

/// <summary>Reads validated deployment-owned options before dependency injection and managed-store activation.</summary>
public static class MonicaStartupOptionsExtensions
{
    /// <summary>Gets the host's single frozen instance from the source snapshot captured before the AddMonica callback.</summary>
    /// <remarks>
    /// The type must declare Startup ownership and an explicit section path. Binding and pure object validation
    /// fail immediately; named options and Configure/PostConfigure contributions are not supported. Treat the
    /// returned object as read-only. This call does not register or activate a managed configuration store.
    /// </remarks>
    public static TOptions GetStartupOptions<TOptions>(this IMonicaCompositionContext context) where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        return ConfigurationStartupOptionsReader.For(context.Composition).Get<TOptions>();
    }
}
