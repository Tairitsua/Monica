using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Services;

namespace Monica.Core.Modularity.Extensions;

/// <summary>
/// Creates a host-bound Monica module composition.
/// </summary>
public static class MonicaHostBuilderExtensions
{
    private static readonly object MONICA_APPLICATION_KEY = new();

    /// <summary>
    /// Composes, validates, and registers Monica modules for one host.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete host builder type.</typeparam>
    /// <param name="builder">The host application builder that owns the composition.</param>
    /// <param name="configure">A synchronous callback that configures application defaults and modules.</param>
    /// <returns>The same host builder instance.</returns>
    /// <remarks>
    /// Call this exactly once before <c>Build()</c>. The module graph is sealed after the callback and the subsequent
    /// framework-owned application registration pass, before this method returns;
    /// retained module registrations reject later mutation. This overload does not track application startup; pass a
    /// <see cref="MonicaStartup"/> to the startup-aware overload when that observation is required.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when Monica was already added to this host builder.</exception>
    public static TBuilder AddMonica<TBuilder>(
        this TBuilder builder,
        Action<IMonicaBuilder> configure)
        where TBuilder : IHostApplicationBuilder
    {
        return AddMonicaCore(builder, startup: null, configure);
    }

    /// <summary>
    /// Composes, validates, and registers Monica modules for one host while tracking application startup from an
    /// explicit application-owned marker.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete host builder type.</typeparam>
    /// <param name="builder">The host application builder that owns the composition.</param>
    /// <param name="startup">The single-use startup marker created near the beginning of the application entry point.</param>
    /// <param name="configure">A synchronous callback that configures application defaults and modules.</param>
    /// <returns>The same host builder instance.</returns>
    /// <remarks>
    /// The observed interval ends when the Generic Host publishes <c>ApplicationStarted</c>. Work continuing in
    /// unbounded background services after that signal is not part of application startup.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Monica was already added to this host builder or <paramref name="startup"/> was already assigned
    /// to another host.
    /// </exception>
    public static TBuilder AddMonica<TBuilder>(
        this TBuilder builder,
        MonicaStartup startup,
        Action<IMonicaBuilder> configure)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(startup);
        return AddMonicaCore(builder, startup, configure);
    }

    private static TBuilder AddMonicaCore<TBuilder>(
        TBuilder builder,
        MonicaStartup? startup,
        Action<IMonicaBuilder> configure)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder.Properties.ContainsKey(MONICA_APPLICATION_KEY))
        {
            throw new InvalidOperationException("AddMonica(...) can be called only once for a host builder.");
        }

        // Claim only after validating the builder so an unrelated duplicate-registration failure does not consume
        // an otherwise unused application marker.
        var startupOrigin = startup?.Claim();
        var application = new MonicaApplication(startupOrigin);
        application.Profiling.StartModuleSystem();
        var monicaBuilder = new MonicaBuilder(builder, application);
        builder.Properties.Add(MONICA_APPLICATION_KEY, application);

        try
        {
            application.InitializeComposition(builder);
            application.Profiling.StartStage(ModuleSystemStage.ApplicationConfiguration);
            try
            {
                configure(monicaBuilder);
            }
            finally
            {
                application.Profiling.StopStage(ModuleSystemStage.ApplicationConfiguration);
            }

            monicaBuilder.Complete();
            return builder;
        }
        catch
        {
            if (!application.Modules.HasMutatedHost && !application.CompositionHasMutatedHost)
            {
                builder.Properties.Remove(MONICA_APPLICATION_KEY);
            }

            application.Dispose();
            throw;
        }
    }
}
