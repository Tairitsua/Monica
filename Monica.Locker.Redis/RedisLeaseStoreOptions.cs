namespace Monica.Locker.Redis;

/// <summary>Configures an isolated Redis lease authority. Connections are supplied and owned by the host.</summary>
public sealed record RedisLeaseStoreOptions
{
    /// <summary>Required deployment namespace, encoded literally into every key; no glob or hash-tag interpretation.</summary>
    public required string KeyPrefix { get; init; }
    /// <summary>Redis database, zero by default. Redis Cluster requires zero.</summary>
    public int Database { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyPrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(Database);
    }
}
