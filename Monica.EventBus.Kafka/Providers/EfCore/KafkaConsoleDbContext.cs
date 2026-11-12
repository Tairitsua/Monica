using Microsoft.EntityFrameworkCore;
using Monica.EntityFrameworkCore.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Persistence.Services;

namespace Monica.EventBus.Kafka.Providers.EfCore;

/// <summary>
/// EF Core DbContext for Kafka console persistence.
/// </summary>
public sealed class KafkaConsoleDbContext(
    DbContextOptions<KafkaConsoleDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<KafkaConsoleDbContext>(options, serviceProvider)
{
    /// <summary>
    /// Persisted Kafka clusters.
    /// </summary>
    public DbSet<KafkaClusterEntity> KafkaClusters => Set<KafkaClusterEntity>();

    /// <summary>
    /// Persisted Kafka performance snapshots.
    /// </summary>
    public DbSet<KafkaPerformanceSnapshotEntity> KafkaPerformanceSnapshots => Set<KafkaPerformanceSnapshotEntity>();

    protected override void OnModelCreatingExtend(ModelBuilder builder)
    {
        base.OnModelCreatingExtend(builder);

        builder.Entity<KafkaClusterEntity>(entity =>
        {
            entity.ToMonicaInfrastructureTable("mo_eventbus_kafka_clusters");
            entity.HasKey(cluster => cluster.ClusterId);
            entity.Property(cluster => cluster.ClusterId).HasMaxLength(128);
            entity.Property(cluster => cluster.DisplayName).HasMaxLength(256);
            entity.Property(cluster => cluster.BootstrapServers).HasMaxLength(2048);
            entity.Property(cluster => cluster.ClientId).HasMaxLength(256);
            entity.Property(cluster => cluster.SecurityProtocol).HasMaxLength(64);
            entity.Property(cluster => cluster.SaslMechanism).HasMaxLength(64);
            entity.Property(cluster => cluster.SaslUsername).HasMaxLength(512);
            entity.Property(cluster => cluster.SaslPassword).HasMaxLength(2048);
            entity.Property(cluster => cluster.SslCaLocation).HasMaxLength(2048);
            entity.Property(cluster => cluster.JmxEndpoint).HasMaxLength(512);
            entity.Property(cluster => cluster.DaprPubSubName).HasMaxLength(256);
        });

        builder.Entity<KafkaPerformanceSnapshotEntity>(entity =>
        {
            entity.ToMonicaInfrastructureTable("mo_eventbus_kafka_performance_snapshots");
            entity.HasKey(snapshot => snapshot.ClusterId);
            entity.Property(snapshot => snapshot.ClusterId).HasMaxLength(128);
            entity.Property(snapshot => snapshot.Message).HasMaxLength(2048);
            entity.Property(snapshot => snapshot.TopicMetricsJson);
            entity.Property(snapshot => snapshot.ConsumerGroupMetricsJson);
        });
    }
}
