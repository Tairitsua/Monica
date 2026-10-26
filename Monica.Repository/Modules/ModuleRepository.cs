using Monica.EventBus.Abstractions;
using Monica.EventBus;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.Repository.Inbox.Services;
using Monica.Core.Execution;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.Repository;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Entity.Services;
using Monica.Repository.Facades;
using Monica.Repository.GuidGeneration.Abstractions;
using Monica.Repository.GuidGeneration.Models;
using Monica.Repository.GuidGeneration.Services;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Metrics;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Persistence.Services.Support;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleRepositoryBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the Repository module
        /// </summary>
        public ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddRepository(Action<ModuleRepositoryOption>? action = null)
        {
            return builder.AddModule<ModuleRepository, ModuleRepositoryOption>(action);
        }
    }
}

public class ModuleRepository : MonicaModule<ModuleRepositoryOption>
{
    public override void ConfigureServices(ModuleContext<ModuleRepositoryOption> context)
    {
        var services = context.Services;
        services.AddOptions<SequentialGuidGeneratorOptions>();
        services.TryAddTransient<IGuidGenerator, SequentialGuidGenerator>();
        services.TryAddSingleton<IRepositoryDbContextRegistry, RepositoryDbContextRegistry>();
        services.TryAddScoped<IRepositoryDbContextDiagnosticsService, RepositoryDbContextDiagnosticsService>();
        services.TryAddScoped<RepositoryDiagnosticsFacade>();
        services.TryAddScoped<EntityEventPublicationScope>();
        services.AddScoped<IEntityEventPublishSwitch>(
            serviceProvider => serviceProvider.GetRequiredService<EntityEventPublicationScope>());

        if (Option.EnableEfCoreConnectionMetrics)
        {
            services.TryAddSingleton<EfCoreConnectionMetrics>();
            services.TryAddSingleton<EfCoreConnectionMetricsInterceptor>();
        }
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleObjectMapping, ModuleObjectMappingOption>();
        module.Require<ModuleClock, ModuleClockOption>();
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(pipeline =>
            pipeline.AddBehavior(typeof(InboxExecutionBehavior<,>), ExecutionBehaviorOrder.UnitOfWork + 100,
                static descriptor => (descriptor.Point == EventBusExecutionPoints.LocalHandler
                    || descriptor.Point == EventBusExecutionPoints.DistributedHandler)
                    && (Attribute.IsDefined(descriptor.ComponentType, typeof(InboxAttribute), inherit: true)
                        || descriptor.EntryMethod?.IsDefined(typeof(InboxAttribute), inherit: true) == true)));
    }
}

