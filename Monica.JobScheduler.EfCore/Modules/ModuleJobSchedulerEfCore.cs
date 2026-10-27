using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.EfCore;
using Monica.Locker.EfCore;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Adds the relational definition and execution store to a JobScheduler registration.
/// </summary>
public static class ModuleJobSchedulerEfCoreBuilderExtensions
{
    /// <summary>
    /// Uses one EF Core database as the scheduler definition, policy, queue, lease, and history correctness boundary.
    /// </summary>
    /// <param name="module">The host-bound JobScheduler registration.</param>
    /// <param name="optionsAction">Configures the relational provider and connection.</param>
    /// <param name="moduleAction">Optionally configures the EF Core persistence module.</param>
    /// <returns>The original JobScheduler registration for continued composition.</returns>
    /// <remarks>
    /// The selected database must be shared by every scheduler and worker replica for the same scope.
    /// Production deployments should use PostgreSQL-compatible serializable transactions.
    /// </remarks>
    public static ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> UseEfCoreStore(
        this ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> module,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction,
        Action<ModuleJobSchedulerEfCoreOption>? moduleAction = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(optionsAction);

        module.Include<ModuleJobSchedulerEfCore, ModuleJobSchedulerEfCoreOption>(moduleAction)
            .UseDbContext(optionsAction);
        module.UseStore<EfCoreJobSchedulerStore>();
        return module;
    }
}

/// <summary>
/// Configures the relational DbContext owned by JobScheduler persistence.
/// </summary>
public static class ModuleJobSchedulerEfCoreRegistrationExtensions
{
    /// <summary>
    /// Configures the scheduler DbContext and its singleton-safe owned-scope factory.
    /// </summary>
    /// <param name="module">The EF Core persistence registration.</param>
    /// <param name="optionsAction">Configures the provider and connection.</param>
    /// <returns>The same persistence registration.</returns>
    public static ModuleRegistration<ModuleJobSchedulerEfCore, ModuleJobSchedulerEfCoreOption> UseDbContext(
        this ModuleRegistration<ModuleJobSchedulerEfCore, ModuleJobSchedulerEfCoreOption> module,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(optionsAction);

        module.Require<ModuleRepository, ModuleRepositoryOption>()
            .AddRepositoryDbContext<JobSchedulerDbContext>(optionsAction, DbContextProviderType.Default);
        module.ConfigureServices(context =>
        {
            context.Services.TryAddSingleton<EfCoreJobSchedulerStore>();
            context.Services.TryAddSingleton<IJobSchedulerStore>(static provider =>
                provider.GetRequiredService<EfCoreJobSchedulerStore>());
        });
        module.SatisfyFeature(nameof(UseDbContext));
        return module;
    }
}

/// <summary>
/// Provides durable relational persistence for the JobScheduler module.
/// </summary>
public sealed class ModuleJobSchedulerEfCore : MonicaModule<ModuleJobSchedulerEfCoreOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.RequireFeature(nameof(ModuleJobSchedulerEfCoreRegistrationExtensions.UseDbContext));
    }
}

/// <summary>
/// Configures relational scheduler persistence.
/// </summary>
public sealed class ModuleJobSchedulerEfCoreOption : ModuleOptions<ModuleJobSchedulerEfCore>
{
    /// <summary>Shared coordinator table mapping; choose one migration owner per database/schema.</summary>
    public LeaseTableOptions LeaseTable { get; } = new();
}
