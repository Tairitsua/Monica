using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace Monica.Kafka;

/// <summary>Chooses how a native Kafka input handles an exhausted record-level processing failure.</summary>
public enum KafkaFailureAction
{
    /// <summary>Keep the input unacknowledged until recovery.</summary>
    Block,
    /// <summary>Publish the raw record to a separate topic before acknowledging the input.</summary>
    DeadLetter,
    /// <summary>Record the error and advance past the failed input.</summary>
    LogAndSkip
}

/// <summary>Shared wire contract for native Kafka failure diagnostics and at-least-once dead-letter delivery.</summary>
public static class KafkaDeliveryFailure
{
    /// <summary>Original delivery identity, supplied by the producer or attached to an exception by the receiver.</summary>
    public const string MessageIdHeader = "monica-message-id";

    /// <summary>Checks the action and, for dead-letter forwarding, its distinct Kafka topic.</summary>
    public static void Validate(KafkaFailureAction action, string sourceTopic, string? deadLetterTopic)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (action != KafkaFailureAction.DeadLetter) return;
        if (string.IsNullOrEmpty(deadLetterTopic) || deadLetterTopic.Length > 249 || deadLetterTopic is "." or ".."
            || deadLetterTopic == sourceTopic || deadLetterTopic.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Dead-letter delivery requires a valid topic distinct from the input topic.", nameof(deadLetterTopic));
    }

    /// <summary>Describes a failed record without including its body. The broker position remains usable without an envelope ID.</summary>
    public static string Describe(ConsumeResult<byte[], byte[]> record, Exception failure)
    {
        var header = record.Message?.Headers?.LastOrDefault(item => item.Key == MessageIdHeader)?.GetValueBytes();
        var id = header is not null ? Display(header) : failure.Data[MessageIdHeader]?.ToString() ?? "<unknown>";
        return $"{record.TopicPartitionOffset}, MessageId={id}, Key={Display(record.Message?.Key)}";
    }

    private static string Display(byte[]? bytes)
    {
        if (bytes is null) return "<null>";
        const int limit = 256;
        var value = Encoding.UTF8.GetString(bytes, 0, Math.Min(limit, bytes.Length))
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return bytes.Length > limit ? value + "…" : value;
    }

    /// <summary>Persists the original key, value and ordered headers with source and failure information.</summary>
    /// <remarks>Group, source topic, partition and offset identify repeat publications after a crash. The target record has a fresh timestamp.</remarks>
    public static async Task ForwardAsync(IProducer<byte[], byte[]> producer, string destination, string groupId,
        ConsumeResult<byte[], byte[]> record, Exception failure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = new Headers();
        if (record.Message?.Headers is { } original)
            foreach (var header in original) headers.Add(header.Key, header.GetValueBytes());
        void Add(string name, string value) => headers.Add("monica-dlq-" + name, Encoding.UTF8.GetBytes(value));
        Add("topic", record.Topic);
        Add("partition", record.Partition.Value.ToString(CultureInfo.InvariantCulture));
        Add("offset", record.Offset.Value.ToString(CultureInfo.InvariantCulture));
        Add("consumer-group", groupId);
        if (record.Message is { } source && source.Timestamp.Type != TimestampType.NotAvailable)
            Add("timestamp-ms", source.Timestamp.UnixTimestampMs.ToString(CultureInfo.InvariantCulture));
        Add("error-type", failure.GetType().FullName ?? failure.GetType().Name);
        Add("error", failure.Message);
        var forwarded = new Message<byte[], byte[]> { Key = record.Message?.Key!, Value = record.Message?.Value!, Headers = headers };
        var delivery = await producer.ProduceAsync(destination, forwarded, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (delivery.Status != PersistenceStatus.Persisted)
            throw new IOException($"Kafka did not confirm dead-letter persistence on '{destination}' for {record.TopicPartitionOffset}.");
    }
}
