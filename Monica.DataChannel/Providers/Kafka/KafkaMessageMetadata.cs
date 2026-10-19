using Confluent.Kafka;

namespace Monica.DataChannel.Providers.Kafka;

/// <summary>Detached Kafka receive metadata stored in the existing channel metadata bag.</summary>
/// <remarks>Headers retain their order, duplicate names, null values, and bytes. No consumer is exposed to pipeline code.</remarks>
public sealed record KafkaMessageMetadata(
    string Topic, int Partition, long Offset, byte[]? Key, IReadOnlyList<KafkaMessageHeader> Headers, Timestamp Timestamp)
{
    /// <summary>The metadata bag key containing this receive snapshot.</summary>
    public const string METADATA_KEY = "Kafka";
    /// <summary>Optional output key override. Accepts a string, bytes, or null for an explicitly unkeyed record.</summary>
    public const string OUTPUT_KEY_METADATA_KEY = "Kafka.Key";
    /// <summary>Optional output header list of <see cref="KafkaMessageHeader"/> values.</summary>
    public const string OUTPUT_HEADERS_METADATA_KEY = "Kafka.Headers";
}

/// <summary>One raw Kafka header. Null is distinct from an empty value.</summary>
public sealed record KafkaMessageHeader(string Key, byte[]? Value);

/// <summary>One visibly blocked partition, which remains unacknowledged until endpoint reinitialization.</summary>
public sealed record KafkaBlockedPartition(string Topic, int Partition, long Offset, string Reason);

/// <summary>A detached bounded-input snapshot; it contains no credentials or live clients.</summary>
public sealed record KafkaConsumerSnapshot(
    string State, int AssignedPartitions, int PausedPartitions, int BufferedMessages, int ActiveWorkers,
    long ReceivedMessages, long ProcessedMessages, long CommittedMessages, IReadOnlyList<KafkaBlockedPartition> BlockedPartitions);
