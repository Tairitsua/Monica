using System.Text;
using System.Runtime.CompilerServices;
using Monica.Locker.Leases;
using StackExchange.Redis;

namespace Monica.Locker.Redis;

/// <summary>Coordinates low-cardinality roles using single-key Lua operations and Redis server time.</summary>
/// <remarks>
/// Every acquisition creates a random term; renewal never recreates a missing key. Operations use the primary.
/// Cancellation stops waiting, not a command already sent to Redis; ambiguous writes are left to expire. Replication
/// failover may lose acknowledged grants, so downstream effects require idempotency or their own fencing.
/// Snapshots scan primary nodes and are observational, not an atomic cluster-wide view. Redis 2.8+ SCAN is required.
/// The caller owns the connection and its operation timeouts; this provider creates no connections or timers.
/// </remarks>
public sealed class RedisLeaseStore : IExclusiveLeaseStore, ILeaseAdministration
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly string _prefix;

    /// <summary>Uses an existing singleton connection and validates the immutable namespace/database settings.</summary>
    public RedisLeaseStore(IConnectionMultiplexer connection, RedisLeaseStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _connection = connection;
        _database = connection.GetDatabase(options.Database);
        _prefix = $"monica:lease:{Encode(options.KeyPrefix)}:";
    }

    /// <inheritdoc />
    public async Task<LeaseGrant?> TryAcquireAsync(LeaseResource resource, string holderId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        if (holderId.Length > 256) throw new ArgumentOutOfRangeException(nameof(holderId));
        var milliseconds = DurationMilliseconds(duration);
        var identity = new LeaseIdentity(resource, holderId, Guid.NewGuid().ToString("N"));
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _database.ScriptEvaluateAsync(AcquireScript, [Key(resource)],
            [holderId, identity.TermId, milliseconds, resource.Name], CommandFlags.DemandMaster)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return Grant(identity, result, milliseconds);
    }

    /// <inheritdoc />
    public async Task<LeaseGrant?> TryRenewAsync(LeaseIdentity identity, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var milliseconds = DurationMilliseconds(duration);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _database.ScriptEvaluateAsync(RenewScript, [Key(identity.Resource)],
            [identity.HolderId, identity.TermId, milliseconds], CommandFlags.DemandMaster)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return Grant(identity, result, milliseconds);
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(LeaseIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        return (long)await _database.ScriptEvaluateAsync(ReleaseScript, [Key(identity.Resource)],
            [identity.HolderId, identity.TermId], CommandFlags.DemandMaster)
            .WaitAsync(cancellationToken).ConfigureAwait(false) != 0;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(LeaseResource resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.KeyDeleteAsync(Key(resource), CommandFlags.DemandMaster)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LeaseSnapshot> GetSnapshotAsync(string scope, string namePrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(namePrefix);
        cancellationToken.ThrowIfCancellationRequested();
        var observations = new List<LeaseObservation>();
        var seen = new HashSet<RedisKey>();
        DateTimeOffset? observedAt = null;
        var servers = _connection.GetEndPoints().Select(endpoint => _connection.GetServer(endpoint))
            .Where(server => server.IsConnected && !server.IsReplica).ToArray();
        if (servers.Length == 0) throw new RedisConnectionException(ConnectionFailureType.UnableToResolvePhysicalConnection,
            "No connected Redis primary is available for lease observation.");
        foreach (var server in servers)
        {
            if (!server.Features.Scan) throw new NotSupportedException("Lease snapshots require Redis SCAN.");
            observedAt ??= new DateTimeOffset(DateTime.SpecifyKind(
                await server.TimeAsync().WaitAsync(cancellationToken).ConfigureAwait(false), DateTimeKind.Utc));
            await foreach (var key in ScanAsync(server, _prefix + Encode(scope) + ":*", cancellationToken).ConfigureAwait(false))
            {
                if (!seen.Add(key)) continue;
                var result = await _database.ScriptEvaluateAsync(ObserveScript, [key], flags: CommandFlags.DemandMaster)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsNull) continue;
                var values = (RedisResult[])result!;
                var name = (string)values[0]!;
                if (!name.StartsWith(namePrefix, StringComparison.Ordinal)) continue;
                observations.Add(new LeaseObservation(new LeaseResource(scope, name), (string)values[1]!,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)values[2]),
                    DateTimeOffset.FromUnixTimeMilliseconds((long)values[3]),
                    DateTimeOffset.FromUnixTimeMilliseconds((long)values[4] + (long)values[5])));
            }
        }
        return new LeaseSnapshot(observedAt!.Value, observations);
    }

    private async IAsyncEnumerable<RedisKey> ScanAsync(IServer server, string pattern,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // IServer.KeysAsync can fall back to KEYS when SCAN is disabled in the client's command map.
        // Explicit SCAN preserves bounded server work and fails rather than making that fallback.
        var cursor = "0";
        do
        {
            var result = await server.ExecuteAsync(_database.Database, "SCAN",
                [cursor, "MATCH", pattern, "COUNT", 128], CommandFlags.DemandMaster)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var page = (RedisResult[])result!;
            cursor = (string)page[0]!;
            foreach (var key in (RedisResult[])page[1]!) yield return (string)key!;
        } while (cursor != "0");
    }

    private RedisKey Key(LeaseResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _prefix + Encode(resource.Scope) + ":" + Encode(resource.Name);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long DurationMilliseconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive and at most one day.");
        return (long)Math.Ceiling(duration.TotalMilliseconds);
    }

    private static LeaseGrant? Grant(LeaseIdentity identity, RedisResult result, long milliseconds) => result.IsNull
        ? null : new LeaseGrant(identity, TimeSpan.FromMilliseconds(milliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds((long)result));

    private const string ClockScript = """
        local clock = redis.call('TIME')
        local now = clock[1] * 1000 + math.floor(clock[2] / 1000)

        """;
    private const string AcquireScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 then return nil end

        """ + ClockScript + """
        redis.call('HSET', KEYS[1], 'holder', ARGV[1], 'term', ARGV[2], 'acquired', now, 'confirmed', now, 'name', ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return now
        """;
    private const string MatchScript = """
        if redis.call('PTTL', KEYS[1]) <= 0 or redis.call('HGET', KEYS[1], 'holder') ~= ARGV[1]
            or redis.call('HGET', KEYS[1], 'term') ~= ARGV[2] then return nil end

        """;
    private const string RenewScript = MatchScript + ClockScript + """
        redis.call('HSET', KEYS[1], 'confirmed', now)
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return now
        """;
    private const string ReleaseScript = """
        if redis.call('HGET', KEYS[1], 'holder') ~= ARGV[1]
            or redis.call('HGET', KEYS[1], 'term') ~= ARGV[2] then return 0 end
        return redis.call('DEL', KEYS[1])
        """;
    private const string ObserveScript = ClockScript + """
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl <= 0 then return nil end
        return {redis.call('HGET', KEYS[1], 'name'), redis.call('HGET', KEYS[1], 'holder'),
            redis.call('HGET', KEYS[1], 'acquired'), redis.call('HGET', KEYS[1], 'confirmed'), now, ttl}
        """;
}
