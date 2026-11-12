using System.Text.Json.Serialization;
using Confluent.Kafka;
using Monica.DataChannel.Abstractions.Communication;

namespace Monica.DataChannel.Providers.Kafka;

/// <summary>Configures one host-owned Kafka endpoint and its bounded, explicitly acknowledged input.</summary>
public class KafkaOptions : CommunicationOptions<KafkaEndpoint>
{
    /// <summary>Gets or sets the required comma-separated broker endpoints.</summary>
    public required string BootstrapServers { get; set; }
    /// <summary>Gets or sets the required topic for an active direction.</summary>
    public string? Topic { get; set; }
    /// <summary>Gets or sets the stable group shared by replicas of the same logical input.</summary>
    public string ConsumerGroupId { get; set; } = nameof(KafkaOptions);
    /// <summary>Gets or sets the diagnostic client ID. It does not isolate consumer offsets.</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Gets or sets security. Defaults to SASL plaintext; local plaintext requires explicit selection.</summary>
    public SecurityProtocol SecurityProtocol { get; set; } = SecurityProtocol.SaslPlaintext;
    /// <summary>Gets or sets the SASL mechanism. Defaults to SCRAM-SHA-512.</summary>
    public SaslMechanism SaslMechanism { get; set; } = SaslMechanism.ScramSha512;
    /// <summary>Gets or sets the SASL user. Excluded from management JSON.</summary>
    [JsonIgnore]
    public string? Username { get; set; }
    /// <summary>Gets or sets the exact SASL password, including spaces. Excluded from management JSON.</summary>
    [JsonIgnore]
    public string? Password { get; set; }
    /// <summary>Gets or sets the native CA file location for TLS certificate validation.</summary>
    public string? SslCaLocation { get; set; }
    /// <summary>Gets or sets missing/out-of-range offset behavior. Defaults to Error; no implicit backlog skip.</summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Error;
    /// <summary>Must remain false. This endpoint commits only successfully completed deliveries.</summary>
    public bool EnableAutoCommit { get; set; }
    /// <summary>Gets or sets input decoding. UTF-8 text is the default; RawBytes retains tombstones and empty bytes.</summary>
    public KafkaInputPayloadFormat InputPayloadFormat { get; set; } = KafkaInputPayloadFormat.Utf8String;
    /// <summary>Gets or sets the maximum concurrent partition operations. Defaults to four.</summary>
    public int MaxConcurrentPartitions { get; set; } = 4;
    /// <summary>Gets or sets the maximum retained records, including active/retired workers. Defaults to 64.</summary>
    public int MaxBufferedMessages { get; set; } = 64;
    /// <summary>Gets or sets processing attempts per record before visibly blocking its partition. Defaults to three.</summary>
    public int ProcessingMaxAttempts { get; set; } = 3;
    /// <summary>Gets or sets processing and commit retry spacing. Defaults to one second; polling continues.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Gets or sets the native maximum interval between polls. Defaults to five minutes.</summary>
    public TimeSpan ConsumerMaxPollInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets the native session timeout. Defaults to 45 seconds.</summary>
    public TimeSpan ConsumerSessionTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Gets or sets the native per-partition fetch target. Defaults to 1 MiB; Kafka may return a larger first batch.</summary>
    public int ConsumerFetchMessageMaxBytes { get; set; } = 1024 * 1024;
    /// <summary>Gets or sets the native fetch-response target. Defaults to 50 MiB.</summary>
    public int ConsumerFetchMaxBytes { get; set; } = 50 * 1024 * 1024;
    /// <summary>Gets or sets the native receive queue target in KiB. Defaults to 64 MiB.</summary>
    public int ConsumerQueuedMaxMessagesKbytes { get; set; } = 64 * 1024;
    /// <summary>Gets or sets the native producer message limit, including protocol overhead. Defaults to 1,000,012 bytes.</summary>
    public int ProducerMessageMaxBytes { get; set; } = 1_000_012;
    /// <summary>Gets or sets the native delivery deadline. Defaults to two minutes.</summary>
    public TimeSpan ProducerDeliveryTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Gets or sets the producer shutdown flush deadline. Defaults to ten seconds; outstanding messages fail shutdown.</summary>
    public TimeSpan ProducerShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Gets or sets an optional application-owned partition mapping. Null keeps the native default.</summary>
    /// <remarks>The delegate must be thread-safe, deterministic, and free of blocking work. It receives serialized key bytes.</remarks>
    [JsonIgnore]
    public PartitionerDelegate? Partitioner { get; set; }

    /// <summary>Creates input options by default.</summary>
    public KafkaOptions(ConnectionDirection direction = ConnectionDirection.Input)
    {
        Type = CommunicationType.MQ;
        Direction = direction;
    }

    /// <inheritdoc />
    public override void EnrichOrValidate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BootstrapServers);
        if (!Enum.IsDefined(Direction) || !Enum.IsDefined(SecurityProtocol) || !Enum.IsDefined(SaslMechanism)
            || !Enum.IsDefined(AutoOffsetReset) || !Enum.IsDefined(InputPayloadFormat))
            throw new ArgumentException("Kafka options contain an unknown enum value.");
        if (Direction != ConnectionDirection.None) ArgumentException.ThrowIfNullOrWhiteSpace(Topic);
        if (Direction is ConnectionDirection.Input or ConnectionDirection.InputAndOutput)
            ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroupId);
        if (EnableAutoCommit) throw new ArgumentException("Kafka DataChannel requires explicit commits after successful processing.");
        if (SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Username);
            if (string.IsNullOrEmpty(Password)) throw new ArgumentException("Kafka SASL requires a password.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentPartitions);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBufferedMessages, MaxConcurrentPartitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProcessingMaxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProducerMessageMaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConsumerFetchMessageMaxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConsumerFetchMaxBytes, ConsumerFetchMessageMaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConsumerQueuedMaxMessagesKbytes);
        ValidateTimeout(RetryDelay, nameof(RetryDelay));
        ValidateTimeout(ConsumerMaxPollInterval, nameof(ConsumerMaxPollInterval));
        ValidateTimeout(ConsumerSessionTimeout, nameof(ConsumerSessionTimeout));
        ValidateTimeout(ProducerDeliveryTimeout, nameof(ProducerDeliveryTimeout));
        ValidateTimeout(ProducerShutdownTimeout, nameof(ProducerShutdownTimeout));
        if (ConsumerSessionTimeout >= ConsumerMaxPollInterval)
            throw new ArgumentException("Kafka session timeout must be shorter than its maximum poll interval.");
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value.TotalMilliseconds is < 1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(name, "Kafka timeouts must be positive native integer milliseconds.");
    }
}

/// <summary>Controls only Kafka input decoding; producer payloads remain explicit strings or bytes.</summary>
public enum KafkaInputPayloadFormat
{
    /// <summary>Decode non-null values as strict UTF-8, retaining empty strings and null tombstones.</summary>
    Utf8String,
    /// <summary>Deliver detached byte arrays, retaining null tombstones and empty arrays.</summary>
    RawBytes
}
