using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;

namespace Monica.Testing.Hosting;

/// <summary>
/// Composes a complete, host-owned Monica application for each test scenario.
/// </summary>
/// <typeparam name="TDiscoveryAnchor">
/// A type from the assembly that contains the scenario's discoverable Monica building blocks.
/// </typeparam>
/// <remarks>
/// The factory is an immutable composition recipe and can be shared by test classes. Each call to
/// <see cref="CreateAsync"/> creates, starts, and returns a distinct host. Registration changes are applied
/// before that host is built; scopes created from the returned application never alter the service graph.
/// </remarks>
public abstract class MonicaTestApplicationFactory<TDiscoveryAnchor>
{
    /// <summary>
    /// Gets the production assemblies Monica should scan for this scenario.
    /// </summary>
    /// <remarks>
    /// Override this collection when one application scenario spans multiple production assemblies.
    /// </remarks>
    protected virtual IEnumerable<Assembly> TypeDiscoveryAssemblies => [typeof(TDiscoveryAnchor).Assembly];

    /// <summary>
    /// Configures host-level services before Monica composes its module graph.
    /// </summary>
    /// <param name="builder">The web application builder owned by this scenario.</param>
    protected virtual void ConfigureHost(WebApplicationBuilder builder)
    {
    }

    /// <summary>
    /// Configures the production Monica module composition exercised by the scenario.
    /// </summary>
    /// <param name="builder">The host-bound Monica builder.</param>
    protected abstract void ConfigureMonica(IMonicaBuilder builder);

    /// <summary>
    /// Configures deterministic providers and boundary seams after Monica module registration.
    /// </summary>
    /// <param name="services">The scenario's service collection.</param>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        services.AddMonicaTestSeams();
    }

    /// <summary>
    /// Creates and starts one complete Monica application for a test scenario.
    /// </summary>
    /// <param name="configureScenario">
    /// Optional final boundary replacements applied before the scenario host is built.
    /// </param>
    /// <param name="cancellationToken">Cancels host startup.</param>
    /// <returns>An application that owns the scenario host and every scope created from it.</returns>
    public async Task<MonicaTestApplication> CreateAsync(
        Action<ISeamReplacementBuilder>? configureScenario = null,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        ConfigureHost(builder);

        builder.AddMonica(monica =>
        {
            var discoveryAssemblies = TypeDiscoveryAssemblies
                .Distinct()
                .ToArray();
            if (discoveryAssemblies.Length == 0)
            {
                throw new InvalidOperationException("A Monica test application must declare at least one discovery assembly.");
            }

            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(discoveryAssemblies);
            });
            ConfigureMonica(monica);
        });

        ConfigureServices(builder.Services);
        configureScenario?.Invoke(new SeamReplacementBuilder(builder.Services));

        var application = builder.Build();
        try
        {
            await using (var setupScope = application.Services.CreateAsyncScope())
            {
                foreach (var initializer in setupScope.ServiceProvider.GetServices<ITestDatabaseInitializer>())
                    await initializer.InitializeAsync(cancellationToken);
            }
            application.UseMonica();
            application.MapMonica();
            await application.StartAsync(cancellationToken);
            return new MonicaTestApplication(application);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }
}
