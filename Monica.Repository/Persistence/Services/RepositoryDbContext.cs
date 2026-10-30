using Monica.Locker.EfCore;
using Monica.Core.Execution;
using Monica.EntityFrameworkCore.Extensions;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Entity.Extensions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Extensions;
using Monica.Repository.Persistence.Services.Support;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Services;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Inbox.Models;
using Monica.Repository.Inbox.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Monica.Tool.Extensions;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Services;

namespace Monica.Repository.Persistence.Services;

/// <summary>
/// Base DbContext for Monica repositories. Persistence concepts — audit stamping, soft-delete rewriting,
/// concurrency stamps, and optional durable notification capture — are intrinsic to
/// <see cref="SaveChangesAsync(bool, CancellationToken)"/>. Storage policies apply on every save path;
/// durable notification capture requires a managed UnitOfWork transaction. External delivery runs after commit.
/// </summary>
public abstract class RepositoryDbContext<TDbContext>(DbContextOptions<TDbContext> options, ICachedServiceProvider serviceProvider)
    : DbContext(options), IRepositoryModelFeatures, IRepositoryContextLifetime, IOutboxStoreContext, IInboxStoreContext
    where TDbContext : DbContext
{
    private IServiceScope? _factoryScope;

    public ICachedServiceProvider CachedServiceProvider { get; } = serviceProvider;

    protected IAuditPropertySetter AuditPropertySetter => CachedServiceProvider.GetRequiredService<IAuditPropertySetter>();

    protected ILogger<RepositoryDbContext<TDbContext>> Logger => CachedServiceProvider.GetService<ILogger<RepositoryDbContext<TDbContext>>>() ?? NullLogger<RepositoryDbContext<TDbContext>>.Instance;

    protected ModuleRepositoryOption Options => CachedServiceProvider.GetRequiredService<IOptions<ModuleRepositoryOption>>().Value;

    private bool _saveFailed;
    private bool _saving;
    private readonly List<OutboxMessage> _pendingMessages = [];
    private RepositoryOutboxOptions? OutboxOptions => CachedServiceProvider.GetService<OutboxRegistration<TDbContext>>()?.Options;
    private RepositoryEntityEventOptions? EntityEventOptions => CachedServiceProvider.GetService<RepositoryEntityEventOptionsRegistration<TDbContext>>()?.Options;
    bool IRepositoryModelFeatures.HasOutbox => OutboxOptions is not null;
    bool IRepositoryModelFeatures.HasInbox => CachedServiceProvider.GetService<InboxRegistration<TDbContext>>() is not null;
    bool IRepositoryModelFeatures.ExcludeOutboxFromMigrations => OutboxOptions?.ExcludeFromMigrations == true;
    bool IRepositoryModelFeatures.ExcludeInboxFromMigrations => CachedServiceProvider.GetService<InboxRegistration<TDbContext>>()?.Options.ExcludeFromMigrations == true;
    bool IRepositoryModelFeatures.ExcludeLeasesFromMigrations => OutboxOptions?.LeaseTable.ExcludeFromMigrations == true;
    bool IRepositoryModelFeatures.HasSnapshots => CachedServiceProvider.GetService<SnapshotTableRegistration<TDbContext>>() is not null;
    bool IRepositoryModelFeatures.ExcludeSnapshotsFromMigrations => CachedServiceProvider.GetService<SnapshotTableRegistration<TDbContext>>()?.ExcludeFromMigrations == true;
    bool IOutboxStoreContext.HasOutbox => OutboxOptions is not null;
    bool IOutboxStoreContext.HasPendingOutboxMessages => _pendingMessages.Count != 0;
    bool IInboxStoreContext.HasInbox => CachedServiceProvider.GetService<InboxRegistration<TDbContext>>() is not null;
    object IRepositoryModelFeatures.ModelCacheKey => ModelCacheKey;

    /// <summary>
    /// Identifies this context's model variant. Override for dynamic mappings such as table shards,
    /// including <c>base.ModelCacheKey</c> and every value that changes the model in the returned key.
    /// Monica adds message-store participation, migration exclusions, and design-time mode automatically.
    /// UseMonicaTableLayout isolates EF's internal model cache by the immutable provider/layout identity.
    /// The getter runs before model initialization and must not access <see cref="DbContext.Model"/>.
    /// </summary>
    protected virtual object ModelCacheKey => GetType();

    /// <summary>
    /// Transfers ownership of the factory-created dependency injection scope to this context.
    /// </summary>
    /// <remarks>
    /// Contexts resolved normally from an application scope never use this path. Factory-created contexts dispose
    /// the attached scope together with the context so scoped dependencies cannot escape their operation boundary.
    /// </remarks>
    internal void OwnFactoryScope(IServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (Interlocked.CompareExchange(ref _factoryScope, scope, null) is not null)
        {
            throw new InvalidOperationException("A repository DbContext can own only one factory scope.");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        var scope = Interlocked.Exchange(ref _factoryScope, null);
        try
        {
            base.Dispose();
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        var scope = Interlocked.Exchange(ref _factoryScope, null);
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                scope?.Dispose();
            }
        }
    }


    protected readonly DbContextOptions DbContextOptions = options;
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ConfigureRepositoryModel(optionsBuilder);
        optionsBuilder.AddInterceptors(RepositoryOperationInterceptor.Instance);
        var enableSensitiveDataLogging = Options.EnableSensitiveDataLogging
            ?? CachedServiceProvider.GetService<IHostEnvironment>()?.IsDevelopment()
            ?? false;

        if (enableSensitiveDataLogging)
        {
            // EF Core must receive this setting during OnConfiguring for parameter values to be included in diagnostics.
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }

    /// <summary>Installs repository model variants without changing interception or sensitive logging.</summary>
    /// <remarks>Contexts with their own OnConfiguring policy call this method to preserve repository model identity.</remarks>
    protected void ConfigureRepositoryModel(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, RepositoryModelCacheKeyFactory>();
    }

    #region Model conventions

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HavePrecision(0);
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp");
        configurationBuilder.Properties<TimeOnly>().HavePrecision(0);
        configurationBuilder.Properties<TimeSpan>().HavePrecision(0);
        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Extend DbContext default field settings
    /// </summary>
    /// <param name="builder"></param>
    protected virtual void OnModelCreatingExtend(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        //set all string property default value to ""
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                //Set up automatic generation of SnowflakeIdGenerator ID
                if (property.Name.Equals("Id") && property.ValueGenerated != ValueGenerated.Never &&
                    property.ClrType == typeof(long))
                {
                    property.SetValueGeneratorFactory((_, _) => new SnowflakeLongValueGenerator());
                    builder.Entity(entityType.ClrType).Property(property.Name).ValueGeneratedNever();
                }

                if (property.ClrType == typeof(string) && !IsKeyProperty(entityType, property))
                {
                    property.SetDefaultValue("");
                }

                //pgsql will automatically fill in spaces for char type
                if (property.GetColumnType() == "char")
                {
                    property.SetValueConverter(new CharTrimEndValueConverter());
                }

                //Enum conversion database storage as string
                if (property.ClrType.BaseType == typeof(Enum))
                {
                    var columnType = property.GetColumnType();
                    if (columnType == "varchar")
                    {
                        var type = typeof(EnumToStringConverter<>).MakeGenericType(property.ClrType);
                        var converter = Activator.CreateInstance(type, new ConverterMappingHints()) as ValueConverter;
                        property.SetValueConverter(converter);
                    }
                }
            }
        }
    }

    private static bool IsKeyProperty(IMutableEntityType entityType, IMutableProperty property)
    {
        return entityType.GetKeys().Any(key => key.Properties.Contains(property));
    }


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            _configureBasePropertiesMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [builder, entityType]);

            _configureValueConverterMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [builder, entityType]);
        }

        builder.ApplyEntitySelfConfigurations(Options, Logger);

        builder.ApplyEntitySeparateConfigurations(Options, Logger);

        OnModelCreatingExtend(builder);
        if (CachedServiceProvider.GetService<SnapshotTableRegistration<TDbContext>>() is { } snapshotTable)
        {
            var versions = builder.Entity<RepositorySnapshotVersion>();
            versions.ToMonicaInfrastructureTable("MonicaSnapshotVersions", table =>
            {
                if (snapshotTable.ExcludeFromMigrations) table.ExcludeFromMigrations();
            });
            versions.HasKey(x => new { x.SourceId, x.DatasetKey });
            versions.Property(x => x.SourceId).HasMaxLength(200).IsRequired();
            versions.Property(x => x.DatasetKey).HasMaxLength(200).IsRequired();
        }
        if (OutboxOptions is not null)
        {
            builder.ConfigureMonicaLeases(OutboxOptions.LeaseTable);
            var outbox = builder.Entity<OutboxMessage>();
            outbox.ToMonicaInfrastructureTable("MonicaOutbox", table =>
            {
                if (OutboxOptions.ExcludeFromMigrations) table.ExcludeFromMigrations();
            });
            outbox.HasKey(x => x.Sequence);
            outbox.Property(x => x.Sequence).ValueGeneratedOnAdd();
            outbox.HasIndex(x => x.MessageId).IsUnique();
            outbox.HasIndex(x => new { x.DeliveredAtUtc, x.NextAttemptAtUtc, x.LeaseUntilUtc, x.Sequence });
            outbox.HasIndex(x => new { x.DeliveryTarget, x.DeliveredAtUtc, x.NextAttemptAtUtc, x.LeaseUntilUtc, x.Sequence });
            outbox.Property(x => x.MessageId).HasMaxLength(200).IsRequired();
            outbox.Property(x => x.Source).HasMaxLength(200).IsRequired();
            outbox.Property(x => x.EventName).HasMaxLength(200).IsRequired();
            outbox.Property(x => x.TopicName).HasMaxLength(200).IsRequired();
            outbox.Property(x => x.ServiceKey).HasMaxLength(200);
            outbox.Property(x => x.DeliveryTarget).HasMaxLength(66);
            outbox.Property(x => x.TransportKey).HasMaxLength(500);
            outbox.Property(x => x.Body).IsRequired();
            outbox.Property(x => x.LastError).HasMaxLength(1000);
            outbox.Property(x => x.CreatedAtUtc).HasConversion<UtcTimestampValueConverter>();
            outbox.Property(x => x.DeliveredAtUtc).HasConversion<UtcTimestampValueConverter>();
            outbox.Property(x => x.LeaseUntilUtc).HasConversion<UtcTimestampValueConverter>();
            outbox.Property(x => x.NextAttemptAtUtc).HasConversion<UtcTimestampValueConverter>();
        }
        var inboxRegistration = CachedServiceProvider.GetService<InboxRegistration<TDbContext>>();
        if (inboxRegistration is not null)
        {
            var inbox = builder.Entity<InboxReceipt>();
            inbox.ToMonicaInfrastructureTable("MonicaInbox", table =>
            {
                if (inboxRegistration.Options.ExcludeFromMigrations) table.ExcludeFromMigrations();
            });
            inbox.HasKey(x => new { x.Consumer, x.Source, x.MessageId });
            inbox.Property(x => x.Consumer).HasMaxLength(200).IsRequired();
            inbox.Property(x => x.Source).HasMaxLength(200).IsRequired();
            inbox.Property(x => x.MessageId).HasMaxLength(200).IsRequired();
            inbox.Property(x => x.ReceivedAtUtc).HasConversion<UtcTimestampValueConverter>();
        }
    }


    #endregion


    /// <summary>
    /// Saves through fixed persistence policies. Outbox snapshots and business rows share the managed UnitOfWork transaction.
    /// No transport is invoked. A failed save faults this context; retry in a fresh scope.
    /// </summary>
    /// <remarks>
    /// SaveChanges(false) is deliberately unsupported: accepting business changes between two internal flushes
    /// is necessary for generated-key outbox capture. Use a transaction and a fresh context when retrying.
    /// Managed saves omit automatic savepoints because any failure requires the whole operation to roll back.
    /// Other saves retain EF's transaction and savepoint behavior. This method never commits a caller-owned transaction
    /// or starts an additional transaction merely because Outbox is configured.
    /// </remarks>
    public sealed override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => SaveCoreAsync(true, acceptAllChangesOnSuccess, cancellationToken);

    /// <summary>Synchronous saves apply identical policies and durable capture using synchronous database I/O.</summary>
    public sealed override int SaveChanges(bool acceptAllChangesOnSuccess)
        => SaveCoreAsync(false, acceptAllChangesOnSuccess, CancellationToken.None).GetAwaiter().GetResult();

    void IOutboxStoreContext.StagePreparedMessage(EventMessage message)
    {
        if (_saveFailed) throw new InvalidOperationException("This context's save failed. Retry in a fresh operation scope.");
        if (OutboxOptions is null) throw new InvalidOperationException("The selected transaction owner must enable AddOutbox.");
        var captured = OutboxMessage.Capture(message, CachedServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System);
        if (OutboxOptions.SharedStoreKey is not null)
            captured.DeliveryTarget = CachedServiceProvider.GetRequiredService<OutboxDeliveryCapabilities>().Capture(message);
        _pendingMessages.Add(captured);
    }

    private bool ValidateSave()
    {
        CachedServiceProvider.GetService<UnitOfWorkManager>()?.ThrowIfRollbackOnly("access the database");
        if (_saveFailed) throw new InvalidOperationException("This context's save failed. Dispose it and retry in a fresh scope.");
        if (_saving) throw new InvalidOperationException("Recursive or concurrent saves are not supported.");
        if (this is IRepositoryContextAdapter { TransactionOwner: { } owner }
            && owner.Database.CurrentTransaction is { } transaction
            && (!ReferenceEquals(Database.GetDbConnection(), owner.Database.GetDbConnection())
                || Database.ProviderName != owner.Database.ProviderName
                || Database.CurrentTransaction is not { } physicalTransaction
                || !ReferenceEquals(physicalTransaction.GetDbTransaction(), transaction.GetDbTransaction())))
            throw new NotSupportedException("A physical context must share its logical owner's connection and transaction.");
        return CachedServiceProvider.GetService<UnitOfWorkManager>()?.ValidateSave(this) == true;
    }

    /// <summary>
    /// Validates native database access against this context's save state, operation lifetime,
    /// transaction participation, and physical adapter ownership before provider I/O begins.
    /// </summary>
    /// <remarks>
    /// Provider APIs that bypass EF command interceptors, such as binary COPY, must call this method
    /// immediately before access. It performs no database I/O, starts no transaction, and does not
    /// apply save policies. Callers still own provider-specific prerequisites and failure handling.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The context or operation is failed, terminal, or not a selected participant.</exception>
    /// <exception cref="NotSupportedException">A physical adapter does not share its owner's connection and transaction.</exception>
    public void ValidateDatabaseAccess()
    {
        // Save validates its transaction before executing internal commands. Outside it, native EF access
        // must obey the same operation lifetime and participant selection as tracked saves.
        if (!_saving) ValidateSave();
    }

    /// <summary>Executes a controlled set-based/native write and declares its complete snapshot dependency effects before I/O.</summary>
    /// <remarks>Call from a named repository method inside a managed UnitOfWork. Declarations are conservative even if no row changes.
    /// This does not synthesize per-entity audit, synchronization or domain events skipped by bulk SQL. The delegate must use this context's transaction.</remarks>
    public async Task<TResult> ExecuteSnapshotWriteAsync<TResult>(IReadOnlyCollection<Type> affectedEntityTypes,
        Func<CancellationToken, Task<TResult>> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affectedEntityTypes);
        ArgumentNullException.ThrowIfNull(write);
        if (affectedEntityTypes.Count == 0) throw new ArgumentException("Declare at least one affected entity type.", nameof(affectedEntityTypes));
        ValidateDatabaseAccess();
        var manager = CachedServiceProvider.GetRequiredService<UnitOfWorkManager>();
        if (!manager.IsActive || Database.CurrentTransaction is null)
            throw new InvalidOperationException("Controlled snapshot writes require an active managed transaction.");
        if (affectedEntityTypes.Any(type => !Model.GetEntityTypes().Any(entity => type.IsAssignableFrom(entity.ClrType))))
            throw new ArgumentException("Every affected entity type must be mapped by this context.", nameof(affectedEntityTypes));
        CaptureSnapshotChanges(affectedEntityTypes);
        try { return await write(cancellationToken); }
        catch (Exception exception)
        {
            manager.MarkRollbackOnly(ExecutionTransactionFailure.FromException("SnapshotBulkWrite", exception));
            throw;
        }
    }

    private void CaptureSnapshotChanges(IEnumerable<Type> entityTypes)
    {
        if (CachedServiceProvider.GetService<SnapshotRegistration<TDbContext>>() is { } registration)
            CachedServiceProvider.GetRequiredService<SnapshotTransaction>().Capture(this, registration, entityTypes);
    }

    /// <summary>Checks staged and already-flushed changes affecting a registered dataset in this operation.</summary>
    /// <remarks>Use only to select an explicit application-owned current-state read. This method never merges business state into shared snapshots.</remarks>
    public bool HasUncommittedSnapshotChanges(string datasetKey)
    {
        ValidateDatabaseAccess();
        var registration = CachedServiceProvider.GetService<SnapshotRegistration<TDbContext>>()
            ?? throw new InvalidOperationException("This context has no registered snapshot datasets.");
        var dataset = registration.Datasets.SingleOrDefault(x => x.Key == datasetKey)
            ?? throw new ArgumentException("Unknown snapshot dataset.", nameof(datasetKey));
        return CachedServiceProvider.GetRequiredService<SnapshotTransaction>().HasChanges(this, registration.SourceId, dataset);
    }

    void IRepositoryContextLifetime.MarkDatabaseFailure(ExecutionTransactionFailure failure)
        => CachedServiceProvider.GetService<IUnitOfWorkManager>()?.Current?.MarkRollbackOnly(
            failure with { DbContextType = GetType().FullName });

    private async Task<int> SaveCoreAsync(bool useAsync, bool acceptAll, CancellationToken token)
    {
        if (!acceptAll) throw new NotSupportedException("RepositoryDbContext requires acceptAllChangesOnSuccess=true. Retry failures in a fresh scope.");
        var managedTransaction = ValidateSave();
        _saving = true;
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        var autoSavepoints = Database.AutoSavepointsEnabled;
        try
        {
            token.ThrowIfCancellationRequested();
            // The operation owns recovery across both business and Outbox flushes. A failed context cannot retry.
            if (managedTransaction) Database.AutoSavepointsEnabled = false;
            // Sharding coordinators delegate their EF save to physical RepositoryDbContexts. Policies
            // belong to those actual saves, never to both the aggregate tracker and its physical trackers.
            IReadOnlyList<PersistenceChange> changes = this is IRepositoryContextAdapter { CoordinatesSave: true }
                ? [] : PersistencePolicies.Apply(this, AuditPropertySetter, CachedServiceProvider.GetServices<IPersistencePolicy>());
            CaptureSnapshotChanges(changes.Select(change => change.Entry.Metadata.ClrType));
            ChangeTracker.AutoDetectChangesEnabled = false;
            var outbox = OutboxOptions;
            var switches = CachedServiceProvider.GetServices<IEntityEventPublishSwitch>().ToArray();
            if (EntityEventOptions is { } entityEvents)
            {
                var projectedChanges = changes.Where(change =>
                    entityEvents.Projections.ContainsKey(change.Entry.Metadata.ClrType)
                    && switches.All(x => x.CanPublish(change.Entry.Entity))).ToArray();
                if (!useAsync && projectedChanges.Any(change =>
                        entityEvents.AsyncEntityTypes.Contains(change.Entry.Metadata.ClrType)))
                    throw new NotSupportedException("An enabled asynchronous entity-event projection requires SaveChangesAsync.");
                if (projectedChanges.Length != 0)
                    CachedServiceProvider.GetRequiredService<UnitOfWorkManager>().RequireOutboxOwner();
            }
            if (outbox is not null && !Database.IsRelational())
                throw new NotSupportedException("Transactional outbox requires a relational provider.");

            var affected = useAsync ? await base.SaveChangesAsync(true, token) : base.SaveChanges(true);
            if (useAsync) await CaptureEntityEventsAsync(changes, switches, token);
            else CaptureEntityEvents(changes, switches);
            if (outbox is not null)
            {
                if (_pendingMessages.Count != 0)
                    Set<OutboxMessage>().AddRange(_pendingMessages);
            }
            ChangeTracker.DetectChanges();
            if (ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                CaptureSnapshotChanges(ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    .Select(entry => entry.Metadata.ClrType));
                if (useAsync) await base.SaveChangesAsync(true, token);
                else base.SaveChanges(true);
            }
            _pendingMessages.Clear();
            return affected;
        }
        catch (Exception exception)
        {
            _saveFailed = true;
            CachedServiceProvider.GetService<IUnitOfWorkManager>()?.Current?.MarkRollbackOnly(
                ExecutionTransactionFailure.FromException("SaveChanges", exception) with { DbContextType = GetType().FullName });
            throw;
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
            if (managedTransaction) Database.AutoSavepointsEnabled = autoSavepoints;
            _saving = false;
        }
    }

    private async Task CaptureEntityEventsAsync(IReadOnlyList<PersistenceChange> changes,
        IReadOnlyList<IEntityEventPublishSwitch> switches, CancellationToken token)
    {
        var projections = EntityEventOptions?.Projections;
        if (projections is null || changes.Count == 0) return;
        if (!changes.Any(change => projections.ContainsKey(change.Entry.Metadata.ClrType)
                && switches.All(x => x.CanPublish(change.Entry.Entity)))) return;
        var owner = CachedServiceProvider.GetRequiredService<UnitOfWorkManager>().RequireOutboxOwner();
        var factory = CachedServiceProvider.GetRequiredService<IEventMessageFactory>();
        foreach (var change in changes)
        {
            if (!projections.TryGetValue(change.Entry.Metadata.ClrType, out var callbacks)
                || switches.Any(x => !x.CanPublish(change.Entry.Entity))) continue;
            foreach (var project in callbacks)
            {
                var payload = project.Async is { } asyncProject
                    ? await asyncProject(CachedServiceProvider.UnderlyingProvider, change, token)
                    : project.Sync!(CachedServiceProvider.UnderlyingProvider, change);
                if (payload is not null) StageProjectedEvent(payload, owner, factory);
            }
        }
    }

    private void CaptureEntityEvents(IReadOnlyList<PersistenceChange> changes,
        IReadOnlyList<IEntityEventPublishSwitch> switches)
    {
        var projections = EntityEventOptions?.Projections;
        if (projections is null || changes.Count == 0) return;
        if (!changes.Any(change => projections.ContainsKey(change.Entry.Metadata.ClrType)
                && switches.All(x => x.CanPublish(change.Entry.Entity)))) return;
        var owner = CachedServiceProvider.GetRequiredService<UnitOfWorkManager>().RequireOutboxOwner();
        var factory = CachedServiceProvider.GetRequiredService<IEventMessageFactory>();
        foreach (var change in changes)
        {
            if (!projections.TryGetValue(change.Entry.Metadata.ClrType, out var callbacks)
                || switches.Any(x => !x.CanPublish(change.Entry.Entity))) continue;
            foreach (var project in callbacks)
            {
                var payload = project.Sync!(CachedServiceProvider.UnderlyingProvider, change);
                if (payload is not null) StageProjectedEvent(payload, owner, factory);
            }
        }
    }

    private static void StageProjectedEvent(object payload, IOutboxStoreContext owner, IEventMessageFactory factory)
    {
        var message = factory.Prepare(payload.GetType(), payload, EventSubscriptionScope.Distributed);
        owner.StagePreparedMessage(message);
    }

    #region Entity conventions

    private static readonly MethodInfo _configureBasePropertiesMethodInfo
        = typeof(RepositoryDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureBaseProperties),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    private static readonly MethodInfo _configureValueConverterMethodInfo
        = typeof(RepositoryDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureValueConverter),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    protected virtual void ConfigureBaseProperties<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
       where TEntity : class
    {
        if (mutableEntityType.IsOwned())
        {
            return;
        }

        if (!typeof(IEntity).IsAssignableFrom(typeof(TEntity)))
        {
            return;
        }

        modelBuilder.Entity<TEntity>().ConfigureByConvention();

        ConfigureGlobalFilters<TEntity>(modelBuilder, mutableEntityType);
    }

    /// <summary>
    /// Configures global filters for given entity.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="modelBuilder"></param>
    /// <param name="mutableEntityType"></param>
    protected virtual void ConfigureGlobalFilters<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.BaseType == null && ShouldFilterEntity<TEntity>(mutableEntityType))
        {
            var filterExpression = CreateFilterExpression<TEntity>(modelBuilder);
            if (filterExpression != null)
            {
                modelBuilder.Entity<TEntity>().HasQueryFilter(RepositoryQueryFilters.SoftDelete, filterExpression);
            }
        }
    }

    protected virtual void ConfigureValueConverter<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        //TODO Automatic conversion between UTC DateTime type and local time
    }

    /// <summary>
    /// Checks if given entity should be filtered.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="entityType"></param>
    /// <returns></returns>
    protected virtual bool ShouldFilterEntity<TEntity>(IMutableEntityType entityType) where TEntity : class
    {
        if (typeof(IHasSoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a filter expression for given entity.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="modelBuilder"></param>
    /// <returns></returns>
    protected virtual Expression<Func<TEntity, bool>>? CreateFilterExpression<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class
    {
        Expression<Func<TEntity, bool>>? expression = null;

        if (typeof(IHasSoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            const string softDeletePropertyName = nameof(IHasSoftDelete.IsDeleted);

            if (Options.UseDbFunction)
            {
                expression = e => EfCoreDataFilterDbFunctions.SoftDeleteFilter(((IHasSoftDelete)e).IsDeleted, true);
                modelBuilder.ConfigureSoftDeleteDbFunction(EfCoreDataFilterDbFunctions.SoftDeleteFilterMethodInfo, true);
            }
            else
            {
                expression = e => !EF.Property<bool>(e, softDeletePropertyName);
            }
        }

        return expression;
    }


    #endregion


}
