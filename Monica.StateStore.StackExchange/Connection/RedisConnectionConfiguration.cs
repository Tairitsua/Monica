namespace Monica.StateStore.StackExchange.Connection;

/// <summary>
/// Connection settings for standalone Redis, Sentinel discovery, and Redis Cluster.
/// In Sentinel mode, <see cref="Host"/>, <see cref="Port"/>, and <see cref="AdditionalEndpoints"/>
/// identify Sentinel servers; data-server addresses are discovered from <see cref="ServiceName"/>.
/// </summary>
public class RedisConnectionConfiguration
{
    /// <summary>
    /// Primary endpoint host (default: localhost)
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Primary endpoint port (default: 6379)
    /// </summary>
    public int Port { get; set; } = 6379;

    /// <summary>
    /// ACL username for Redis data servers. Null uses the server's default user.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Password for Redis data servers. Null connects without a password.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Additional seed endpoints in every connection mode, in host:port or [IPv6]:port format.
    /// Sentinel mode interprets every seed as a Sentinel server.
    /// </summary>
    public List<string> AdditionalEndpoints { get; set; } = [];

    /// <summary>
    /// Sentinel-specific: master service name (default: "mymaster")
    /// </summary>
    public string ServiceName { get; set; } = "mymaster";

    /// <summary>
    /// ACL username for Sentinel servers. Independent of <see cref="User"/>; null uses their default user.
    /// </summary>
    public string? SentinelUser { get; set; }

    /// <summary>
    /// Password for Sentinel servers. Independent of <see cref="Password"/>; null connects without a password.
    /// </summary>
    public string? SentinelPassword { get; set; }

    /// <summary>
    /// Connection timeout in milliseconds (default: 5000)
    /// </summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>
    /// Sync operation timeout in milliseconds (default: 5000)
    /// </summary>
    public int SyncTimeout { get; set; } = 5000;

    /// <summary>
    /// Asynchronous operation timeout in milliseconds. Null inherits <see cref="SyncTimeout"/>.
    /// </summary>
    public int? AsyncTimeout { get; set; }

    /// <summary>
    /// Number of retry attempts on connection failure (default: 3)
    /// </summary>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>
    /// Whether to abort on connection failure (default: false for resilience)
    /// </summary>
    public bool AbortOnConnectFail { get; set; } = false;

    /// <summary>
    /// Allows privileged administrative commands. Defaults to false; ordinary state-store operations do not require it.
    /// </summary>
    public bool AllowAdmin { get; set; } = false;

    /// <summary>
    /// Enables TLS with normal certificate validation. Defaults to false.
    /// In Sentinel mode, this applies to both Sentinel and discovered data servers.
    /// </summary>
    public bool Ssl { get; set; } = false;

    /// <summary>
    /// Expected TLS certificate host. Null lets StackExchange.Redis infer it from the endpoint.
    /// In Sentinel mode, the same certificate host applies to discovery and data connections.
    /// </summary>
    public string? SslHost { get; set; }
}
