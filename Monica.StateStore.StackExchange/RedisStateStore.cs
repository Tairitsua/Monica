using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Modules;
using Monica.StateStore.Abstractions;
using Monica.StateStore.Models;
using Monica.StateStore.Queries;
using Monica.StateStore.StackExchange.Scripts;
using StackExchange.Redis;

namespace Monica.StateStore.StackExchange;

/// <summary>
/// Redis state store implementation based on StackExchange.Redis.
/// Uses Hash storage format with fields: data, version
/// ETag = version.ToString() (increments on each save)
/// TTL is set atomically within Lua scripts with millisecond precision.
/// </summary>
/// <remarks>
/// Typed values use the immutable JSON contract owned by the current Monica host. Changing that host contract also
/// changes the persisted Redis representation and must be coordinated with existing state.
/// The host owns the injected connection and its disposal. A null TTL uses the configured default; zero makes the
/// entry permanent, including when replacing an expiring entry. Conditional operations require the exact stored ETag;
/// an empty ETag or <c>"0"</c> is not an unconditional-write or deletion token.
/// </remarks>
public class RedisStateStore(
    IConnectionMultiplexer connection,
    IOptions<ModuleRedisStateStoreOption> options,
    IJsonSerializerOptionsProvider serializerOptionsProvider,
    ILogger<RedisStateStore> logger) : DistributedStateStoreBase(logger), IStateStoreKeyTtlReader
{
    private readonly IConnectionMultiplexer _connection = connection;
    private readonly ModuleRedisStateStoreOption _option = options.Value;
    private readonly IDatabase _database = connection.GetDatabase(options.Value.DatabaseIndex);
    private readonly JsonSerializerOptions _serializerOptions = serializerOptionsProvider.SerializerOptions;

    /// <summary>
    /// Get the final Redis key with optional KeyPrefix configuration
    /// </summary>
    private string GetRedisKey(string key)
    {
        return string.IsNullOrEmpty(_option.KeyPrefix) ? key : $"{_option.KeyPrefix}:{key}";
    }

    /// <summary>
    /// Strip the KeyPrefix from a Redis key
    /// </summary>
    private string StripRedisKeyPrefix(string redisKey)
    {
        if (string.IsNullOrEmpty(_option.KeyPrefix))
            return redisKey;

        var prefixWithColon = $"{_option.KeyPrefix}:";
        return redisKey.StartsWith(prefixWithColon, StringComparison.Ordinal)
            ? redisKey[prefixWithColon.Length..]
            : redisKey;
    }

    public override async Task<List<string>> ScanKeysAsync(string pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var servers = _connection.GetServers()
                .Where(server => !server.IsReplica && server.ServerType != ServerType.Sentinel)
                .ToArray();
            if (servers.Length == 0 || servers.Any(server => !server.IsConnected))
            {
                throw new InvalidOperationException("All Redis primary servers must be connected to scan state keys.");
            }

            // The namespace is literal; only the caller-supplied pattern contains Redis glob operators.
            var redisPattern = string.IsNullOrEmpty(_option.KeyPrefix)
                ? pattern
                : $"{EscapeGlobPattern(_option.KeyPrefix)}:{pattern}";
            foreach (var server in servers)
            {
                await foreach (var key in server.KeysAsync(
                    database: _option.DatabaseIndex,
                    pattern: redisPattern).WithCancellation(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    keys.Add(StripRedisKeyPrefix(key.ToString()));
                }
            }

            Logger.LogDebug("Found {Count} keys matching pattern: {Pattern}", keys.Count, pattern);
            return keys.ToList();
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("ScanKeysAsync was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR scanning keys with pattern {0}", pattern);
        }
    }

    private static string EscapeGlobPattern(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    public override Task<Dictionary<string, T?>> QueryStateAsync<T>(
        Func<QueryBuilder<T>, IFinishedQueryBuilder<T>> query,
        CancellationToken cancellationToken = default) where T : class
    {
        throw new NotImplementedException(
            "QueryStateAsync is not supported in RedisStateStore. " +
            "Redis does not have native query capabilities. Consider using DaprStateStore with a queryable backend.");
    }

    public override async Task<Dictionary<string, string>> GetRawBulkStateAsync(
        IReadOnlyList<string> keys,
        bool removeEmptyValue = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (keys.Count == 0) return new Dictionary<string, string>();

        try
        {
            // Use batch/pipeline for efficient Hash field retrieval
            var batch = _database.CreateBatch();
            var tasks = keys.Select(k => batch.HashGetAsync(GetRedisKey(k), "data")).ToList();
            batch.Execute();
            var values = await Task.WhenAll(tasks);

            var result = new Dictionary<string, string>();
            for (int i = 0; i < keys.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var value = values[i];
                if (!value.HasValue && removeEmptyValue)
                    continue;

                if (value.HasValue)
                {
                    result[keys[i]] = value.ToString();
                }
                else if (!removeEmptyValue)
                {
                    result[keys[i]] = string.Empty;
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting bulk state with keys: {0}", string.Join(", ", keys));
        }
    }

    public override async Task<Dictionary<string, T?>> GetBulkStateAsync<T>(
        IReadOnlyList<string> keys,
        bool removeEmptyValue = true,
        CancellationToken cancellationToken = default) where T : default
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (keys.Count == 0) return new Dictionary<string, T?>();

        try
        {
            // Use batch/pipeline for efficient Hash field retrieval
            var batch = _database.CreateBatch();
            var tasks = keys.Select(k => batch.HashGetAsync(GetRedisKey(k), "data")).ToList();
            batch.Execute();
            var values = await Task.WhenAll(tasks);

            var result = new Dictionary<string, T?>();
            for (int i = 0; i < keys.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var value = values[i];
                if (!value.HasValue && removeEmptyValue)
                    continue;

                if (value.HasValue)
                {
                    result[keys[i]] = JsonSerializer.Deserialize<T>(value.ToString(), _serializerOptions);
                }
                else
                {
                    result[keys[i]] = default;
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting bulk state with keys: {0}", string.Join(", ", keys));
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ExistAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await _database.HashExistsAsync(GetRedisKey(key), "data");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR checking state existence with key: {0}", key);
        }
    }

    public override async Task<T?> GetStateAsync<T>(string key, CancellationToken cancellationToken = default) where T : default
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var data = await _database.HashGetAsync(redisKey, "data");
            if (!data.HasValue)
                return default;

            return JsonSerializer.Deserialize<T>(data.ToString(), _serializerOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting state with key: {0}", key);
        }
    }

    public override async Task<string?> GetRawStateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var data = await _database.HashGetAsync(redisKey, "data");
            if (!data.HasValue)
                return null;

            return data.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting state with key: {0}", key);
        }
    }

    public async Task<StateStoreKeyTtlSnapshot> GetKeyTtlAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var result = await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetKeyTtlScript(), new { key = (RedisKey)redisKey });
            var milliseconds = (long)result;
            return milliseconds switch
            {
                -2 => throw new KeyNotFoundException($"Key not found: {key}"),
                -1 => StateStoreKeyTtlSnapshot.Permanent,
                _ => StateStoreKeyTtlSnapshot.FromRemaining(TimeSpan.FromMilliseconds(milliseconds))
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting TTL for key: {0}", key);
        }
    }

    public override async Task SaveStateAsync<T>(string key, T value, CancellationToken cancellationToken = default, TimeSpan? ttl = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var json = JsonSerializer.Serialize(value, _serializerOptions);
            var ttlMilliseconds = BuildTtlMilliseconds(ttl);

            // Execute Lua script with TTL included
            await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetSaveStateScript(),
                new
                {
                    key = (RedisKey)redisKey,
                    data = json,
                    ttl = ttlMilliseconds
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR saving state with key: {0}", key);
        }
    }

    public override async Task DeleteStateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            await _database.KeyDeleteAsync(redisKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR deleting state with key: {0}", key);
        }
    }

    public override async Task DeleteBulkStateAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0) return;

        try
        {
            // Individual commands can route to different cluster slots; a multi-key DEL cannot.
            var batch = _database.CreateBatch();
            var tasks = keys.Select(key => batch.KeyDeleteAsync(GetRedisKey(key))).ToArray();
            batch.Execute();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR deleting bulk state with keys: {0}", string.Join(", ", keys));
        }
    }

    public override async Task<(T? Value, string ETag)> GetStateAndETagAsync<T>(string key, CancellationToken cancellationToken = default) where T : default
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var result = await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetGetWithETagScript(),
                new { key = (RedisKey)redisKey });

            var results = (RedisResult[])result!;
            var dataJson = (string?)results[0];
            var etag = (string)results[1]!;

            if (dataJson == null)
                return (default, string.Empty);

            var deserialized = JsonSerializer.Deserialize<T>(dataJson, _serializerOptions);
            return (deserialized, etag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR getting state and ETag with key: {0}", key);
        }
    }

    public override async Task<(bool Success, string? NewETag)> TrySaveStateWithETagAsync<T>(
        string key,
        T value,
        string expectedETag,
        CancellationToken cancellationToken = default,
        TimeSpan? ttl = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var json = JsonSerializer.Serialize(value, _serializerOptions);
            var ttlMilliseconds = BuildTtlMilliseconds(ttl);

            // Execute CAS script with TTL included
            var result = await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetCompareAndSwapScript(),
                new
                {
                    key = (RedisKey)redisKey,
                    etag = expectedETag,
                    data = json,
                    ttl = ttlMilliseconds
                });

            if (result.IsNull)
            {
                Logger.LogDebug("ETag mismatch for key: {Key}. Expected: {Expected}", key, expectedETag);
                return (false, null);
            }

            var newETag = (string)result!;
            return (true, newETag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR saving state with ETag for key: {0}", key);
        }
    }

    public override async Task<bool> TrySaveStateIfNotExistsAsync<T>(
        string key,
        T value,
        CancellationToken cancellationToken = default,
        TimeSpan? ttl = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var dataJson = JsonSerializer.Serialize(value, _serializerOptions);
            var ttlMilliseconds = BuildTtlMilliseconds(ttl);

            // Use atomic Lua script for first-write with TTL included
            var result = await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetSaveIfNotExistsScript(),
                new
                {
                    key = (RedisKey)redisKey,
                    data = dataJson,
                    ttl = ttlMilliseconds
                });

            var success = (int)result == 1;

            if (success)
            {
                Logger.LogDebug("Saved state (if not exists) with key: {Key}", key);
            }
            else
            {
                Logger.LogDebug("Key already exists, cannot save: {Key}", key);
            }

            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR saving state if not exists for key: {0}", key);
        }
    }

    private long BuildTtlMilliseconds(TimeSpan? ttl)
    {
        var expiry = ttl ?? _option.DefaultTTL;
        if (expiry is null || expiry == TimeSpan.Zero)
        {
            return 0;
        }

        if (expiry < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL cannot be smaller than zero.");
        }

        // Round up so a positive duration never becomes permanent or expires earlier through truncation.
        var ticks = expiry.Value.Ticks;
        return ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
    }

    public override async Task SaveBulkStateAsync<T>(
        IReadOnlyList<(string Key, T Value)> items,
        CancellationToken cancellationToken = default,
        TimeSpan? ttl = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0) return;

        try
        {
            var ttlMilliseconds = BuildTtlMilliseconds(ttl);

            // Use batch/pipeline for better performance
            var batch = _database.CreateBatch();
            var saveTasks = new List<Task>(items.Count);

            // Execute Lua scripts with TTL included
            foreach (var (key, value) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var redisKey = GetRedisKey(key);
                var json = JsonSerializer.Serialize(value, _serializerOptions);

                var task = batch.ScriptEvaluateAsync(
                    RedisLuaScripts.GetSaveStateScript(),
                    new
                    {
                        key = (RedisKey)redisKey,
                        data = json,
                        ttl = ttlMilliseconds
                    },
                    // A batch cannot dispatch a NOSCRIPT retry queued after its Execute call.
                    CommandFlags.NoScriptCache);
                saveTasks.Add(task);
            }

            batch.Execute();
            await Task.WhenAll(saveTasks);

            Logger.LogDebug("Saved {Count} states in bulk", items.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR saving bulk state with {0} items", items.Count);
        }
    }

    public override async Task<bool> TryDeleteStateWithETagAsync(
        string key,
        string expectedETag,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        try
        {
            var result = await _database.ScriptEvaluateAsync(
                RedisLuaScripts.GetDeleteWithETagScript(),
                new
                {
                    key = (RedisKey)redisKey,
                    etag = expectedETag
                });

            var success = (int)result == 1;

            if (success)
            {
                Logger.LogDebug("Deleted state with ETag for key: {Key}", key);
            }
            else
            {
                Logger.LogDebug("ETag mismatch for delete, key: {Key}. Expected: {Expected}", key, expectedETag);
            }

            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(Logger, "ERROR deleting state with ETag for key: {0}", key);
        }
    }
}
