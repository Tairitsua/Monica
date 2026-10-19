namespace Monica.EventBus.Kafka.Models;

/// <summary>
/// Current Kafka EventBus integration state displayed by the console.
/// </summary>
public sealed class KafkaIntegrationSnapshot
{
    /// <summary>
    /// Resolved integration mode.
    /// </summary>
    public KafkaIntegrationMode Mode { get; set; }

    /// <summary>
    /// Display name for the active distributed EventBus provider.
    /// </summary>
    public string ActiveProviderName { get; set; } = "Unknown";

    /// <summary>
    /// Dapr pub/sub component name when Dapr-backed Kafka is configured.
    /// </summary>
    public string? DaprPubSubName { get; set; }

    /// <summary>
    /// Cluster id selected as the primary Kafka integration target.
    /// </summary>
    public string? PrimaryClusterId { get; set; }

    /// <summary>
    /// Console capabilities available for the active integration.
    /// </summary>
    public KafkaConsoleCapabilities Capabilities { get; set; }

    /// <summary>
    /// Human-readable details explaining unavailable or degraded capabilities.
    /// </summary>
    public IReadOnlyList<string> Messages { get; set; } = [];
}

/// <summary>
/// Summarizes a Kafka cluster and its last known console state.
/// </summary>
public sealed class KafkaClusterSummary
{
    /// <summary>
    /// Cluster configuration with SASL username and password removed for presentation.
    /// Empty credentials on an edit preserve the stored values; broker operations resolve private configuration.
    /// </summary>
    public KafkaClusterConfig Config { get; set; } = new();

    /// <summary>
    /// Whether the last connection probe succeeded.
    /// </summary>
    public bool IsReachable { get; set; }

    /// <summary>
    /// Last connection or sampling error, if any.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Broker count from the last metadata query.
    /// </summary>
    public int BrokerCount { get; set; }

    /// <summary>
    /// Topic count from the last metadata query.
    /// </summary>
    public int TopicCount { get; set; }

    /// <summary>
    /// Whether the last connection probe successfully loaded the topic inventory, including an empty inventory.
    /// </summary>
    public bool HasTopicInventory { get; set; }

    /// <summary>
    /// Consumer group count from the last group query.
    /// </summary>
    public int ConsumerGroupCount { get; set; }

    /// <summary>
    /// Whether the last connection probe successfully loaded consumer groups, including an empty inventory.
    /// </summary>
    public bool HasConsumerGroupInventory { get; set; }

    /// <summary>
    /// Time when the summary was captured.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Kafka broker metadata.
/// </summary>
public sealed class KafkaBrokerInfo
{
    /// <summary>
    /// Broker id assigned by Kafka.
    /// </summary>
    public int BrokerId { get; set; }

    /// <summary>
    /// Broker host name or address.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Broker port.
    /// </summary>
    public int Port { get; set; }
}

/// <summary>
/// Result of a direct Kafka connection test.
/// </summary>
public sealed class KafkaConnectionTestResult
{
    /// <summary>
    /// Brokers returned by Kafka metadata.
    /// </summary>
    public IReadOnlyList<KafkaBrokerInfo> Brokers { get; set; } = [];
}

/// <summary>
/// Kafka topic summary displayed in topic management.
/// </summary>
public sealed class KafkaTopicSummary
{
    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Number of partitions in the topic.
    /// </summary>
    public int Partitions { get; set; }

    /// <summary>
    /// Best-effort replication factor calculated from partition metadata.
    /// </summary>
    public int ReplicationFactor { get; set; }

    /// <summary>
    /// Whether the topic is an internal Kafka topic.
    /// </summary>
    /// <remarks>
    /// Kafka's metadata flag is combined with the reserved <c>__</c> name prefix so callers remain
    /// safe when a provider returns a topic summary without setting the metadata flag.
    /// </remarks>
    public bool IsInternal
    {
        get => _isInternal ||
               (!string.IsNullOrEmpty(TopicName) && TopicName.StartsWith("__", StringComparison.Ordinal));
        set => _isInternal = value;
    }

    private bool _isInternal;

    /// <summary>
    /// Metadata error reported by Kafka for this topic, when the topic name is known but its
    /// partition metadata is currently unavailable.
    /// </summary>
    /// <remarks>
    /// Kafka can return a topic name together with a per-topic error (for example while a leader
    /// is being elected). Keeping that row visible makes the topic inventory consistent with the
    /// cluster metadata count instead of silently dropping it from the console.
    /// </remarks>
    public string? MetadataError { get; set; }

    /// <summary>
    /// Whether partition metadata is currently available for this topic.
    /// </summary>
    public bool IsMetadataAvailable => string.IsNullOrWhiteSpace(MetadataError);

