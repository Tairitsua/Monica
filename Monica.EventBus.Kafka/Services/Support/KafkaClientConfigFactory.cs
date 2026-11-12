using Confluent.Kafka;
using Monica.EventBus.Kafka.Models;
using Monica.Modules;

namespace Monica.EventBus.Kafka.Services.Support;

/// <summary>
/// Builds Confluent Kafka client configuration from Monica Kafka cluster models.
/// </summary>
internal static class KafkaClientConfigFactory
{
    private const int MINIMUM_KAFKA_TIMEOUT_MS = 1_000;

    public static AdminClientConfig BuildAdminConfig(KafkaClusterConfig cluster, ModuleEventBusKafkaOption option)
    {
        var config = new AdminClientConfig();
        ApplyCommon(config, cluster, option);
        return config;
    }

    public static ProducerConfig BuildProducerConfig(KafkaClusterConfig cluster, ModuleEventBusKafkaOption option)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(option.ProducerMessageMaxBytes, 1);
        var config = new ProducerConfig
        {
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageMaxBytes = option.ProducerMessageMaxBytes,
            MessageTimeoutMs = RequireTimeoutMilliseconds(option.ProducerDeliveryTimeout,
                nameof(option.ProducerDeliveryTimeout))
        };
        ApplyCommon(config, cluster, option);
        return config;
    }

    public static ConsumerConfig BuildConsumerConfig(KafkaClusterConfig cluster, ModuleEventBusKafkaOption option,
        string? serviceKey, string? workloadOwner = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(option.ConsumerFetchMessageMaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(option.ConsumerFetchMaxBytes, 1);
        if (!Enum.IsDefined(option.AutoOffsetReset))
        {
            throw new ArgumentOutOfRangeException(nameof(option.AutoOffsetReset), option.AutoOffsetReset,
                "The native Kafka offset reset policy is invalid.");
        }

        var sessionTimeoutMs = RequireTimeoutMilliseconds(option.ConsumerSessionTimeout,
            nameof(option.ConsumerSessionTimeout));
        var maxPollIntervalMs = RequireTimeoutMilliseconds(option.ConsumerMaxPollInterval,
            nameof(option.ConsumerMaxPollInterval));
        if (maxPollIntervalMs < sessionTimeoutMs)
        {
            throw new ArgumentException("The Kafka consumer maximum poll interval must be at least its session timeout.",
                nameof(option.ConsumerMaxPollInterval));
        }

        var config = new ConsumerConfig
        {
            GroupId = BuildConsumerGroupId(option, serviceKey, workloadOwner),
            AutoOffsetReset = option.AutoOffsetReset,
            MaxPartitionFetchBytes = option.ConsumerFetchMessageMaxBytes,
            FetchMaxBytes = option.ConsumerFetchMaxBytes,
            SessionTimeoutMs = sessionTimeoutMs,
            MaxPollIntervalMs = maxPollIntervalMs,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };
        ApplyCommon(config, cluster, option);
        return config;
    }

    public static ConsumerConfig BuildReadOnlyConsumerConfig(KafkaClusterConfig cluster, ModuleEventBusKafkaOption option, string serviceKey)
    {
        var config = new ConsumerConfig
        {
            GroupId = $"{BuildConsumerGroupId(option, serviceKey)}-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = false
        };
        ApplyCommon(config, cluster, option);
        return config;
    }

    public static ConsumerConfig BuildOffsetQueryConsumerConfig(
        KafkaClusterConfig cluster,
        ModuleEventBusKafkaOption option,
        string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var config = new ConsumerConfig
        {
            GroupId = groupId.Trim(),
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = false
        };
        ApplyCommon(config, cluster, option);
        return config;
    }

    private static void ApplyCommon(ClientConfig config, KafkaClusterConfig cluster, ModuleEventBusKafkaOption option)
    {
        var normalized = cluster.Clone().Normalize();
        if (string.IsNullOrWhiteSpace(normalized.BootstrapServers))
        {
            throw new InvalidOperationException($"Kafka cluster '{normalized.ClusterId}' does not define bootstrap servers.");
        }

        config.BootstrapServers = normalized.BootstrapServers;
        config.ClientId = normalized.ClientId ?? option.ClientId;
        var requestTimeoutMs = NormalizeTimeoutMilliseconds(option.AdminRequestTimeout);
        config.SocketTimeoutMs = requestTimeoutMs;
        config.SocketConnectionSetupTimeoutMs = Math.Min(
            requestTimeoutMs,
            NormalizeTimeoutMilliseconds(option.ConnectionSetupTimeout));

        if (TryParseEnum(normalized.SecurityProtocol, nameof(KafkaClusterConfig.SecurityProtocol), out SecurityProtocol securityProtocol))
        {
            config.SecurityProtocol = securityProtocol;
        }

        if (TryParseEnum(normalized.SaslMechanism, nameof(KafkaClusterConfig.SaslMechanism), out SaslMechanism saslMechanism))
        {
            config.SaslMechanism = saslMechanism;
        }

        if (!string.IsNullOrWhiteSpace(normalized.SaslUsername))
        {
            config.SaslUsername = normalized.SaslUsername;
        }

        if (!string.IsNullOrEmpty(normalized.SaslPassword))
        {
            config.SaslPassword = normalized.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(normalized.SslCaLocation))
        {
            config.SslCaLocation = normalized.SslCaLocation;
        }
    }

    internal static string BuildConsumerGroupId(ModuleEventBusKafkaOption option, string? serviceKey,
        string? workloadOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option.ConsumerGroupId);
        var group = string.IsNullOrWhiteSpace(workloadOwner)
            ? $"{option.ConsumerGroupId.Trim()}.default"
            : $"{option.ConsumerGroupId.Trim()}.owner.{EscapeGroupSegment(workloadOwner)}";
        return string.IsNullOrWhiteSpace(serviceKey)
            ? group
            : $"{group}.bus.{EscapeGroupSegment(serviceKey)}";
    }

    private static string EscapeGroupSegment(string value)
        => Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);

    internal static int NormalizeTimeoutMilliseconds(TimeSpan timeout)
    {
        return (int)Math.Clamp(
            timeout.TotalMilliseconds,
            MINIMUM_KAFKA_TIMEOUT_MS,
            int.MaxValue);
    }

    private static int RequireTimeoutMilliseconds(TimeSpan timeout, string parameterName)
    {
        if (timeout.TotalMilliseconds is < MINIMUM_KAFKA_TIMEOUT_MS or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, timeout,
                "Kafka delivery and consumer timeouts must be between one second and Int32.MaxValue milliseconds.");
        }

        return (int)timeout.TotalMilliseconds;
    }

    private static bool TryParseEnum<TEnum>(string? value, string propertyName, out TEnum result) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        if (Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Kafka cluster {propertyName} value '{value}' is invalid. Supported values are: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
