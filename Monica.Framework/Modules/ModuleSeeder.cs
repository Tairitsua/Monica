using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Facades;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Models.Internal;
using Monica.Framework.Seeder.Services;
using Monica.Framework.Seeder.Services.Support;
using Monica.HealthCheck.Extensions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides the host-bound registration entry point for dependency-aware seeders.
/// </summary>
public static class ModuleSeederBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers discovered seeders, their background DAG scheduler, and readiness reporting.
        /// </summary>
        /// <remarks>
        /// Seeder execution begins only after the Generic Host publishes <c>ApplicationStarted</c>. Required
        /// seeders keep the <c>monica.seeder</c> readiness check unhealthy until they complete successfully.
        /// Application activation filters startup entries before dependency validation. A selected seeder cannot
        /// depend on an inactive seeder; place both prerequisites together or remove that startup dependency.
        /// </remarks>
        public ModuleRegistration<ModuleSeeder, ModuleSeederOption> AddSeeder(Action<ModuleSeederOption>? action = null)
        {
            return builder.AddModule<ModuleSeeder, ModuleSeederOption>(action);
        }
    }
}

/// <summary>
/// Discovers seeders, validates their dependency graph, and schedules them after application startup.
/// </summary>
public sealed class ModuleSeeder : MonicaModule<ModuleSeederOption>
{
    private const string HEALTH_CHECK_NAME = "monica.seeder";
    private SeederGraph _graph = SeederGraph.Empty;

    /// <inheritdoc />
    public override void ValidateOptions(ModuleSeederOption options, string? profileName)
    {
        SeederGraph.ValidateOptions(options);
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.Require<ModuleHealthCheck, ModuleHealthCheckOption>();
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleSeederOption> discovery)
    {
        discovery.Match(
            TypeQuery.ConcreteClass.AssignableTo<ISeeder>(),
            (context, matches) =>
            {
                var seederTypes = matches.Select(static match => match.Type)
                    .Where(type => Application.TypeActivation?.IsEnabled(type, TypeActivationKind.HostedService) != false)
                    .ToArray();
                _graph = SeederGraph.Create(seederTypes, Option);

                foreach (var descriptor in _graph.Nodes)
                {
                    context.Registrations.Add(ServiceDescriptor.Transient(
                        descriptor.SeederType,
                        descriptor.SeederType));
                }
            });
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleSeederOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_graph);
        services.AddSingleton<SeederState>();
        services.AddSingleton<ISeederState>(static provider => provider.GetRequiredService<SeederState>());
        services.AddSingleton<SeederFacade>();
        services.TryAddSingleton<ISeederRetryDelay, SeederRetryDelay>();
        services.AddSingleton<SeederScheduler>();
        services.AddHostedService<SeederBackgroundService>();
        services.AddHealthChecks().AddMonicaReadinessCheck<SeederHealthCheck>(
            HEALTH_CHECK_NAME,
            tags: ["seeder"]);
    }
}

/// <summary>
/// Configures dependency-aware background seeder execution for one Monica host.
/// </summary>
public sealed class ModuleSeederOption : ModuleOptions<ModuleSeeder>
{
    /// <summary>
    /// Gets or sets the maximum number of concurrently running seeders. The default uses the current process's
    /// processor count. Exclusive seeders always run alone regardless of this value.
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Gets or sets the execution mode used when a seeder policy inherits from the host. The default is
    /// <see cref="SeederExecutionMode.Concurrent"/>.
    /// </summary>
    public SeederExecutionMode DefaultExecutionMode { get; set; } = SeederExecutionMode.Concurrent;

    /// <summary>
    /// Gets or sets the readiness criticality used when a seeder policy inherits from the host. The default is
    /// <see cref="SeederCriticality.Required"/>.
    /// </summary>
    public SeederCriticality DefaultCriticality { get; set; } = SeederCriticality.Required;

    /// <summary>
    /// Gets or sets the behavior used after a seeder without an explicit override exhausts its attempts. The
    /// default is <see cref="SeederFailureBehavior.ContinueAndRecord"/>, which preserves independent work and
    /// retains diagnostics without aborting the run.
    /// </summary>
    public SeederFailureBehavior DefaultFailureBehavior { get; set; } = SeederFailureBehavior.ContinueAndRecord;

    /// <summary>
    /// Gets or sets the maximum attempt count inherited by seeders without an explicit policy. The default is one,
    /// which disables retries. Increase this only for idempotent seeders that can safely retry transient failures.
    /// </summary>
    public int DefaultMaxAttempts { get; set; } = 1;

    /// <summary>
    /// Gets or sets the initial retry delay for opted-in seeders. Each subsequent delay grows exponentially and
    /// includes bounded jitter. The default is 200 milliseconds.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets or sets the upper bound for an individual retry delay. The default is two seconds.
    /// </summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(2);
}