    /// <summary>
    /// Optional retention time in milliseconds.
    /// </summary>
    public long? RetentionMs { get; set; }

    /// <summary>
    /// Current number of retained messages that a new consumer can read from this topic.
    /// </summary>
    /// <remarks>
    /// This value is calculated from each partition's latest offset minus earliest offset. It is
    /// independent of consumer groups and can be calculated even when no consumer group exists.
    /// </remarks>
    public long? RetainedMessageCount { get; set; }
}

/// <summary>
/// Current retained-message inventory for one Kafka topic.
/// </summary>
public sealed class KafkaTopicBacklogSnapshot
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Total retained messages across all readable partitions.
    /// </summary>
    /// <remarks>
    /// Kafka offset ranges can contain holes on compacted topics, so this is a lightweight
    /// offset-range inventory rather than a full message scan.
    /// </remarks>
    public long TotalRetainedMessageCount { get; set; }

    /// <summary>
    /// Time when the snapshot was captured.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Partition-level offset ranges.
    /// </summary>
    public IReadOnlyList<KafkaTopicPartitionBacklog> Partitions { get; set; } = [];
}

/// <summary>
/// Current retained-message inventory for one Kafka topic partition.
/// </summary>
public sealed class KafkaTopicPartitionBacklog
{
    /// <summary>
    /// Partition id.
    /// </summary>
    public int Partition { get; set; }

    /// <summary>
    /// Earliest retained offset currently readable from this partition.
    /// </summary>
    public long EarliestOffset { get; set; }

    /// <summary>
    /// Latest offset, represented as the next offset Kafka will assign in this partition.
    /// </summary>
    public long LatestOffset { get; set; }

    /// <summary>
    /// Number of retained messages estimated from <see cref="LatestOffset"/> minus <see cref="EarliestOffset"/>.
    /// </summary>
    public long RetainedMessageCount { get; set; }
}

/// <summary>
/// Request for creating a Kafka topic.
/// </summary>
public sealed class KafkaTopicCreateRequest
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Number of partitions to create.
    /// </summary>
    public int Partitions { get; set; } = 1;

    /// <summary>
    /// Replication factor to request.
    /// </summary>
    public short ReplicationFactor { get; set; } = 1;

    /// <summary>
    /// Optional retention time in milliseconds.
    /// </summary>
    public long? RetentionMs { get; set; }
}

/// <summary>
/// Request for increasing a topic's partition count.
/// </summary>
public sealed class KafkaTopicPartitionRequest
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// New total partition count. Kafka does not allow reducing partitions.
    /// </summary>
    public int IncreaseTo { get; set; }
}

/// <summary>
/// Request for updating topic retention.
/// </summary>
public sealed class KafkaTopicRetentionRequest
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Retention time in milliseconds.
    /// </summary>
    public long RetentionMs { get; set; }
}

/// <summary>
/// Request for reading a bounded sample of topic messages without committing consumer offsets.
/// </summary>
public sealed class KafkaTopicMessagesRequest
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of messages to return. The module clamps this to its configured preview limit.
    /// </summary>
    public int MaxMessages { get; set; } = 20;
}

/// <summary>
/// Bounded topic message sample returned to the Kafka console.
/// </summary>
public sealed class KafkaTopicMessageBatch
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the preview started from each partition's latest offsets.
    /// </summary>
    public bool ReadFromEnd { get; set; } = true;

    /// <summary>
    /// Maximum number of messages requested after module-level clamping.
    /// </summary>
    public int MaxMessages { get; set; }

    /// <summary>
    /// Time when the sample was captured.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Sampled messages ordered from newest to oldest.
    /// </summary>
    public IReadOnlyList<KafkaTopicMessageSample> Messages { get; set; } = [];
}

/// <summary>
/// Kafka message metadata and decoded content shown in the console.
/// </summary>
public sealed class KafkaTopicMessageSample
{
    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Partition that contains the message.
    /// </summary>
    public int Partition { get; set; }

    /// <summary>
    /// Kafka offset within the partition.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Producer timestamp when Kafka exposes it.
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Decoded message key, if present.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Decoded message value, base64 payload, or null for tombstone messages.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Encoding used for <see cref="Value"/>.
    /// </summary>
    public string ValueEncoding { get; set; } = "utf-8";

    /// <summary>
    /// Original value payload length in bytes.
    /// </summary>
    public int ValueSizeBytes { get; set; }

    /// <summary>
    /// Whether the value was truncated before display.
    /// </summary>
    public bool IsTruncated { get; set; }

    /// <summary>
    /// Whether the Kafka value is null, which represents a compacted-topic tombstone.
    /// </summary>
    public bool IsTombstone { get; set; }
}