public static class ModuleRepositoryRegistrationExtensions
{
    /// <summary>
    /// Registers a repository DbContext and the scoped access services used by repositories and long-lived workers.
    /// </summary>
    /// <param name="module">The Repository module registration being configured.</param>
    /// <typeparam name="TDbContext">The repository DbContext type to register.</typeparam>
    /// <param name="optionsAction">Configures the EF Core provider and options for the DbContext.</param>
    /// <param name="dbContextProviderType">
    /// Selects transaction participation. UnitOfWork is the default; Default is for independently managed stores.
    /// Both modes resolve the same directly registered scoped context.
    /// </param>
    /// <returns>The repository module registration for method chaining.</returns>
    /// <remarks>
    /// Registration also exposes an <see cref="IDbContextFactory{TContext}"/> whose contexts own independent
    /// dependency injection scopes. Factory-created contexts must be disposed by the caller and are safe to create
    /// from long-lived services. This host-owned factory replaces any earlier factory registration for the same
    /// context so the ownership guarantee cannot be bypassed accidentally.
    /// </remarks>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddRepositoryDbContext<TDbContext>(this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module, Action<IServiceProvider, DbContextOptionsBuilder> optionsAction, DbContextProviderType dbContextProviderType = DbContextProviderType.UnitOfWork)
        where TDbContext : RepositoryDbContext<TDbContext>
    {
        if (dbContextProviderType == DbContextProviderType.UnitOfWork)
        {
            module.Require<ModuleUnitOfWork, ModuleUnitOfWorkOption>();
        }
        else if (dbContextProviderType != DbContextProviderType.Default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dbContextProviderType),
                dbContextProviderType,
                "Unknown repository DbContext provider type.");
        }

        module.ConfigureServices(context =>
        {
            context.Services.AddScoped<IDbContextProvider<TDbContext>, DefaultDbContextProvider<TDbContext>>();
            
            context.Services.TryAddTransient<IAuditPropertySetter, AuditPropertySetter>();
            context.Services.TryAddSingleton(TimeProvider.System);
            context.Services.AddSingleton(new RepositoryDbContextRegistration(
                typeof(TDbContext),
                dbContextProviderType));
            context.Services.TryAddSingleton(
                typeof(IDbContextOperation<TDbContext>),
                typeof(ScopedDbContextOperation<TDbContext>));
            context.Services.AddDbContext<TDbContext>(
                (serviceProvider, builder) => ConfigureDbContextOptions(context.Options, serviceProvider, builder, optionsAction));
            context.Services.RemoveAll<IDbContextFactory<TDbContext>>();
            context.Services.AddSingleton<IDbContextFactory<TDbContext>, OwnedScopeDbContextFactory<TDbContext>>();

            //TODO Use Module to optimize automatic registration
            var options = new EfRepositoryRegistrationOptions(typeof(TDbContext), context.Services);

            context.Services.AddTransient(serviceProvider =>
            {
                var builder = new DbContextOptionsBuilder<TDbContext>()
                    .UseLoggerFactory(serviceProvider.GetRequiredService<ILoggerFactory>())
                    .UseApplicationServiceProvider(serviceProvider);
                ConfigureDbContextOptions(context.Options, serviceProvider, builder, optionsAction);
                return builder.Options;
            });

            new EfCoreRepositoryRegistrar(options).AddRepositories();

            context.Services
                .AddTransient<IDbContextDatabaseManager<TDbContext>, DbContextDatabaseManager<TDbContext>>();
        });
        return module;
    }

    /// <summary>
    /// Adds transactional event storage to the selected write context and starts Monica's delivery worker.
    /// Generate an EF migration before deployment. Event names, payload serialization and transport routing
    /// come from the configured EventBus; the primary context in an operation must enable this capability.
    /// </summary>
    /// <remarks>
    /// Register each context once. Its declared domain or role owns delivery; a registration callback cannot
    /// transfer that ownership. Manual or disabled activation holds its worker contribution while retaining
    /// transactional staging and passive dispatch services.
    /// </remarks>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddOutbox<TDbContext>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module,
        Action<RepositoryOutboxOptions>? configure = null)
        where TDbContext : RepositoryDbContext<TDbContext>
    {
        var options = new RepositoryOutboxOptions();
        configure?.Invoke(options);
        if (options.DeliveryLease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Delivery lease must be positive.");
        if (options.PollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Poll interval must be positive.");
        if (options.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(configure), "Batch size must be positive.");
        if (options.CleanupBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(configure), "Cleanup batch size must be positive.");
        if (options.CleanupInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Cleanup interval must be positive.");
        if (options.CleanupTimeBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Cleanup time budget must be positive.");
        if (options.StatusInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Status interval must be positive.");
        if (options.RetryBaseDelay <= TimeSpan.Zero || options.RetryMaxDelay < options.RetryBaseDelay)
            throw new ArgumentOutOfRangeException(nameof(configure), "Retry delays must be positive and ordered.");
        if (options.DeliveredRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "Delivered retention must be positive.");
        module.Require<ModuleUnitOfWork, ModuleUnitOfWorkOption>();
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.ConfigureServices(context =>
        {
            if (context.Services.Any(descriptor => descriptor.ServiceType == typeof(OutboxRegistration<TDbContext>)))
                throw new InvalidOperationException($"Outbox context '{typeof(TDbContext).FullName}' must be registered once with one declaring owner.");
            context.Services.AddSingleton(new OutboxRegistration<TDbContext>(options));
            context.Services.TryAddScoped<ITransactionalEventSink, TransactionalEventSink>();
            context.Services.AddSingleton<OutboxDispatcher<TDbContext>>();
            var mode = options.EnableWorker
                ? context.Application.TypeActivation!.GetActivation(typeof(TDbContext), TypeActivationKind.Outbox)
                : ActivationMode.Disabled;
            context.Application.RecordActivation(new ResourceActivation
            {
                Key = $"Outbox:{typeof(TDbContext).FullName}",
                ImplementationType = typeof(TDbContext),
                RegistrationOwner = context.RegistrationOwner,
                Kind = TypeActivationKind.Outbox,
                Mode = mode,
                Reason = mode == ActivationMode.Automatic ? "Automatic dispatch permitted for this context."
                    : "Background dispatch is held; transactional staging remains available."
            });
            if (mode == ActivationMode.Automatic)
            {
                context.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<OutboxDispatcher<TDbContext>>());
                context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxWorker>());
                context.Composition.AdmitHostedLifecycle(
                    context.Services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(OutboxWorker)), typeof(OutboxWorker));
            }
        });
        return module;
    }

    /// <summary>
    /// Registers side-effect-free entity-to-event projections for the context. Projections run after generated
    /// values exist and stage ordinary Outbox-marked events on the operation's primary transaction context.
    /// Configure <see cref="AddOutbox{TDbContext}"/> on that primary context as well.
    /// </summary>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddEntityEventProjections<TDbContext>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module,
        Action<RepositoryEntityEventOptions> configure)
        where TDbContext : RepositoryDbContext<TDbContext>
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RepositoryEntityEventOptions();
        configure(options);
        module.ConfigureServices(context => context.Services.AddSingleton(new RepositoryEntityEventOptionsRegistration<TDbContext>(options)));
        return module;
    }

    /// <summary>
    /// Adds durable Inbox receipts for EventBus handlers marked with <see cref="InboxAttribute"/>.
    /// Their receipt, business changes and outgoing events commit in the selected primary context's transaction.
    /// Generate an EF migration for the MonicaInbox table before deployment.
    /// </summary>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddInbox<TDbContext>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module,
        Action<RepositoryInboxOptions>? configure = null)
        where TDbContext : RepositoryDbContext<TDbContext>
    {
        var options = new RepositoryInboxOptions();
        configure?.Invoke(options);
        module.Require<ModuleUnitOfWork, ModuleUnitOfWorkOption>();
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton(new InboxRegistration<TDbContext>(options));
            context.Services.TryAddSingleton(TimeProvider.System);
        });
        return module;
    }

    private static void ConfigureDbContextOptions(
        ModuleRepositoryOption option,
        IServiceProvider serviceProvider,
        DbContextOptionsBuilder builder,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
    {
        optionsAction(serviceProvider, builder);

        if (option.EnableEfCoreConnectionMetrics)
        {
            builder.AddInterceptors(serviceProvider.GetRequiredService<EfCoreConnectionMetricsInterceptor>());
        }
    }

}

