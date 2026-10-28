using Microsoft.EntityFrameworkCore;
using Monica.EntityFrameworkCore.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.Models;
using Monica.Repository.Persistence.Services;

namespace Monica.Configuration.EfCore.DbContext;

/// <summary>
/// EF Core context for Monica.Configuration persistence.
/// </summary>
/// <remarks>
/// The consuming host owns this context's migrations and must apply them before configuration-store operations begin.
/// The context never creates or upgrades its schema automatically.
/// </remarks>
public sealed class ConfigurationDbContext(
    DbContextOptions<ConfigurationDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<ConfigurationDbContext>(options, serviceProvider)
{
    private const string TIMESTAMP_WITH_TIME_ZONE_COLUMN_TYPE = "timestamp with time zone";
    private const string TIMESTAMP_COLUMN_TYPE = "timestamp";

    public DbSet<ConfigurationDefinitionEntity> ConfigurationDefinitions => Set<ConfigurationDefinitionEntity>();

    public DbSet<ConfigurationDefinitionPublishHistoryEntity> ConfigurationDefinitionPublishHistories =>
        Set<ConfigurationDefinitionPublishHistoryEntity>();

    public DbSet<ConfigurationDefinitionPublisherStateEntity> ConfigurationDefinitionPublisherStates =>
        Set<ConfigurationDefinitionPublisherStateEntity>();

    public DbSet<ConfigurationEffectiveValueEntity> ConfigurationEffectiveValues => Set<ConfigurationEffectiveValueEntity>();

    public DbSet<ConfigurationValueHistoryEntity> ConfigurationValueHistories => Set<ConfigurationValueHistoryEntity>();

    public DbSet<ConfigurationMutationGroupEntity> ConfigurationMutationGroups => Set<ConfigurationMutationGroupEntity>();

    public DbSet<ConfigurationUnifiedVersionEntity> ConfigurationUnifiedVersions => Set<ConfigurationUnifiedVersionEntity>();

    public DbSet<ConfigurationUnifiedVersionDocumentEntity> ConfigurationUnifiedVersionDocuments =>
        Set<ConfigurationUnifiedVersionDocumentEntity>();

    internal DbSet<ConfigurationStoreLockEntity> ConfigurationStoreLocks => Set<ConfigurationStoreLockEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Configuration values can include secrets, so this store never enables sensitive data logging implicitly.
        ConfigureRepositoryModel(optionsBuilder);
    }

    protected override void OnModelCreatingExtend(ModelBuilder modelBuilder)
    {
        ConfigureKeys(modelBuilder);
        base.OnModelCreatingExtend(modelBuilder);

        modelBuilder.Entity<ConfigurationDefinitionEntity>().ToMonicaInfrastructureTable("ConfigurationDefinitions");
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>().ToMonicaInfrastructureTable("ConfigurationDefinitionPublishHistories");
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>().ToMonicaInfrastructureTable("ConfigurationEffectiveValues");
        modelBuilder.Entity<ConfigurationValueHistoryEntity>().ToMonicaInfrastructureTable("ConfigurationValueHistories");
        modelBuilder.Entity<ConfigurationMutationGroupEntity>().ToMonicaInfrastructureTable("ConfigurationMutationGroups");
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>().ToMonicaInfrastructureTable("ConfigurationUnifiedVersions");
        modelBuilder.Entity<ConfigurationUnifiedVersionDocumentEntity>().ToMonicaInfrastructureTable("ConfigurationUnifiedVersionDocuments");

        var timeColumnType = UsesTimestampWithTimeZone(Database.ProviderName)
            ? TIMESTAMP_WITH_TIME_ZONE_COLUMN_TYPE
            : TIMESTAMP_COLUMN_TYPE;

        modelBuilder.Entity<ConfigurationStoreLockEntity>()
            .ToMonicaInfrastructureTable("ConfigurationStoreLocks");
        modelBuilder.Entity<ConfigurationStoreLockEntity>()
            .Property(x => x.LockKey)
            .IsRequired()
            .HasMaxLength(100);
        modelBuilder.Entity<ConfigurationStoreLockEntity>()
            .Property(x => x.LockVersion)
            .IsRequired();
        modelBuilder.Entity<ConfigurationStoreLockEntity>()
            .HasData(ConfigurationStoreLockEntity.CreateSeedRows());
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .HasIndex(x => x.DefinitionIdentity)
            .HasDatabaseName(ConfigurationDbSchema.DefinitionIdentityIndex)
            .IsUnique();
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .HasIndex(x => x.FromProject);
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .HasIndex(x => x.Category);
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .Property(x => x.FromProject)
            .IsRequired()
            .HasMaxLength(200);
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .Property(x => x.Category)
            .HasMaxLength(200);
        modelBuilder.Entity<ConfigurationDefinitionEntity>()
            .Property(x => x.DefinitionRevision)
            .IsConcurrencyToken();
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>()
            .HasIndex(x => new { x.DefinitionIdentity, x.DefinitionRevision })
            .HasDatabaseName(ConfigurationDbSchema.DefinitionRevisionIndex)
            .IsUnique();
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>()
            .Property(x => x.FromProject)
            .IsRequired()
            .HasMaxLength(200);
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>()
            .Property(x => x.Category)
            .HasMaxLength(200);
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>()
            .Property(x => x.PublishedTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .ToMonicaInfrastructureTable("ConfigurationDefinitionPublisherStates");
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .Property(x => x.PublisherIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .Property(x => x.PublisherKey)
            .IsRequired()
            .HasMaxLength(191);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .HasIndex(x => x.PublisherIdentity)
            .HasDatabaseName(ConfigurationDbSchema.DefinitionPublisherIndex);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .Property(x => x.ObservationKind)
            .IsRequired()
            .HasMaxLength(50);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .Property(x => x.ReloadBehavior)
            .IsRequired()
            .HasMaxLength(50);
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>()
            .HasIndex(x => x.DefinitionIdentity)
            .HasDatabaseName(ConfigurationDbSchema.EffectiveValueIdentityIndex)
            .IsUnique();
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>()
            .Property(x => x.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>()
            .Property(x => x.LastModifiedTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationValueHistoryEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationValueHistoryEntity>()
            .Property(x => x.ModifiedTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationValueHistoryEntity>()
            .HasIndex(x => new { x.DefinitionIdentity, x.PathDepth, x.ModifiedTime })
            .HasDatabaseName(ConfigurationDbSchema.ValueHistoryIdentityIndex);
        modelBuilder.Entity<ConfigurationValueHistoryEntity>()
            .HasIndex(x => x.MutationGroupId);
        modelBuilder.Entity<ConfigurationMutationGroupEntity>()
            .Property(x => x.CreatedTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationMutationGroupEntity>()
            .Property(x => x.RolledBackTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationMutationGroupEntity>()
            .HasIndex(x => x.CreatedTime);
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>()
            .Property(x => x.Version)
            .ValueGeneratedNever();
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>()
            .Property(x => x.CreatedTime)
            .HasPrecision(6)
            .HasColumnType(timeColumnType);
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>()
            .HasIndex(x => x.CreatedTime);
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>()
            .HasIndex(x => x.MutationGroupId);
        modelBuilder.Entity<ConfigurationUnifiedVersionDocumentEntity>()
            .Property(x => x.DefinitionIdentity)
            .IsRequired()
            .IsUnicode(false)
            .IsFixedLength()
            .HasMaxLength(ConfigurationDefinitionIdentity.Length);
        modelBuilder.Entity<ConfigurationUnifiedVersionDocumentEntity>()
            .HasIndex(x => new { x.DefinitionIdentity, x.Version })
            .HasDatabaseName(ConfigurationDbSchema.UnifiedVersionDocumentIdentityIndex)
            .IsUnique();
    }

    private static void ConfigureKeys(ModelBuilder modelBuilder)
    {
        // RepositoryDbContext applies string defaults while its model is built, so custom keys must be known first.
        modelBuilder.Entity<ConfigurationStoreLockEntity>().HasKey(x => x.LockKey);
        modelBuilder.Entity<ConfigurationDefinitionEntity>().HasKey(x => x.DefinitionKey);
        modelBuilder.Entity<ConfigurationDefinitionPublishHistoryEntity>().HasKey(x => x.HistoryId);
        modelBuilder.Entity<ConfigurationDefinitionPublisherStateEntity>()
            .HasKey(x => new { x.DefinitionIdentity, x.PublisherIdentity });
        modelBuilder.Entity<ConfigurationEffectiveValueEntity>().HasKey(x => x.DefinitionKey);
        modelBuilder.Entity<ConfigurationValueHistoryEntity>().HasKey(x => x.HistoryId);
        modelBuilder.Entity<ConfigurationMutationGroupEntity>().HasKey(x => x.GroupId);
        modelBuilder.Entity<ConfigurationUnifiedVersionEntity>().HasKey(x => x.Version);
        modelBuilder.Entity<ConfigurationUnifiedVersionDocumentEntity>()
            .HasKey(x => new { x.Version, x.DefinitionKey });
    }

    private static bool UsesTimestampWithTimeZone(string? providerName)
    {
        return providerName is not null
               && (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                   || providerName.Contains("GaussDB", StringComparison.OrdinalIgnoreCase));
    }
}