/// <summary>
/// Consumer group summary displayed in the console.
/// </summary>
public sealed class KafkaConsumerGroupSummary
{
    /// <summary>
    /// Consumer group id.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Current group state reported by Kafka.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Member count from consumer group description.
    /// </summary>
    /// <remarks>A nonempty <see cref="ErrorMessage"/> means this count must not be interpreted as verified member evidence.</remarks>
    public int MemberCount { get; set; }

    /// <summary>
    /// Gets or sets the diagnostic when the group description or scoped metric query is incomplete;
    /// otherwise null. A default member count on a failed description does not prove an empty group.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total lag when it can be calculated; otherwise null.
    /// </summary>
    public long? TotalLag { get; set; }

    /// <summary>
    /// Gets or sets the topic associated with this summary when it is returned by a topic-scoped
    /// query. Cluster-wide consumer-group listings leave this value <see langword="null"/>.
    /// </summary>
    public string? TopicName { get; set; }
}

/// <summary>
/// Latest cluster performance snapshot retained by the Kafka console.
/// </summary>
public sealed class KafkaPerformanceSnapshot
{
    /// <summary>
    /// Target cluster id.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// UTC capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether broker, topic and consumer-group metadata completed successfully during this capture.
    /// </summary>
    /// <remarks>
    /// The default is false. Default numeric counts in a failed or configuration-only capture are
    /// not evidence of an empty cluster. Optional offset diagnostics do not clear this flag.
    /// Persistent EF Core console stores must add the corresponding
    /// <c>KafkaPerformanceSnapshotEntity.HasMetadataSnapshot</c> column through a consumer-owned migration.
    /// Older stored snapshots default to false and require a fresh capture before their counts are shown as verified.
    /// </remarks>
    public bool HasMetadataSnapshot { get; set; }

    /// <summary>
    /// Broker count.
    /// </summary>
    public int BrokerCount { get; set; }

    /// <summary>
    /// Topic count.
    /// </summary>
    public int TopicCount { get; set; }

    /// <summary>
    /// Consumer group count.
    /// </summary>
    public int ConsumerGroupCount { get; set; }

    /// <summary>
    /// Total consumer lag when it can be calculated.
    /// </summary>
    public long? TotalLag { get; set; }

    /// <summary>
    /// Sum of latest offsets across sampled non-internal topic partitions.
    /// </summary>
    /// <remarks>
    /// The value is <see langword="null"/> when any required topic metadata or offset batch is
    /// incomplete; partial totals are not used for rate calculation.
    /// </remarks>
    public long? TotalLogEndOffset { get; set; }

    /// <summary>
    /// Total retained messages across all sampled non-internal topic partitions.
    /// </summary>
    /// <remarks>
    /// This is independent of consumer groups and can be calculated when no consumer group exists.
    /// </remarks>
    public long? TotalRetainedMessageCount { get; set; }

    /// <summary>
    /// Sum of committed offsets across sampled consumer groups and non-internal topic partitions.
    /// </summary>
    public long? TotalConsumerCommittedOffset { get; set; }

    /// <summary>
    /// Estimated topic write rate in messages per second, calculated from latest offset growth.
    /// </summary>
    public double? MessageWriteRatePerSecond { get; set; }

    /// <summary>
    /// Estimated consumer processing rate in messages per second, calculated from committed offset growth.
    /// </summary>
    public double? MessageConsumeRatePerSecond { get; set; }

    /// <summary>
    /// Whether JMX metrics were included in this snapshot.
    /// </summary>
    /// <remarks>
    /// A configured JMX endpoint only declares configuration visibility. The current producer
    /// does not run a JMX collector and leaves this value false.
    /// </remarks>
    public bool IncludesJmxMetrics { get; set; }

    /// <summary>
    /// Optional diagnostic message recorded during sampling.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Per-topic offset and rate metrics captured during this sampling cycle.
    /// </summary>
    /// <remarks>
    /// Internal Kafka topics are intentionally omitted. The collection is ordered by topic name so
    /// callers can render a stable list without applying another ordering pass.
    /// </remarks>
    public IReadOnlyList<KafkaTopicPerformanceSnapshot> TopicMetrics { get; set; } = [];

    /// <summary>
    /// Per-topic and per-consumer-group metrics captured during this sampling cycle.
    /// </summary>
    /// <remarks>
    /// The collection is intentionally provider-neutral so the performance list can distinguish
    /// groups that consume the same topic and open the member/partition detail view.
    /// </remarks>
    public IReadOnlyList<KafkaConsumerGroupTopicMetrics> ConsumerGroupMetrics { get; set; } = [];
}

/// <summary>
/// Performance metrics for one application Kafka topic at a point in time.
/// </summary>
public sealed class KafkaTopicPerformanceSnapshot
{
    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Sum of latest offsets across the topic's partitions.
    /// </summary>
    public long? TotalLogEndOffset { get; set; }

