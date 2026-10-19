using Microsoft.Extensions.Localization;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Kafka.Facades;
using Monica.EventBus.Kafka.Localization;
using Monica.EventBus.Kafka.Models;

namespace Monica.EventBus.Kafka.UIEventBusKafka.State;

/// <summary>
/// Page state for the Kafka EventBus console.
/// </summary>
public sealed class EventBusKafkaPageState(
    KafkaConsoleFacade facade,
    KafkaPerformancePollingState performancePollingState,
    IStringLocalizer<EventBusKafkaResource> localizer)
{
    private string? _topicsClusterId;
    private string? _consumerGroupsClusterId;
    private string? _performanceClusterId;
    private int _busyOperations;

    /// <summary>Gets whether initialization has completed, including a failed initial load.</summary>
    public bool HasInitialized { get; private set; }

    /// <summary>Gets whether integration and cluster configuration have been loaded successfully.</summary>
    public bool HasLoadedConfiguration { get; private set; }

    /// <summary>Gets whether matching selected-cluster broker, topic and group metadata has been verified.</summary>
    public bool HasDashboardSnapshot { get; private set; }

    /// <summary>Gets whether topics were successfully loaded for the exact selected cluster, including an empty inventory.</summary>
    public bool HasLoadedTopics => string.Equals(_topicsClusterId, SelectedClusterId, StringComparison.Ordinal) && _topicsClusterId is not null;

    /// <summary>Gets whether consumer groups were successfully loaded for the exact selected cluster, including an empty inventory.</summary>
    public bool HasLoadedConsumerGroups => string.Equals(_consumerGroupsClusterId, SelectedClusterId, StringComparison.Ordinal) && _consumerGroupsClusterId is not null;

    /// <summary>Gets the latest topic reload error, retaining earlier matching-cluster evidence when available.</summary>
    public string? TopicsError { get; private set; }

    /// <summary>Gets the latest consumer-group reload error, retaining earlier matching-cluster evidence when available.</summary>
    public string? ConsumerGroupsError { get; private set; }

    /// <summary>Gets the latest repository performance-evidence reload error.</summary>
    public string? LatestPerformanceError { get; private set; }

    /// <summary>Gets the exact route or explicitly selected cluster identity; missing identities are not substituted.</summary>
    public string? RequestedClusterId { get; private set; }

    /// <summary>Gets whether an explicitly requested cluster is absent from the successfully loaded configuration.</summary>
    public bool IsSelectedClusterUnavailable => HasLoadedConfiguration && SelectedClusterId is not null && SelectedCluster is null;

    /// <summary>
    /// Current integration snapshot.
    /// </summary>
    public KafkaIntegrationSnapshot Integration { get; private set; } = new();

    /// <summary>
    /// Visible Kafka clusters.
    /// </summary>
    public List<KafkaClusterSummary> Clusters { get; private set; } = [];

    /// <summary>
    /// Selected cluster dashboard.
    /// </summary>
    public KafkaDashboardSnapshot Dashboard { get; private set; } = new();

    /// <summary>
    /// Topics for the selected cluster.
    /// </summary>
    public List<KafkaTopicSummary> Topics { get; private set; } = [];

    /// <summary>
    /// Consumer groups for the selected cluster.
    /// </summary>
    public List<KafkaConsumerGroupSummary> ConsumerGroups { get; private set; } = [];

    /// <summary>
    /// Latest performance snapshot, including captures produced by the live sampler.
    /// </summary>
    public KafkaPerformanceSnapshot? LatestPerformance
    {
        get
        {
            var snapshot = performancePollingState.LatestSnapshot ?? Dashboard.LatestPerformance;
            return snapshot?.HasMetadataSnapshot == true &&
                   string.Equals(snapshot.ClusterId, SelectedClusterId, StringComparison.Ordinal)
                ? snapshot
                : null;
        }
    }

    /// <summary>
    /// Current retained-message inventory from the latest live sample or topic enrichment.
    /// </summary>
    public long? TotalRetainedMessageCount =>
        LatestPerformance is { TopicMetrics.Count: > 0 } latest
            ? latest.TotalRetainedMessageCount
            : Dashboard.TotalRetainedMessageCount;

    /// <summary>
    /// Current topic count from the latest live metadata sample or dashboard snapshot.
    /// </summary>
    /// <remarks>Interpret this count only when <see cref="HasDashboardSnapshot"/> is true or <see cref="LatestPerformance"/> exists.</remarks>
    public int CurrentTopicCount => LatestPerformance?.TopicCount ?? Dashboard.TopicCount;

    /// <summary>
    /// Current consumer-group count from the latest live metadata sample or dashboard snapshot.
    /// </summary>
    /// <remarks>Interpret this count only when <see cref="HasDashboardSnapshot"/> is true or <see cref="LatestPerformance"/> exists.</remarks>
    public int CurrentConsumerGroupCount => LatestPerformance?.ConsumerGroupCount ?? Dashboard.ConsumerGroupCount;

    /// <summary>
    /// Current broker count from the latest live metadata sample or dashboard snapshot.
    /// </summary>
    /// <remarks>Interpret this count only when <see cref="HasDashboardSnapshot"/> is true or <see cref="LatestPerformance"/> exists.</remarks>
    public int CurrentBrokerCount => LatestPerformance?.BrokerCount ?? Dashboard.BrokerCount;

    /// <summary>
    /// Whether live performance sampling is enabled.
    /// </summary>
    public bool IsPerformanceAutoRefreshEnabled => performancePollingState.IsAutomaticRefreshEnabled;

    /// <summary>
    /// Current live performance refresh interval in seconds.
    /// </summary>
    public int PerformanceRefreshIntervalSeconds => performancePollingState.RefreshIntervalSeconds;

    /// <summary>
    /// Whether a live performance sample is in progress.
    /// </summary>
    public bool IsPerformanceSampling => performancePollingState.IsSampling;

    /// <summary>
    /// Timestamp of the latest completed live performance sample.
    /// </summary>
    public DateTimeOffset? LastPerformanceCapturedAt => performancePollingState.LastCapturedAt;

    /// <summary>Gets the latest completed performance attempt time, including failures.</summary>
    public DateTimeOffset? LastPerformanceAttemptedAt => performancePollingState.LastAttemptedAt;

    /// <summary>
    /// Latest live sampling diagnostic.
    /// </summary>
    public string? PerformanceSamplingError => performancePollingState.LastError;

    /// <summary>
    /// Selected cluster id.
    /// </summary>
    public string? SelectedClusterId { get; private set; }

    /// <summary>
    /// Last operation error message.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Whether the page is loading initial data.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Whether an operation is in progress.
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Selected cluster summary.
    /// </summary>
    public KafkaClusterSummary? SelectedCluster =>
        Clusters.FirstOrDefault(cluster => string.Equals(cluster.Config.ClusterId, SelectedClusterId, StringComparison.Ordinal));

    /// <summary>
    /// Whether the selected cluster supports direct Kafka admin operations.
    /// </summary>
    public bool CanAdminSelectedCluster => SelectedCluster is { IsReachable: true, Config.HasDirectKafkaAccess: true };

    /// <summary>
    /// Initializes all page data.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => await InitializeAsync(null, cancellationToken);

    /// <summary>
    /// Initializes one routed session, preserving and freshly probing an exact requested direct cluster.
    /// </summary>
    /// <param name="requestedClusterId">Exact route identity, or null to retain the explicit-test default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Missing and externally managed identities remain selected without fallback or administration.</remarks>
    public async Task InitializeAsync(string? requestedClusterId, CancellationToken cancellationToken = default)
    {
        RequestedClusterId = requestedClusterId;
        SelectedClusterId = requestedClusterId;
        IsLoading = true;
        try
        {
            await RefreshAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedClusterId is not null && SelectedCluster?.Config.HasDirectKafkaAccess == true)
                await TestClusterAsync(requestedClusterId, cancellationToken);
        }
        finally
        {
            IsLoading = false;
            HasInitialized = true;
        }
    }

    /// <summary>
    /// Refreshes integration, cluster, dashboard, and selected cluster details.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.GetIntegrationAsync(cancellationToken), cancellationToken), out var integration))
            {
                return;
            }

            Integration = integration;

            if (!TryRead(await ObserveEvidenceAsync(facade.ListClustersAsync(cancellationToken), cancellationToken), out var clusters))
            {
                return;
            }

            var previousSelectedClusterId = SelectedClusterId;
            Clusters = MergeClusterRuntimeState(clusters);
            HasLoadedConfiguration = true;
            SelectedClusterId = ResolveSelectedClusterId();
            var selectedClusterChanged = !string.Equals(previousSelectedClusterId, SelectedClusterId, StringComparison.Ordinal);
            if (selectedClusterChanged)
            {
                ClearSelectedClusterDetails();
            }

            if (IsSelectedClusterUnavailable)
            {
                Dashboard = new KafkaDashboardSnapshot { Integration = Integration };
                HasDashboardSnapshot = false;
                return;
            }

            if (!TryRead(await ObserveEvidenceAsync(facade.GetDashboardAsync(SelectedClusterId, cancellationToken), cancellationToken), out var dashboard))
            {
                return;
            }

            Dashboard = dashboard;
            ApplySelectedClusterToDashboard();
            ApplyLoadedDetailsToDashboard();
            await RefreshSelectedClusterDetailsAsync(cancellationToken);
        });

        await SynchronizePerformancePollingAsync(cancellationToken);
    }

    /// <summary>
    /// Selects a cluster and loads detail tabs.
    /// </summary>
    public async Task SelectClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedCluster = Clusters.FirstOrDefault(cluster =>
            string.Equals(cluster.Config.ClusterId, clusterId, StringComparison.Ordinal));
        RequestedClusterId = clusterId;
        SelectedClusterId = clusterId;
        Dashboard = new KafkaDashboardSnapshot { Integration = Integration, SelectedCluster = requestedCluster };
        HasDashboardSnapshot = false;
        ClearSelectedClusterDetails();
        if (requestedCluster is null)
        {
            await SynchronizePerformancePollingAsync(cancellationToken);
            return;
        }
        if (requestedCluster.Config.HasDirectKafkaAccess && !requestedCluster.IsReachable)
        {
            await TestClusterAsync(clusterId, cancellationToken);
            return;
        }
        await RunAsync(async () =>
        {
            if (TryRead(await ObserveEvidenceAsync(facade.GetDashboardAsync(clusterId, cancellationToken), cancellationToken), out var dashboard))
            {
                Dashboard = dashboard;
                ApplySelectedClusterToDashboard();
            }

            await RefreshSelectedClusterDetailsAsync(cancellationToken);
        });

        await SynchronizePerformancePollingAsync(cancellationToken);
    }

    /// <summary>
    /// Creates or updates a cluster.
    /// </summary>
    public async Task<bool> UpsertClusterAsync(KafkaClusterConfig cluster, CancellationToken cancellationToken = default)
    {
        return await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.UpsertClusterAsync(new KafkaClusterUpsertRequest { Cluster = cluster }, cancellationToken), cancellationToken), out var summary))
            {
                return false;
            }

            await RefreshAsync(cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Deletes a cluster.
    /// </summary>
    public async Task<bool> DeleteClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.DeleteClusterAsync(clusterId, cancellationToken), cancellationToken)))
            {
                return false;
            }

            if (string.Equals(SelectedClusterId, clusterId, StringComparison.Ordinal))
            {
                // Keep the deleted exact identity visible until the user deliberately chooses another.
                RequestedClusterId = clusterId;
                ClearSelectedClusterDetails();
            }

            await RefreshAsync(cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Tests the selected cluster.
    /// </summary>
    public async Task<bool> TestClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        var succeeded = await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.TestClusterAsync(clusterId, cancellationToken), cancellationToken), out var summary))
            {
                return false;
            }

            if (summary.IsReachable)
            {
                ActivateCluster(summary);
                await RefreshSelectedClusterDetailsIfSelectedAsync(
                    summary.Config.ClusterId,
                    cancellationToken,
                    suppressErrors: true);
                return true;
            }

            ApplyClusterSummary(summary);
            ClearSelectedClusterDetailsIfSelected(summary.Config.ClusterId);
            ErrorMessage = summary.ErrorMessage
                           ?? localizer["Clusters:Messages:Unreachable", summary.Config.DisplayName];
            return false;
        });

        await SynchronizePerformancePollingAsync(cancellationToken);
        return succeeded;
    }

    /// <summary>
    /// Creates a topic in the selected cluster.
    /// </summary>
    public async Task<bool> CreateTopicAsync(KafkaTopicCreateRequest request, CancellationToken cancellationToken = default)
    {
        var clusterId = request.ClusterId;
        return await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.CreateTopicAsync(request, cancellationToken), cancellationToken)))
            {
                return false;
            }

            await RefreshTopicsIfSelectedAsync(clusterId, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Deletes a topic from the selected cluster.
    /// </summary>
    public Task<bool> DeleteTopicAsync(string topicName, CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? DeleteTopicAsync(clusterId, topicName, cancellationToken)
            : Task.FromResult<bool>(false);

    /// <summary>Deletes the exact topic in a frozen cluster and reloads rows only if that cluster remains selected.</summary>
    /// <param name="clusterId">Exact cluster identity frozen before confirmation.</param>
    /// <param name="topicName">Exact topic name frozen before confirmation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> DeleteTopicAsync(string clusterId, string topicName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        return await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.DeleteTopicAsync(clusterId, topicName, cancellationToken), cancellationToken)))
            {
                return false;
            }

            await RefreshTopicsIfSelectedAsync(clusterId, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Clears all retained records from every partition of a selected topic without deleting it.
    /// </summary>
    public Task<bool> ClearTopicMessagesAsync(
        string topicName,
        CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? ClearTopicMessagesAsync(clusterId, topicName, cancellationToken)
            : Task.FromResult<bool>(false);

    /// <summary>Clears retained records in a frozen cluster without deleting or recreating the topic.</summary>
    /// <param name="clusterId">Exact cluster identity frozen before confirmation.</param>
    /// <param name="topicName">Exact topic name frozen before confirmation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> ClearTopicMessagesAsync(
        string clusterId,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        return await RunAsync(async () =>
        {
            if (!TryRead(await ObserveEvidenceAsync(facade.ClearTopicMessagesAsync(clusterId, topicName, cancellationToken), cancellationToken)))
            {
                return false;
            }

            await RefreshTopicsIfSelectedAsync(clusterId, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Increases a topic partition count.
    /// </summary>
    public Task<bool> IncreasePartitionsAsync(string topicName, int increaseTo, CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? IncreasePartitionsAsync(clusterId, topicName, increaseTo, cancellationToken)
            : Task.FromResult<bool>(false);

    /// <summary>Increases the partition count for a frozen cluster and topic.</summary>
    /// <param name="clusterId">Exact target cluster identity.</param>
    /// <param name="topicName">Exact target topic name.</param>
    /// <param name="increaseTo">Requested final partition count; the backend rejects reductions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> IncreasePartitionsAsync(string clusterId, string topicName, int increaseTo, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        return await RunAsync(async () =>
        {
            var request = new KafkaTopicPartitionRequest
            {
                ClusterId = clusterId,
                TopicName = topicName,
                IncreaseTo = increaseTo
            };

            if (!TryRead(await ObserveEvidenceAsync(facade.IncreasePartitionsAsync(request, cancellationToken), cancellationToken)))
            {
                return false;
            }

            await RefreshTopicsIfSelectedAsync(clusterId, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Updates topic retention.
    /// </summary>
    public Task<bool> UpdateRetentionAsync(string topicName, long retentionMs, CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? UpdateRetentionAsync(clusterId, topicName, retentionMs, cancellationToken)
            : Task.FromResult<bool>(false);

    /// <summary>Changes retention for a frozen cluster and topic.</summary>
    /// <param name="clusterId">Exact target cluster identity.</param>
    /// <param name="topicName">Exact target topic name.</param>
    /// <param name="retentionMs">Retention in milliseconds; negative one uses Kafka's unlimited convention.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> UpdateRetentionAsync(string clusterId, string topicName, long retentionMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        return await RunAsync(async () =>
        {
            var request = new KafkaTopicRetentionRequest
            {
                ClusterId = clusterId,
                TopicName = topicName,
                RetentionMs = retentionMs
            };

            if (!TryRead(await ObserveEvidenceAsync(facade.UpdateRetentionAsync(request, cancellationToken), cancellationToken)))
            {
                return false;
            }

            await RefreshTopicsIfSelectedAsync(clusterId, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Reads a bounded sample of recent messages from a selected cluster topic.
    /// </summary>
    public Task<KafkaTopicMessageBatch?> ReadTopicMessagesAsync(
        string topicName,
        int maxMessages,
        CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? ReadTopicMessagesAsync(clusterId, topicName, maxMessages, cancellationToken)
            : Task.FromResult<KafkaTopicMessageBatch?>(null);

    /// <summary>Reads bounded recent records for a frozen cluster and topic without advancing group commits.</summary>
    /// <param name="clusterId">Exact target cluster identity.</param>
    /// <param name="topicName">Exact target topic name.</param>
    /// <param name="maxMessages">Requested sample bound, limited by the configured backend preview policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<KafkaTopicMessageBatch?> ReadTopicMessagesAsync(
        string clusterId,
        string topicName,
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        KafkaTopicMessageBatch? batch = null;
        await RunAsync(async () =>
        {
            var request = new KafkaTopicMessagesRequest
            {
                ClusterId = clusterId,
                TopicName = topicName,
                MaxMessages = maxMessages
            };

            if (TryRead(await ObserveEvidenceAsync(facade.ReadTopicMessagesAsync(request, cancellationToken), cancellationToken), out var loadedBatch))
            {
                batch = loadedBatch;
            }
        });

        return batch;
    }

    /// <summary>
    /// Gets partition-level retained-message inventory for a selected cluster topic.
    /// </summary>
    public Task<KafkaTopicBacklogSnapshot?> GetTopicBacklogAsync(
        string topicName,
        CancellationToken cancellationToken = default) =>
        SelectedClusterId is { } clusterId
            ? GetTopicBacklogAsync(clusterId, topicName, cancellationToken)
            : Task.FromResult<KafkaTopicBacklogSnapshot?>(null);

    /// <summary>Loads partition watermark inventory for a frozen cluster and topic.</summary>
    /// <param name="clusterId">Exact target cluster identity.</param>
    /// <param name="topicName">Exact target topic name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<KafkaTopicBacklogSnapshot?> GetTopicBacklogAsync(
        string clusterId,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        KafkaTopicBacklogSnapshot? snapshot = null;
        await RunAsync(async () =>
        {
            if (TryRead(await ObserveEvidenceAsync(facade.GetTopicBacklogAsync(clusterId, topicName, cancellationToken), cancellationToken), out var loadedSnapshot))
            {
                snapshot = loadedSnapshot;
            }
        });

        return snapshot;
    }

    /// <summary>
    /// Captures and stores a fresh performance snapshot.
    /// </summary>
    public async Task<bool> CapturePerformanceAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedClusterId is null)
        {
            return false;
        }

        return await RunAsync(async () =>
        {
            if (!await performancePollingState.CaptureNowAsync(cancellationToken))
            {
                ErrorMessage = performancePollingState.LastError;
                return false;
            }

            Dashboard.LatestPerformance = performancePollingState.LatestSnapshot;
            return true;
        });
    }

    /// <summary>
    /// Enables or disables live performance sampling for the selected cluster.
    /// </summary>
    public Task SetPerformanceAutoRefreshAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return performancePollingState.SetAutomaticRefreshAsync(enabled, cancellationToken);
    }

    /// <summary>
    /// Changes the live performance sampling interval for the current page session.
    /// </summary>
    public Task SetPerformanceRefreshIntervalAsync(
        int seconds,
        CancellationToken cancellationToken = default)
    {
        return performancePollingState.SetRefreshIntervalAsync(seconds, cancellationToken);
    }

    /// <summary>
    /// Stops live sampling when the page is no longer active.
    /// </summary>
    public Task DeactivateAsync()
    {
        return performancePollingState.DeactivateAsync();
    }

    private async Task RefreshSelectedClusterDetailsAsync(CancellationToken cancellationToken, bool suppressErrors = false)
    {
        if (SelectedClusterId is null || !ShouldLoadSelectedClusterDetails)
        {
            ClearSelectedClusterDetails();
            return;
        }

        await RefreshTopicsAsync(cancellationToken, suppressErrors);
        await RefreshConsumerGroupsAsync(cancellationToken, suppressErrors);
        await RefreshPerformanceAsync(cancellationToken, suppressErrors);
    }

    private async Task RefreshSelectedClusterDetailsIfSelectedAsync(
        string clusterId,
        CancellationToken cancellationToken,
        bool suppressErrors = false)
    {
        if (!string.Equals(SelectedClusterId, clusterId, StringComparison.Ordinal))
        {
            return;
        }

        await RefreshSelectedClusterDetailsAsync(cancellationToken, suppressErrors);
    }

    private bool ShouldLoadSelectedClusterDetails => SelectedCluster?.IsReachable == true;

    private void ClearSelectedClusterDetailsIfSelected(string clusterId)
    {
        if (string.Equals(SelectedClusterId, clusterId, StringComparison.Ordinal))
        {
            ClearSelectedClusterDetails();
        }
    }

    private void ClearSelectedClusterDetails()
    {
        HasDashboardSnapshot = false;
        Topics = [];
        ConsumerGroups = [];
        performancePollingState.ClearSnapshot();
        _topicsClusterId = null;
        _consumerGroupsClusterId = null;
        _performanceClusterId = null;
        TopicsError = null;
        ConsumerGroupsError = null;
        LatestPerformanceError = null;
        Dashboard.TotalRetainedMessageCount = null;
        Dashboard.LatestPerformance = null;
    }

    private void ApplyClusterSummary(KafkaClusterSummary summary)
    {
        var index = Clusters.FindIndex(cluster =>
            string.Equals(cluster.Config.ClusterId, summary.Config.ClusterId, StringComparison.Ordinal));
        if (index >= 0)
        {
            Clusters[index] = summary;
        }
        else
        {
            Clusters.Add(summary);
            Clusters = Clusters
                .OrderBy(cluster => cluster.Config.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!string.Equals(SelectedClusterId, summary.Config.ClusterId, StringComparison.Ordinal))
        {
            return;
        }

        Dashboard.SelectedCluster = summary;
        Dashboard.BrokerCount = summary.BrokerCount;
        Dashboard.TopicCount = summary.TopicCount;
        Dashboard.ConsumerGroupCount = summary.ConsumerGroupCount;
        HasDashboardSnapshot = summary.IsReachable && summary.HasTopicInventory && summary.HasConsumerGroupInventory;
    }

    private void ActivateCluster(KafkaClusterSummary summary)
    {
        RequestedClusterId = summary.Config.ClusterId;
        SelectedClusterId = summary.Config.ClusterId;
        ClearSelectedClusterDetails();
        ApplyClusterSummary(summary);
    }

    private List<KafkaClusterSummary> MergeClusterRuntimeState(IReadOnlyList<KafkaClusterSummary> refreshedClusters)
    {
        var previousClusters = Clusters.ToDictionary(
            cluster => cluster.Config.ClusterId,
            StringComparer.Ordinal);

        return refreshedClusters
            .Select(cluster =>
            {
                if (previousClusters.TryGetValue(cluster.Config.ClusterId, out var previous) &&
                    CanPreserveRuntimeState(previous.Config, cluster.Config))
                {
                    CopyRuntimeState(previous, cluster);
                }

                return cluster;
            })
            .ToList();
    }

    private void ApplySelectedClusterToDashboard()
    {
        var selectedCluster = SelectedCluster;
        if (selectedCluster is null)
        {
            return;
        }

        Dashboard.SelectedCluster = selectedCluster;
        HasDashboardSnapshot = selectedCluster.IsReachable && selectedCluster.HasTopicInventory && selectedCluster.HasConsumerGroupInventory;
        if (selectedCluster.IsReachable)
        {
            Dashboard.BrokerCount = selectedCluster.BrokerCount;
            Dashboard.TopicCount = selectedCluster.TopicCount;
            Dashboard.ConsumerGroupCount = selectedCluster.ConsumerGroupCount;
        }
    }

    private void ApplyLoadedDetailsToDashboard()
    {
        if (string.Equals(_topicsClusterId, SelectedClusterId, StringComparison.Ordinal))
        {
            ApplyTopicBacklogSummary();
        }

        if (string.Equals(_consumerGroupsClusterId, SelectedClusterId, StringComparison.Ordinal))
        {
            Dashboard.ConsumerGroupCount = ConsumerGroups.Count;
        }

        if (string.Equals(_performanceClusterId, SelectedClusterId, StringComparison.Ordinal))
        {
            Dashboard.LatestPerformance = performancePollingState.LatestSnapshot ?? Dashboard.LatestPerformance;
        }
    }

    private static void CopyRuntimeState(KafkaClusterSummary source, KafkaClusterSummary target)
    {
        target.IsReachable = source.IsReachable;
        target.BrokerCount = source.BrokerCount;
        target.TopicCount = source.TopicCount;
        target.ConsumerGroupCount = source.ConsumerGroupCount;
        target.HasTopicInventory = source.HasTopicInventory;
        target.HasConsumerGroupInventory = source.HasConsumerGroupInventory;
        target.CapturedAt = source.CapturedAt;
        target.ErrorMessage = source.ErrorMessage;
    }

    private static bool CanPreserveRuntimeState(KafkaClusterConfig previous, KafkaClusterConfig current)
    {
        // Credentials are redacted from summaries. The stored edit revision invalidates their old probe evidence.
        return previous.UpdatedAt == current.UpdatedAt &&
               string.Equals(previous.BootstrapServers, current.BootstrapServers, StringComparison.Ordinal) &&
               string.Equals(previous.ClientId, current.ClientId, StringComparison.Ordinal) &&
               string.Equals(previous.SecurityProtocol, current.SecurityProtocol, StringComparison.Ordinal) &&
               string.Equals(previous.SaslMechanism, current.SaslMechanism, StringComparison.Ordinal) &&
               string.Equals(previous.SslCaLocation, current.SslCaLocation, StringComparison.Ordinal) &&
               previous.CredentialsManagedExternally == current.CredentialsManagedExternally;
    }

    private async Task RefreshTopicsAsync(CancellationToken cancellationToken, bool suppressErrors = false)
    {
        TopicsError = null;
        if (SelectedClusterId is null)
        {
            Topics = [];
            _topicsClusterId = null;
            return;
        }

        var clusterId = SelectedClusterId;
        var result = await ObserveEvidenceAsync(facade.ListTopicsAsync(clusterId, cancellationToken), cancellationToken);
        if (!string.Equals(clusterId, SelectedClusterId, StringComparison.Ordinal)) return;
        if (result.IsFailed(out var topicError)) TopicsError = topicError.Message;
        if (suppressErrors
                ? TryReadSilently(result, out var topics)
                : TryRead(result, out topics))
        {
            Topics = topics.ToList();
            _topicsClusterId = clusterId;
            ApplyTopicBacklogSummary();
        }
    }

    private void ApplyTopicBacklogSummary()
    {
        var applicationTopics = Topics
            .Where(topic => !topic.IsInternal)
            .ToList();
        Dashboard.TopicCount = Topics.Count;
        Dashboard.TotalRetainedMessageCount = applicationTopics.Count == 0
            ? 0
            : applicationTopics.All(topic => topic.RetainedMessageCount.HasValue)
                ? applicationTopics.Sum(topic => topic.RetainedMessageCount.GetValueOrDefault())
                : null;
    }

    private async Task RefreshConsumerGroupsAsync(CancellationToken cancellationToken, bool suppressErrors = false)
    {
        ConsumerGroupsError = null;
        if (SelectedClusterId is null)
        {
            ConsumerGroups = [];
            _consumerGroupsClusterId = null;
            return;
        }

        var clusterId = SelectedClusterId;
        var result = await ObserveEvidenceAsync(facade.ListConsumerGroupsAsync(clusterId, cancellationToken), cancellationToken);
        if (!string.Equals(clusterId, SelectedClusterId, StringComparison.Ordinal)) return;
        if (result.IsFailed(out var groupsError)) ConsumerGroupsError = groupsError.Message;
        if (suppressErrors
                ? TryReadSilently(result, out var groups)
                : TryRead(result, out groups))
        {
            ConsumerGroups = groups.ToList();
            _consumerGroupsClusterId = clusterId;
            Dashboard.ConsumerGroupCount = ConsumerGroups.Count;
        }
    }

    private async Task RefreshPerformanceAsync(CancellationToken cancellationToken, bool suppressErrors = false)
    {
        LatestPerformanceError = null;
        if (SelectedClusterId is null)
        {
            performancePollingState.ClearSnapshot();
            _performanceClusterId = null;
            return;
        }

        var clusterId = SelectedClusterId;
        var result = await ObserveEvidenceAsync(facade.GetLatestPerformanceAsync(clusterId, cancellationToken), cancellationToken);
        if (!string.Equals(clusterId, SelectedClusterId, StringComparison.Ordinal)) return;
        if (result.IsFailed(out var performanceError)) LatestPerformanceError = performanceError.Message;
        if (suppressErrors
                ? TryReadSilently(result, out var snapshot)
                : TryRead(result, out snapshot))
        {
            performancePollingState.SetSnapshot(snapshot);
            _performanceClusterId = clusterId;
            Dashboard.LatestPerformance = snapshot;
        }
    }

    private Task SynchronizePerformancePollingAsync(CancellationToken cancellationToken)
    {
        return performancePollingState.ActivateAsync(
            SelectedClusterId,
            ShouldLoadSelectedClusterDetails,
            cancellationToken);
    }

    private Task RefreshTopicsIfSelectedAsync(string clusterId, CancellationToken cancellationToken) =>
        string.Equals(clusterId, SelectedClusterId, StringComparison.Ordinal)
            ? RefreshTopicsAsync(cancellationToken)
            : Task.CompletedTask;

    private string? ResolveSelectedClusterId()
    {
        if (RequestedClusterId is not null) return RequestedClusterId;
        var selectedCluster = string.IsNullOrWhiteSpace(SelectedClusterId)
            ? null
            : Clusters.FirstOrDefault(cluster => string.Equals(cluster.Config.ClusterId, SelectedClusterId, StringComparison.Ordinal));
        if (selectedCluster?.IsReachable == true)
        {
            return selectedCluster.Config.ClusterId;
        }

        var connectedCluster = Clusters.FirstOrDefault(cluster => cluster.IsReachable);
        if (connectedCluster is not null)
        {
            return connectedCluster.Config.ClusterId;
        }

        if (selectedCluster is not null)
        {
            return selectedCluster.Config.ClusterId;
        }

        if (!string.IsNullOrWhiteSpace(Integration.PrimaryClusterId) &&
            Clusters.Any(cluster => string.Equals(cluster.Config.ClusterId, Integration.PrimaryClusterId, StringComparison.Ordinal)))
        {
            return Integration.PrimaryClusterId;
        }

        return Clusters.FirstOrDefault()?.Config.ClusterId;
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = ++_busyOperations > 0;
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorMessage = ex.GetMessageRecursively();
        }
        finally
        {
            IsBusy = --_busyOperations > 0;
        }
    }

    private async Task<bool> RunAsync(Func<Task<bool>> action)
    {
        IsBusy = ++_busyOperations > 0;
        ErrorMessage = null;
        try
        {
            return await action();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorMessage = ex.GetMessageRecursively();
            return false;
        }
        finally
        {
            IsBusy = --_busyOperations > 0;
        }
    }

    private bool TryRead(Res result)
    {
        if (!result.IsFailed(out var error))
        {
            return true;
        }

        ErrorMessage = error.Message;
        return false;
    }

    private static async Task<T> ObserveEvidenceAsync<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        var result = await operation;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private bool TryRead<T>(Res<T> result, out T data)
    {
        if (!result.IsFailed(out var error))
        {
            data = result.Data!;
            return true;
        }

        data = default!;
        ErrorMessage = error.Message;
        return false;
    }

    private static bool TryReadSilently<T>(Res<T> result, out T data)
    {
        if (!result.IsFailed(out _))
        {
            data = result.Data!;
            return true;
        }

        data = default!;
        return false;
    }
}
