using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Modules;
using Monica.StateStore.Models;
using Monica.StateStore.StackExchange;
using Monica.StateStore.StackExchange.Scripts;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Test.Monica.StateStore.StackExchange.Providers;

public sealed class RedisStateStoreTests
{
    [Fact]
    public async Task ExistAsync_WhenStoredJsonIsNull_ShouldReportExistingDataWithoutDeserialization()
    {
        var database = Substitute.For<IDatabase>();
        database.HashExistsAsync("test:null", "data").Returns(true);
        var provider = CreateProvider(database);

        Assert.True(await provider.ExistAsync("null", TestContext.Current.CancellationToken));

        await database.DidNotReceiveWithAnyArgs().HashGetAsync(default, default(RedisValue));
    }

    [Fact]
    public async Task GetStateAsync_WhenMissingOrJsonNull_ShouldReturnNullWhileRawReadPreservesJsonNull()
    {
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync("test:missing", "data").Returns(RedisValue.Null);
        database.HashGetAsync("test:null", "data").Returns((RedisValue)"null");
        var provider = CreateProvider(database);

        Assert.Null(await provider.GetStateAsync<TestState>("missing", TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetRawStateAsync("missing", TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetStateAsync<TestState>("null", TestContext.Current.CancellationToken));
        Assert.Equal("null", await provider.GetRawStateAsync("null", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRawStateAsync_WhenRedisFails_ShouldKeepFailureDistinctFromMissingState()
    {
        var database = Substitute.For<IDatabase>();
        var failure = new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value");
        database.HashGetAsync("test:key", "data").Returns(Task.FromException<RedisValue>(failure));
        var provider = CreateProvider(database);

        var error = await Assert.ThrowsAsync<Exception>(() =>
            provider.GetRawStateAsync("key", TestContext.Current.CancellationToken));

        Assert.Same(failure, error.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetBulkStateAsync_WhenValuesAreMissingOrNull_ShouldHonorMissingEntryOption(bool removeEmptyValue)
    {
        var database = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        database.CreateBatch().Returns(batch);
        batch.HashGetAsync("test:missing", "data").Returns(RedisValue.Null);
        batch.HashGetAsync("test:null", "data").Returns((RedisValue)"null");
        batch.HashGetAsync("test:found", "data").Returns((RedisValue)"{\"value\":\"saved\",\"mode\":\"ready\"}");
        var provider = CreateProvider(database);

        var result = await provider.GetBulkStateAsync<TestState>(
            ["missing", "null", "found"], removeEmptyValue, TestContext.Current.CancellationToken);

        Assert.Equal(!removeEmptyValue, result.ContainsKey("missing"));
        Assert.Null(result["null"]);
        Assert.Equal(new TestState("saved", TestMode.Ready), result["found"]);
        batch.Received(1).Execute();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRawBulkStateAsync_WhenJsonIsNull_ShouldKeepRawNullAndHonorMissingEntryOption(bool removeEmptyValue)
    {
        var database = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        database.CreateBatch().Returns(batch);
        batch.HashGetAsync("test:missing", "data").Returns(RedisValue.Null);
        batch.HashGetAsync("test:null", "data").Returns((RedisValue)"null");
        var provider = CreateProvider(database);

        var result = await provider.GetRawBulkStateAsync(
            ["missing", "null"], removeEmptyValue, TestContext.Current.CancellationToken);

        Assert.Equal(!removeEmptyValue, result.ContainsKey("missing"));
        Assert.Equal("null", result["null"]);
        if (!removeEmptyValue)
        {
            Assert.Equal(string.Empty, result["missing"]);
        }
    }

    [Fact]
    public async Task GetStateAndETagAsync_WhenJsonIsNull_ShouldRetainExistingVersion()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(RedisLuaScripts.GetGetWithETagScript(), Arg.Any<object>())
            .Returns(RedisResult.Create([(RedisValue)"null", (RedisValue)"9007199254740993"]));
        var provider = CreateProvider(database);

        var result = await provider.GetStateAndETagAsync<TestState>("key", TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Equal("9007199254740993", result.ETag);
    }

    [Fact]
    public async Task GetStateAndETagAsync_WhenMissing_ShouldReturnEmptyVersion()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(RedisLuaScripts.GetGetWithETagScript(), Arg.Any<object>())
            .Returns(RedisResult.Create([RedisValue.Null, (RedisValue)string.Empty]));
        var provider = CreateProvider(database);

        var result = await provider.GetStateAndETagAsync<TestState>("missing", TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Empty(result.ETag);
    }

    [Fact]
    public async Task TrySaveStateWithETagAsync_WhenVersionMatches_ShouldReturnExactNewVersionWithoutReadBack()
    {
        var database = Substitute.For<IDatabase>();
        object? parameters = null;
        database.ScriptEvaluateAsync(RedisLuaScripts.GetCompareAndSwapScript(), Arg.Do<object>(value => parameters = value))
            .Returns(RedisResult.Create((RedisValue)"9007199254740994"));
        var provider = CreateProvider(database);

        var result = await provider.TrySaveStateWithETagAsync(
            "key", "value", "9007199254740993", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("9007199254740994", result.NewETag);
        Assert.Equal("9007199254740993", ReadParameter<string>(parameters, "etag"));
        await database.DidNotReceiveWithAnyArgs().HashGetAsync(default, default(RedisValue));
    }

    [Fact]
    public async Task TrySaveStateWithETagAsync_WhenVersionDoesNotMatch_ShouldReturnConflict()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(RedisLuaScripts.GetCompareAndSwapScript(), Arg.Any<object>())
            .Returns(RedisResult.Create(RedisValue.Null));
        var provider = CreateProvider(database);

        var result = await provider.TrySaveStateWithETagAsync(
            "key", "value", "old-version", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.NewETag);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("compare")]
    [InlineData("create")]
    [InlineData("bulk")]
    public async Task SaveStateAsync_WhenDurationIsSubmillisecond_ShouldPassPositiveMillisecondExpiryAndCanonicalJson(string operation)
    {
        var database = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        database.CreateBatch().Returns(batch);
        object? parameters = null;
        database.ScriptEvaluateAsync(Arg.Any<LuaScript>(), Arg.Do<object>(value => parameters = value))
            .Returns(RedisResult.Create((RedisValue)"1"));
        batch.ScriptEvaluateAsync(Arg.Any<LuaScript>(), Arg.Do<object>(value => parameters = value), CommandFlags.NoScriptCache)
            .Returns(RedisResult.Create((RedisValue)"1"));
        var provider = CreateProvider(database);
        var value = new TestState("saved", TestMode.Ready);
        var ttl = TimeSpan.FromTicks(1);

        switch (operation)
        {
            case "save":
                await provider.SaveStateAsync("key", value, TestContext.Current.CancellationToken, ttl);
                break;
            case "compare":
                await provider.TrySaveStateWithETagAsync("key", value, "previous", TestContext.Current.CancellationToken, ttl);
                break;
            case "create":
                await provider.TrySaveStateIfNotExistsAsync("key", value, TestContext.Current.CancellationToken, ttl);
                break;
            case "bulk":
                await provider.SaveBulkStateAsync([("key", value)], TestContext.Current.CancellationToken, ttl);
                break;
        }

        Assert.Equal(1L, ReadParameter<long>(parameters, "ttl"));
        Assert.Equal((RedisKey)"test:key", ReadParameter<RedisKey>(parameters, "key"));
        Assert.Equal("{\"value\":\"saved\",\"mode\":\"ready\"}", ReadParameter<string>(parameters, "data"));
    }

    [Fact]
    public async Task SaveBulkStateAsync_WhenScriptCacheMayHaveBeenCleared_ShouldAvoidDeferredBatchRetries()
    {
        var database = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        database.CreateBatch().Returns(batch);
        batch.ScriptEvaluateAsync(RedisLuaScripts.GetSaveStateScript(), Arg.Any<object>(), CommandFlags.NoScriptCache)
            .Returns(RedisResult.Create((RedisValue)"1"));
        var provider = CreateProvider(database);

        await provider.SaveBulkStateAsync(
            [("one", "first"), ("two", "second")], TestContext.Current.CancellationToken);

        await batch.Received(2).ScriptEvaluateAsync(
            RedisLuaScripts.GetSaveStateScript(), Arg.Any<object>(), CommandFlags.NoScriptCache);
        batch.Received(1).Execute();
    }

    [Theory]
    [InlineData(false, 1250L)]
    [InlineData(true, 0L)]
    public async Task SaveStateAsync_WhenTtlUsesDefaultOrExplicitZero_ShouldPassRequestedExpiry(bool explicitZero, long expectedMilliseconds)
    {
        var database = Substitute.For<IDatabase>();
        object? parameters = null;
        database.ScriptEvaluateAsync(Arg.Any<LuaScript>(), Arg.Do<object>(value => parameters = value))
            .Returns(RedisResult.Create((RedisValue)"1"));
        var provider = CreateProvider(database, options => options.DefaultTTL = TimeSpan.FromMilliseconds(1250));

        await provider.SaveStateAsync("key", "value", TestContext.Current.CancellationToken,
            explicitZero ? TimeSpan.Zero : null);

        Assert.Equal(expectedMilliseconds, ReadParameter<long>(parameters, "ttl"));
    }

    [Fact]
    public async Task SaveStateAsync_WhenDefaultTtlIsNegative_ShouldFailBeforeWriting()
    {
        var database = Substitute.For<IDatabase>();
        var provider = CreateProvider(database, options => options.DefaultTTL = TimeSpan.FromSeconds(-1));

        var error = await Assert.ThrowsAsync<Exception>(() =>
            provider.SaveStateAsync("key", "value", TestContext.Current.CancellationToken));

        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        await database.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(default(LuaScript)!, default);
    }

    [Theory]
    [InlineData(-1L, EStateStoreKeyTtlStatus.Permanent)]
    [InlineData(250L, EStateStoreKeyTtlStatus.Expiring)]
    public async Task GetKeyTtlAsync_WhenKeyExists_ShouldUseOneAtomicObservation(long milliseconds, EStateStoreKeyTtlStatus expectedStatus)
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(RedisLuaScripts.GetKeyTtlScript(), Arg.Any<object>())
            .Returns(RedisResult.Create((RedisValue)milliseconds));
        var provider = CreateProvider(database);

        var result = await provider.GetKeyTtlAsync("key", TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(milliseconds >= 0 ? TimeSpan.FromMilliseconds(milliseconds) : null, result.Remaining);
        await database.DidNotReceiveWithAnyArgs().KeyExistsAsync(default(RedisKey));
    }

    [Fact]
    public async Task GetKeyTtlAsync_WhenKeyIsMissing_ShouldReportMissing()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(RedisLuaScripts.GetKeyTtlScript(), Arg.Any<object>())
            .Returns(RedisResult.Create((RedisValue)(-2L)));
        var provider = CreateProvider(database);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            provider.GetKeyTtlAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBulkStateAsync_WhenKeysHaveDifferentClusterSlots_ShouldPipelineIndividualDeletes()
    {
        var database = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        database.CreateBatch().Returns(batch);
        var provider = CreateProvider(database);

        await provider.DeleteBulkStateAsync(["one", "two"], TestContext.Current.CancellationToken);

        await batch.Received(1).KeyDeleteAsync((RedisKey)"test:one");
        await batch.Received(1).KeyDeleteAsync((RedisKey)"test:two");
        await database.DidNotReceiveWithAnyArgs().KeyDeleteAsync(default(RedisKey[])!);
        batch.Received(1).Execute();
    }

    [Fact]
    public async Task ScanKeysAsync_WhenClusterHasSeveralPrimaries_ShouldCombineDistinctLogicalKeysAndEscapeNamespace()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var first = CreateServer("test[*]:one", "test[*]:shared");
        var second = CreateServer("test[*]:two", "test[*]:shared");
        var replica = Substitute.For<IServer>();
        replica.IsReplica.Returns(true);
        connection.GetServers().Returns([first, replica, second]);
        var provider = CreateProvider(database, options =>
        {
            options.KeyPrefix = "test[*]";
            options.DatabaseIndex = 0;
        }, connection);

        var result = await provider.ScanKeysAsync("*", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "one", "shared", "two" }, result.Order(StringComparer.Ordinal));
        first.Received(1).KeysAsync(database: 0, pattern: (RedisValue)"test\\[\\*\\]:*");
        second.Received(1).KeysAsync(database: 0, pattern: (RedisValue)"test\\[\\*\\]:*");
        replica.DidNotReceiveWithAnyArgs().KeysAsync();
    }

    [Fact]
    public async Task ScanKeysAsync_WhenPrimaryIsDisconnected_ShouldFailInsteadOfReturningPartialResults()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var available = CreateServer("test:one");
        var unavailable = Substitute.For<IServer>();
        connection.GetServers().Returns([available, unavailable]);
        var provider = CreateProvider(database, connection: connection);

        var error = await Assert.ThrowsAsync<Exception>(() =>
            provider.ScanKeysAsync("*", TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        available.DidNotReceiveWithAnyArgs().KeysAsync();
    }

    private static IServer CreateServer(params string[] keys)
    {
        var server = Substitute.For<IServer>();
        server.IsConnected.Returns(true);
        server.ServerType.Returns(ServerType.Cluster);
        server.KeysAsync(Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns(EnumerateKeys(keys));
        return server;
    }

    private static async IAsyncEnumerable<RedisKey> EnumerateKeys(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            yield return key;
        }

        await Task.CompletedTask;
    }

    private static T ReadParameter<T>(object? parameters, string name)
    {
        Assert.NotNull(parameters);
        return Assert.IsType<T>(parameters.GetType().GetProperty(name)!.GetValue(parameters));
    }

    private static RedisStateStore CreateProvider(
        IDatabase database,
        Action<ModuleRedisStateStoreOption>? configure = null,
        IConnectionMultiplexer? connection = null)
    {
        connection ??= Substitute.For<IConnectionMultiplexer>();
        var options = new ModuleRedisStateStoreOption { KeyPrefix = "test", DatabaseIndex = 3 };
        configure?.Invoke(options);
        connection.GetDatabase(options.DatabaseIndex).Returns(database);
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        serializerOptions.MakeReadOnly();
        return new RedisStateStore(
            connection,
            Options.Create(options),
            new JsonSerializerOptionsProvider(serializerOptions, DateTimeWireFormat.Iso8601WallClock),
            NullLogger<RedisStateStore>.Instance);
    }

    public sealed record TestState(string Value, TestMode Mode);

    public enum TestMode
    {
        Ready
    }
}
