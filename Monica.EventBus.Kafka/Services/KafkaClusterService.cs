using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Models;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Modules;

namespace Monica.EventBus.Kafka.Services;

/// <summary>
/// Coordinates configured Kafka clusters and direct broker probes.
/// </summary>
public sealed class KafkaClusterService(
    IKafkaConsoleRepository repository,
    IKafkaAdminProvider adminProvider,
    KafkaBootstrapEndpointProbe bootstrapEndpointProbe,
    IOptions<ModuleEventBusKafkaOption> options)
{
    private readonly Func<KafkaClusterConfig, CancellationToken, Task<KafkaBootstrapEndpointProbeResult>> _probeBootstrap
        = bootstrapEndpointProbe.ProbeAsync;

    internal KafkaClusterService(IKafkaConsoleRepository repository, IKafkaAdminProvider adminProvider,
        IOptions<ModuleEventBusKafkaOption> options,
        Func<KafkaClusterConfig, CancellationToken, Task<KafkaBootstrapEndpointProbeResult>> probeBootstrap)
        : this(repository, adminProvider, new KafkaBootstrapEndpointProbe(options), options)
    {
        ArgumentNullException.ThrowIfNull(probeBootstrap);
        _probeBootstrap = probeBootstrap;
    }

    /// <summary>Gets presentation summaries with SASL credentials removed.</summary>
    public async Task<IReadOnlyList<KafkaClusterSummary>> ListClustersAsync(CancellationToken cancellationToken = default)
    {
        var clusters = await GetEffectiveClustersAsync(cancellationToken);
        var summaries = new List<KafkaClusterSummary>(clusters.Count);

        foreach (var cluster in clusters)
        {
            summaries.Add(await BuildSummaryAsync(cluster, refreshBrokerMetadata: false, cancellationToken));
        }

        return summaries
            .OrderBy(summary => summary.Config.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves private server-side broker configuration, including credentials.</summary>
    /// <remarks>Use presentation summaries when returning cluster information to a UI or HTTP caller.</remarks>
    public async Task<KafkaClusterConfig> GetRequiredClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        var clusters = await GetEffectiveClustersAsync(cancellationToken);
        return clusters.FirstOrDefault(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
               ?? throw new KeyNotFoundException($"Kafka cluster '{clusterId}' was not found.");
    }

    /// <summary>
    /// Gets a cluster that can be used for direct Kafka administration after a managed endpoint preflight.
    /// </summary>
    /// <param name="clusterId">Cluster identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reachable direct Kafka cluster configuration.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the cluster does not expose direct Kafka access or none of its bootstrap endpoints is reachable.
    /// </exception>
    public async Task<KafkaClusterConfig> GetRequiredDirectAdminClusterAsync(
        string clusterId,
        CancellationToken cancellationToken = default)
    {
        var cluster = await GetRequiredClusterAsync(clusterId, cancellationToken);
        await EnsureDirectKafkaAccessAsync(cluster, cancellationToken);
        return cluster;
    }

    /// <summary>
    /// Verifies that a cluster exposes direct Kafka access and at least one bootstrap endpoint accepts TCP connections.
    /// </summary>
    /// <param name="cluster">Cluster configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when direct Kafka access is unavailable or endpoint preflight fails.
    /// </exception>
    public async Task EnsureDirectKafkaAccessAsync(
        KafkaClusterConfig cluster,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (!cluster.HasDirectKafkaAccess)
        {
            throw new InvalidOperationException(
                $"Kafka cluster '{cluster.ClusterId}' is visible but direct admin access is not configured.");
        }

        var preflight = await _probeBootstrap(cluster, cancellationToken);
        if (!preflight.IsReachable)
        {
            throw new InvalidOperationException(
                preflight.ErrorMessage ?? $"Kafka cluster '{cluster.ClusterId}' is not reachable.");
        }
    }

    /// <summary>Saves a console-managed cluster, preserving saved credentials when their edit fields are empty.</summary>
    /// <returns>A presentation summary without SASL credentials.</returns>
    /// <exception cref="InvalidOperationException">The identity belongs to the native EventBus startup configuration.</exception>
    public async Task<KafkaClusterSummary> UpsertClusterAsync(KafkaClusterConfig cluster, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeForPersistence(cluster);
        EnsureClusterCanBeEdited(normalized.ClusterId);
        var existing = (await GetEffectiveClustersAsync(cancellationToken))
            .FirstOrDefault(value => value.ClusterId == normalized.ClusterId);
        // Summary credentials are redacted. Editing another field must not erase the saved secret.
        normalized.SaslUsername ??= existing?.SaslUsername;
        normalized.SaslPassword ??= existing?.SaslPassword;
        await repository.UpsertClusterAsync(normalized, cancellationToken);
        return await BuildSummaryAsync(normalized, refreshBrokerMetadata: false, cancellationToken);
    }

    /// <summary>Deletes a console-managed cluster without changing startup-owned runtime configuration.</summary>
    /// <exception cref="InvalidOperationException">The identity belongs to the native EventBus startup configuration.</exception>
    public Task DeleteClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        var normalizedId = clusterId.Trim();
        EnsureClusterCanBeEdited(normalizedId);
        return repository.DeleteClusterAsync(normalizedId, cancellationToken);
    }

    public async Task<KafkaClusterSummary> TestConnectionAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        var cluster = await GetRequiredClusterAsync(clusterId, cancellationToken);
        var summary = await BuildSummaryAsync(cluster, refreshBrokerMetadata: true, cancellationToken);
        if (!summary.IsReachable)
        {
            summary.ErrorMessage ??= $"Kafka cluster '{cluster.ClusterId}' is not reachable.";
        }

        return summary;
    }

    public async Task<IReadOnlyList<KafkaClusterConfig>> GetEffectiveClustersAsync(CancellationToken cancellationToken = default)
    {
        var clusters = new Dictionary<string, KafkaClusterConfig>(StringComparer.Ordinal);

        foreach (var configuredCluster in options.Value.ConfiguredClusters)
        {
            var normalized = configuredCluster.Clone().Normalize();
            clusters[normalized.ClusterId] = normalized;
        }

        foreach (var persistedCluster in await repository.GetClustersAsync(cancellationToken))
        {
            var normalized = persistedCluster.Clone().Normalize();
            clusters[normalized.ClusterId] = normalized;
        }

        // Runtime configuration owns the actual transport. A persisted console record cannot
        // redirect its diagnostics to another broker while the producer keeps using this one.
        if (options.Value.DirectEventBusCluster is { } runtimeCluster)
        {
            var normalized = runtimeCluster.Clone().Normalize();
            clusters[normalized.ClusterId] = normalized;
        }

        return clusters.Values
            .OrderBy(cluster => cluster.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<KafkaClusterSummary> BuildSummaryAsync(
        KafkaClusterConfig cluster,
        bool refreshBrokerMetadata,
        CancellationToken cancellationToken)
    {
        var summary = new KafkaClusterSummary
        {
            Config = CreateSummaryConfiguration(cluster),
            CapturedAt = DateTimeOffset.UtcNow
        };

        if (!summary.Config.HasDirectKafkaAccess)
        {
            summary.ErrorMessage = summary.Config.IsDaprBacked
                ? "Kafka broker credentials are managed outside Monica; direct admin operations are disabled."
                : "Kafka bootstrap servers or credentials are not available for direct admin operations.";
            return summary;
        }

        if (!refreshBrokerMetadata)
        {
            return summary;
        }

        var preflight = await _probeBootstrap(cluster, cancellationToken);
        if (!preflight.IsReachable)
        {
            summary.IsReachable = false;
            summary.ErrorMessage = preflight.ErrorMessage;
            return summary;
        }

        try
        {
            var connection = await adminProvider.TestConnectionAsync(cluster, cancellationToken);
            summary.IsReachable = true;
            summary.BrokerCount = connection.Brokers.Count;
            await TryPopulateInventoryCountsAsync(summary, cluster, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            summary.IsReachable = false;
            summary.ErrorMessage = BuildConnectionErrorMessage(ex);
        }

        return summary;
    }

    private async Task TryPopulateInventoryCountsAsync(
        KafkaClusterSummary summary,
        KafkaClusterConfig cluster,
        CancellationToken cancellationToken)
    {
        try
        {
            var topics = await adminProvider.ListTopicMetadataAsync(cluster, cancellationToken);
            summary.TopicCount = topics.Count;
            summary.HasTopicInventory = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            summary.ErrorMessage = $"Kafka connection succeeded, but topic metadata could not be read: {ex.GetMessageRecursively()}";
        }

        try
        {
            var groups = await adminProvider.ListConsumerGroupsAsync(cluster, cancellationToken);
            summary.ConsumerGroupCount = groups.Count;
            summary.HasConsumerGroupInventory = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var groupError = $"consumer group metadata could not be read: {ex.GetMessageRecursively()}";
            summary.ErrorMessage = string.IsNullOrWhiteSpace(summary.ErrorMessage)
                ? $"Kafka connection succeeded, but {groupError}"
                : $"{summary.ErrorMessage}; {groupError}";
        }
    }

    private static string BuildConnectionErrorMessage(Exception exception)
    {
        var message = exception.GetMessageRecursively();
        if (message.Contains("Local: Broker transport failure", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}. TCP reached the bootstrap endpoint, but Kafka metadata could not be read. " +
                   "Check the broker advertised.listeners value, security protocol, SASL/SSL settings, and whether the advertised broker address is reachable from Monica.";
        }

        return message;
    }

    private static KafkaClusterConfig NormalizeForPersistence(KafkaClusterConfig cluster)
    {
        var normalized = cluster.Clone().Normalize();
        if (string.IsNullOrWhiteSpace(normalized.DisplayName))
        {
            throw new ArgumentException("Kafka cluster display name is required.", nameof(cluster));
        }

        if (string.IsNullOrWhiteSpace(normalized.BootstrapServers) && !normalized.CredentialsManagedExternally)
        {
            throw new ArgumentException("Kafka bootstrap servers are required when credentials are managed by Monica.", nameof(cluster));
        }

        normalized.UpdatedAt = DateTimeOffset.UtcNow;
        return normalized;
    }

    private void EnsureClusterCanBeEdited(string clusterId)
    {
        if (options.Value.DirectEventBusCluster is { } runtimeCluster
            && runtimeCluster.Clone().Normalize().ClusterId == clusterId)
        {
            throw new InvalidOperationException(
                $"Kafka cluster '{clusterId}' is owned by the native EventBus startup configuration and cannot be edited or deleted through the console.");
        }
    }

    private static KafkaClusterConfig CreateSummaryConfiguration(KafkaClusterConfig cluster)
    {
        var summary = cluster.Clone().Normalize();
        summary.SaslUsername = null;
        summary.SaslPassword = null;
        return summary;
    }
}