    /// <summary>
    /// Number of retained messages estimated from the topic's partition watermarks.
    /// </summary>
    public long? TotalRetainedMessageCount { get; set; }

    /// <summary>
    /// Sum of committed offsets across sampled consumer groups for this topic.
    /// </summary>
    public long? TotalConsumerCommittedOffset { get; set; }

    /// <summary>
    /// Sum of consumer lag across sampled consumer groups for this topic.
    /// </summary>
    public long? TotalLag { get; set; }

    /// <summary>
    /// Estimated write rate in messages per second.
    /// </summary>
    public double? MessageWriteRatePerSecond { get; set; }

    /// <summary>
    /// Estimated consumer processing rate in messages per second.
    /// </summary>
    public double? MessageConsumeRatePerSecond { get; set; }
}

/// <summary>
/// Kafka offset totals used to calculate performance rates.
/// </summary>
public sealed class KafkaPerformanceOffsetTotals
{
    /// <summary>
    /// Sum of latest offsets across sampled non-internal topic partitions.
    /// </summary>
    /// <remarks>
    /// Check <see cref="AreTopicOffsetsComplete"/> before using the value; partial totals are not used for rate
    /// calculation.
    /// </remarks>
    public long TotalLogEndOffset { get; set; }

    /// <summary>
    /// Whether all required topic metadata and offset batches were captured.
    /// </summary>
    /// <remarks>
    /// Consumers should not derive rates from a partial total. The default is <see langword="true"/>
    /// for providers that return a complete total without setting this flag explicitly.
    /// </remarks>
    public bool AreTopicOffsetsComplete { get; set; } = true;

    /// <summary>
    /// Whether committed offsets were captured for every sampled consumer group.
    /// </summary>
    /// <remarks>
    /// Topic watermark totals can remain complete when an individual consumer-group query fails.
    /// Consumers should not derive aggregate consumption rates or lag from partial group offsets.
    /// </remarks>
    public bool AreConsumerOffsetsComplete { get; set; } = true;

    /// <summary>
    /// Sum of committed offsets across sampled consumer groups and non-internal topic partitions.
    /// </summary>
    public long? TotalConsumerCommittedOffset { get; set; }

    /// <summary>
    /// Total lag calculated from latest offsets and committed offsets when consumer offsets are available.
    /// </summary>
    public long? TotalLag { get; set; }

    /// <summary>
    /// Total retained messages across all sampled non-internal topic partitions.
    /// </summary>
    public long? TotalRetainedMessageCount { get; set; }

    /// <summary>
    /// Raw offset totals grouped by application topic.
    /// </summary>
    public IReadOnlyList<KafkaTopicPerformanceOffsetTotals> TopicTotals { get; set; } = [];
}

/// <summary>
/// Raw offset totals for one application Kafka topic.
/// </summary>
public sealed class KafkaTopicPerformanceOffsetTotals
{
    /// <summary>
    /// Topic name.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Sum of latest offsets across the topic's partitions.
    /// </summary>
    public long? TotalLogEndOffset { get; set; }

    /// <summary>
    /// Number of retained messages estimated from partition watermarks.
    /// </summary>
    public long? TotalRetainedMessageCount { get; set; }

    /// <summary>
    /// Sum of committed offsets across sampled consumer groups for this topic.
    /// </summary>
    public long? TotalConsumerCommittedOffset { get; set; }

    /// <summary>
    /// Sum of consumer lag across sampled consumer groups for this topic.
    /// </summary>
    public long? TotalLag { get; set; }

    /// <summary>
    /// Whether all required partition offsets for this topic were captured.
    /// </summary>
    public bool IsComplete { get; set; } = true;
}

/// <summary>
/// Dashboard aggregate for the selected Kafka cluster.
/// </summary>
public sealed class KafkaDashboardSnapshot
{
    /// <summary>
    /// Current integration state.
    /// </summary>
    public KafkaIntegrationSnapshot Integration { get; set; } = new();

    /// <summary>
    /// Selected cluster summary.
    /// </summary>
    public KafkaClusterSummary? SelectedCluster { get; set; }

    /// <summary>
    /// Latest performance snapshot for the selected cluster.
    /// </summary>
    public KafkaPerformanceSnapshot? LatestPerformance { get; set; }

    /// <summary>
    /// Topic count shown by the dashboard.
    /// </summary>
    public int TopicCount { get; set; }

    /// <summary>
    /// Consumer group count shown by the dashboard.
    /// </summary>
    public int ConsumerGroupCount { get; set; }

    /// <summary>
    /// Broker count shown by the dashboard.
    /// </summary>
    public int BrokerCount { get; set; }

    /// <summary>
    /// Total retained messages shown by the dashboard for the selected cluster.
    /// </summary>
    public long? TotalRetainedMessageCount { get; set; }
}
