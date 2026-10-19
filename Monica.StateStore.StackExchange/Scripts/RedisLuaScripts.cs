using StackExchange.Redis;

namespace Monica.StateStore.StackExchange.Scripts;

/// <summary>
/// Atomic operations for state hashes containing JSON in <c>data</c> and an integer ETag in <c>version</c>.
/// Positive TTL parameters are milliseconds; zero clears an existing expiry. Redis errors propagate to the caller.
/// </summary>
public static class RedisLuaScripts
{
    /// <summary>
    /// Reads JSON and its exact version string, or returns a null value and empty ETag for a missing key.
    /// A hash with data but no version is invalid state and raises an error.
    /// </summary>
    public const string GetWithETag = """
        local data = redis.call('HGET', @key, 'data')
        if data == false then
            return {false, ''}
        end
        local version = redis.call('HGET', @key, 'version')
        if not version or version == '' then
            return redis.error_reply('State hash is missing its version field')
        end
        return {data, version}
        """;

    /// <summary>
    /// Saves JSON, increments the version, and applies the requested expiry atomically.
    /// Foreign Redis types and invalid integer versions fail without deleting or replacing their contents.
    /// </summary>
    public const string SaveState = """
        redis.call('HINCRBY', @key, 'version', 1)
        redis.call('HSET', @key, 'data', @data)
        if tonumber(@ttl) > 0 then
            redis.call('PEXPIRE', @key, @ttl)
        else
            redis.call('PERSIST', @key)
        end
        return redis.call('HGET', @key, 'version')
        """;

    /// <summary>
    /// Saves only when an existing hash has the exact expected ETag; a missing key returns null.
    /// ETags are compared as strings so versions above Lua's exact numeric range remain usable.
    /// </summary>
    public const string CompareAndSwap = """
        local currentEtag = redis.call('HGET', @key, 'version')
        if not currentEtag or currentEtag == '' or currentEtag ~= @etag then
            return nil
        end
        redis.call('HINCRBY', @key, 'version', 1)
        redis.call('HSET', @key, 'data', @data)
        if tonumber(@ttl) > 0 then
            redis.call('PEXPIRE', @key, @ttl)
        else
            redis.call('PERSIST', @key)
        end
        return redis.call('HGET', @key, 'version')
        """;

    /// <summary>
    /// Deletes only when an existing hash has the exact expected ETag. No ETag is a wildcard.
    /// Returns one when deleted and zero for missing state or a mismatch.
    /// </summary>
    public const string DeleteWithETag = """
        local currentEtag = redis.call('HGET', @key, 'version')
        if not currentEtag or currentEtag == '' or currentEtag ~= @etag then
            return 0
        end
        return redis.call('DEL', @key)
        """;

    /// <summary>
    /// Creates a version-one hash only when the physical Redis key is absent, and applies its expiry atomically.
    /// Returns one when created and zero when any key already exists, including a JSON-null payload.
    /// </summary>
    public const string SaveIfNotExists = """
        if redis.call('EXISTS', @key) == 1 then
            return 0
        end
        redis.call('HSET', @key, 'data', @data, 'version', 1)
        if tonumber(@ttl) > 0 then
            redis.call('PEXPIRE', @key, @ttl)
        end
        return 1
        """;

    /// <summary>
    /// Reads the remaining milliseconds atomically: minus two means absent and minus one means permanent.
    /// </summary>
    public const string KeyTtl = """
        return redis.call('PTTL', @key)
        """;

    private static readonly LuaScript GetWithETagScript = LuaScript.Prepare(GetWithETag);
    private static readonly LuaScript SaveStateScript = LuaScript.Prepare(SaveState);
    private static readonly LuaScript CompareAndSwapScript = LuaScript.Prepare(CompareAndSwap);
    private static readonly LuaScript DeleteWithETagScript = LuaScript.Prepare(DeleteWithETag);
    private static readonly LuaScript SaveIfNotExistsScript = LuaScript.Prepare(SaveIfNotExists);
    private static readonly LuaScript KeyTtlScript = LuaScript.Prepare(KeyTtl);

    /// <summary>Gets the prepared state/ETag read script.</summary>
    public static LuaScript GetGetWithETagScript() => GetWithETagScript;

    /// <summary>Gets the prepared unconditional save script.</summary>
    public static LuaScript GetSaveStateScript() => SaveStateScript;

    /// <summary>Gets the prepared conditional save script.</summary>
    public static LuaScript GetCompareAndSwapScript() => CompareAndSwapScript;

    /// <summary>Gets the prepared conditional deletion script.</summary>
    public static LuaScript GetDeleteWithETagScript() => DeleteWithETagScript;

    /// <summary>Gets the prepared create-if-absent script.</summary>
    public static LuaScript GetSaveIfNotExistsScript() => SaveIfNotExistsScript;

    /// <summary>Gets the prepared TTL read script.</summary>
    public static LuaScript GetKeyTtlScript() => KeyTtlScript;
}
