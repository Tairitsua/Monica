using Confluent.Kafka;

namespace Monica.DataChannel.Providers.Kafka;

internal static class KafkaClientConfiguration
{
    internal static ProducerConfig Producer(KafkaOptions options)
    {
        var config = new ProducerConfig
        {
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageMaxBytes = options.ProducerMessageMaxBytes,
            MessageTimeoutMs = (int)options.ProducerDeliveryTimeout.TotalMilliseconds
        };
        ApplyConnection(config, options);
        return config;
    }

    internal static ConsumerConfig Consumer(KafkaOptions options)
    {
        var config = new ConsumerConfig
        {
            GroupId = options.ConsumerGroupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = options.AutoOffsetReset,
            MaxPollIntervalMs = (int)options.ConsumerMaxPollInterval.TotalMilliseconds,
            SessionTimeoutMs = (int)options.ConsumerSessionTimeout.TotalMilliseconds,
            MaxPartitionFetchBytes = options.ConsumerFetchMessageMaxBytes,
            FetchMaxBytes = options.ConsumerFetchMaxBytes,
            QueuedMaxMessagesKbytes = options.ConsumerQueuedMaxMessagesKbytes
        };
        ApplyConnection(config, options);
        return config;
    }

    private static void ApplyConnection(ClientConfig config, KafkaOptions options)
    {
        config.BootstrapServers = options.BootstrapServers;
        config.ClientId = options.ClientId;
        config.SecurityProtocol = options.SecurityProtocol;
        config.SslCaLocation = options.SslCaLocation;
        if (options.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = options.SaslMechanism;
            config.SaslUsername = options.Username;
            config.SaslPassword = options.Password;
        }
    }
}
