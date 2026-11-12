using Confluent.Kafka;

namespace Monica.DataChannel.Providers.Kafka;

/// <summary>Creates native Kafka clients at the external transport boundary.</summary>
/// <remarks>The endpoint owns and disposes every returned client. Factories must not share consumer instances.</remarks>
public interface IKafkaDataChannelClientFactory
{
    /// <summary>Creates one producer with explicit raw-byte serializers and an optional application partitioner.</summary>
    IProducer<byte[], byte[]> CreateProducer(ProducerConfig configuration, PartitionerDelegate? partitioner);
    /// <summary>Creates one consumer. Its callbacks execute only as part of the owner's native consumer calls.</summary>
    IConsumer<byte[], byte[]> CreateConsumer(ConsumerConfig configuration, KafkaConsumerCallbacks callbacks);
}

/// <summary>Consumer ownership callbacks supplied by the endpoint's poll owner.</summary>
/// <remarks>Factory implementations invoke these synchronously from consumer polling; never from worker tasks.</remarks>
public sealed record KafkaConsumerCallbacks(
    Action<IReadOnlyList<TopicPartition>> Assigned,
    Action<IReadOnlyList<TopicPartition>> Revoked,
    Action<IReadOnlyList<TopicPartition>> Lost)
{
    /// <summary>Reports native connection errors synchronously from polling, without calling the consumer.</summary>
    public Action<Error>? Error { get; init; }
}

internal sealed class KafkaDataChannelClientFactory : IKafkaDataChannelClientFactory
{
    public IProducer<byte[], byte[]> CreateProducer(ProducerConfig configuration, PartitionerDelegate? partitioner)
    {
        var builder = new ProducerBuilder<byte[], byte[]>(configuration);
        if (partitioner is not null) builder.SetDefaultPartitioner(partitioner);
        return builder.Build();
    }

    public IConsumer<byte[], byte[]> CreateConsumer(ConsumerConfig configuration, KafkaConsumerCallbacks callbacks) =>
        new ConsumerBuilder<byte[], byte[]>(configuration)
            .SetErrorHandler((_, error) => callbacks.Error?.Invoke(error))
            .SetPartitionsAssignedHandler((_, partitions) => callbacks.Assigned(partitions))
            .SetPartitionsRevokedHandler((_, partitions) => callbacks.Revoked(partitions.Select(item => item.TopicPartition).ToArray()))
            .SetPartitionsLostHandler((_, partitions) => callbacks.Lost(partitions.Select(item => item.TopicPartition).ToArray()))
            .Build();
}