public class ModuleRepositoryOption : ModuleOptions<ModuleRepository>
{
    /// <summary>
    /// Use User-defined function mapping to filter data.
    /// https://learn.microsoft.com/en-us/ef/core/querying/user-defined-function-mapping
    /// </summary>
    public bool UseDbFunction { get; set; }

    /// <summary>
    /// Gets or sets whether EF Core includes parameter values in diagnostic output.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="null"/>, which enables sensitive data logging only when the current host's
    /// <see cref="Microsoft.Extensions.Hosting.IHostEnvironment"/> is Development. Set an explicit value when
    /// repository diagnostics must not depend on the host environment.
    /// </remarks>
    public bool? EnableSensitiveDataLogging { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Repository should emit EF Core connection lifecycle metrics.
    /// </summary>
    /// <remarks>
    /// Enable this when a host wants to collect Repository persistence metrics through <c>System.Diagnostics.Metrics</c>
    /// and an OpenTelemetry-compatible exporter. Monica emits the metrics but does not configure any exporter.
    /// </remarks>
    public bool EnableEfCoreConnectionMetrics { get; set; }

    /// <summary>
    /// Disable the entity <see cref="IHasEntitySelfConfig{TEntity}"/> function, which can be turned off when not in use
    /// </summary>
    public bool DisableEntitySelfConfiguration { get; set; }

    /// <summary>
    /// Disables automatic discovery of entity-specific configuration via <see cref="IEntityTypeConfiguration{TEntity}"/>.
    /// You can disable this when automatic registration from <see cref="RepositoryDbContext{TDbContext}"/> is not needed.
    /// </summary>
    public bool DisableEntitySeparateConfiguration { get; set; }

    /// <summary>
    /// Maximum length used for concurrency-stamp columns configured by Repository conventions.
    /// </summary>
    public const int ConcurrencyStampMaxLength = 40;
}

/// <summary>
/// Selects transaction participation; both modes use the directly registered scoped DbContext.
/// </summary>
public enum DbContextProviderType
{
    /// <summary>
    /// Excludes this context from automatic selection. An independent operation can select it explicitly
    /// through UnitOfWorkScopeOptions.DbContextTypes, or own a direct save.
    /// </summary>
    Default,

    /// <summary>
    /// Uses the current scope's DbContext and enlists it in the operation's transaction.
    /// </summary>
    UnitOfWork
}
