namespace Monica.EventBus.Kafka.Models;

/// <summary>
/// Developer or runtime supplied Kafka cluster connection configuration.
/// </summary>
public sealed class KafkaClusterConfig
{
    /// <summary>
    /// Stable cluster identifier used by UI state, APIs, and persisted snapshots.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable cluster name displayed in the Kafka console.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Kafka bootstrap server list in the standard host:port comma-separated format.
    /// </summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>
    /// Optional Kafka client identifier used by admin, producer, and consumer clients.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Optional Confluent Kafka security protocol name, such as Plaintext, SaslPlaintext, SaslSsl, or Ssl.
    /// </summary>
    public string? SecurityProtocol { get; set; }

    /// <summary>
    /// Optional SASL mechanism name, such as Plain, ScramSha256, or ScramSha512.
    /// </summary>
    public string? SaslMechanism { get; set; }

    /// <summary>
    /// Optional SASL user name. Leave empty when credentials are managed outside Monica, such as in Dapr.
    /// </summary>
    public string? SaslUsername { get; set; }

    /// <summary>
    /// Optional SASL password. Leave empty when credentials are managed outside Monica, such as in Dapr.
    /// Nonempty passwords retain their exact value, including leading, trailing, or embedded whitespace.
    /// </summary>
    public string? SaslPassword { get; set; }

    /// <summary>
    /// Optional CA certificate path used by SSL-enabled Kafka clusters.
    /// </summary>
    public string? SslCaLocation { get; set; }

    /// <summary>
    /// Optional JMX endpoint used for host-level metrics when available.
    /// </summary>
    public string? JmxEndpoint { get; set; }

    /// <summary>
    /// Dapr pub/sub component name when this cluster is backing a Dapr EventBus provider.
    /// </summary>
    public string? DaprPubSubName { get; set; }

    /// <summary>
    /// Whether this cluster is declared as the backing broker for a Dapr pub/sub component.
    /// </summary>
    public bool IsDaprBacked { get; set; }

    /// <summary>
    /// Whether broker credentials are intentionally stored outside Monica.
    /// </summary>
    public bool CredentialsManagedExternally { get; set; }

    /// <summary>
    /// UTC creation time for runtime-created cluster records.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// UTC update time for runtime-created cluster records.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns whether Monica has enough broker details to perform direct Kafka admin operations.
    /// </summary>
    public bool HasDirectKafkaAccess => !string.IsNullOrWhiteSpace(BootstrapServers) && !CredentialsManagedExternally;

    /// <summary>
    /// Normalizes defaults and trims user-provided values.
    /// </summary>
    /// <returns>The current configuration instance.</returns>
    public KafkaClusterConfig Normalize()
    {
        ClusterId = string.IsNullOrWhiteSpace(ClusterId)
            ? BuildClusterId(DisplayName, BootstrapServers)
            : ClusterId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? ClusterId : DisplayName.Trim();
        BootstrapServers = NormalizeRequired(BootstrapServers);
        ClientId = NormalizeOptional(ClientId);
        SecurityProtocol = NormalizeOptional(SecurityProtocol);
        SaslMechanism = NormalizeOptional(SaslMechanism);
        SaslUsername = NormalizeOptional(SaslUsername);
        SaslPassword = string.IsNullOrEmpty(SaslPassword) ? null : SaslPassword;
        SslCaLocation = NormalizeOptional(SslCaLocation);
        JmxEndpoint = NormalizeOptional(JmxEndpoint);
        DaprPubSubName = NormalizeOptional(DaprPubSubName);
        return this;
    }

    /// <summary>
    /// Creates a detached copy safe for persistence or UI mutation.
    /// </summary>
    public KafkaClusterConfig Clone()
    {
        return new KafkaClusterConfig
        {
            ClusterId = ClusterId,
            DisplayName = DisplayName,
            BootstrapServers = BootstrapServers,
            ClientId = ClientId,
            SecurityProtocol = SecurityProtocol,
            SaslMechanism = SaslMechanism,
            SaslUsername = SaslUsername,
            SaslPassword = SaslPassword,
            SslCaLocation = SslCaLocation,
            JmxEndpoint = JmxEndpoint,
            DaprPubSubName = DaprPubSubName,
            IsDaprBacked = IsDaprBacked,
            CredentialsManagedExternally = CredentialsManagedExternally,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    private static string BuildClusterId(string displayName, string bootstrapServers)
    {
        var source = !string.IsNullOrWhiteSpace(displayName) ? displayName : bootstrapServers;
        var normalized = new string(NormalizeRequired(source)
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}

/// <summary>
/// Request used to create or update a Kafka cluster from the console.
/// </summary>
public sealed class KafkaClusterUpsertRequest
{
    /// <summary>
    /// Cluster configuration to save.
    /// </summary>
    public KafkaClusterConfig Cluster { get; set; } = new();
}
