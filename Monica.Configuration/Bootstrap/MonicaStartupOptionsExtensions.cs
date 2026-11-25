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
    /// returned object as read-only. Explicitly role-scoped definitions are available only after successful application
    /// preflight when required by at least one local role. A skipped or faulted definition has no typed value.
    /// </remarks>
    public static TOptions GetStartupOptions<TOptions>(this IMonicaCompositionContext context) where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        return ConfigurationStartupOptionsReader.For(context.Composition).Get<TOptions>();
    }
}
