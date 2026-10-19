using Microsoft.Extensions.Logging.Abstractions;
using Monica.Modules;
using Monica.StateStore.StackExchange.Connection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Test.Monica.StateStore.StackExchange.Connection;

public class RedisConnectionFactoryTests
{
    [Theory]
    [InlineData(ERedisConnectionType.Normal)]
    [InlineData(ERedisConnectionType.Sentinel)]
    [InlineData(ERedisConnectionType.Cluster)]
    public void BuildConnectionOptions_WhenConfigured_ShouldForwardCredentialsTlsTimeoutsAndSeeds(ERedisConnectionType mode)
    {
        var config = new RedisConnectionConfiguration
        {
            Host = "redis-primary",
            Port = 6381,
            AdditionalEndpoints = ["redis-secondary:6382", "[::1]:6383", "redis-primary:6381"],
            User = "data-user",
            Password = "data-password",
            Ssl = true,
            SslHost = "redis.example.test",
            ConnectTimeout = 1501,
            SyncTimeout = 2502,
            AsyncTimeout = 3503,
            ConnectRetry = 7,
            AbortOnConnectFail = true,
            AllowAdmin = true,
            ServiceName = "data-service"
        };

        var options = RedisConnectionFactory.BuildConnectionOptions(config, mode);

        Assert.Equal("data-user", options.User);
        Assert.Equal("data-password", options.Password);
        Assert.True(options.Ssl);
        Assert.Equal("redis.example.test", options.SslHost);
        Assert.Equal(1501, options.ConnectTimeout);
        Assert.Equal(2502, options.SyncTimeout);
        Assert.Equal(3503, options.AsyncTimeout);
        Assert.Equal(7, options.ConnectRetry);
        Assert.True(options.AbortOnConnectFail);
        Assert.True(options.AllowAdmin);
        Assert.Equal(3, options.EndPoints.Count);
        Assert.Contains(EndPointCollection.TryParse("redis-primary:6381"), options.EndPoints);
        Assert.Contains(EndPointCollection.TryParse("redis-secondary:6382"), options.EndPoints);
        Assert.Contains(EndPointCollection.TryParse("[::1]:6383"), options.EndPoints);
        Assert.Equal(mode == ERedisConnectionType.Sentinel ? "data-service" : null, options.ServiceName);
    }

    [Fact]
    public void BuildSentinelOptions_WhenCredentialsDiffer_ShouldSeparateDiscoveryAndDataAuthentication()
    {
        var config = new RedisConnectionConfiguration
        {
            Host = "sentinel-one",
            Port = 26379,
            AdditionalEndpoints = ["sentinel-two:26380"],
            User = "data-user",
            Password = "data-password",
            SentinelUser = "sentinel-user",
            SentinelPassword = "sentinel-password",
            ServiceName = "data-service",
            Ssl = true,
            SslHost = "redis.example.test",
            ConnectTimeout = 1100,
            SyncTimeout = 2200,
            AsyncTimeout = 3300,
            ConnectRetry = 4,
            AbortOnConnectFail = true,
            AllowAdmin = true
        };

        var sentinelOptions = RedisConnectionFactory.BuildSentinelOptions(config);
        var primaryOptions = RedisConnectionFactory.BuildConnectionOptions(config, ERedisConnectionType.Sentinel);

        Assert.Equal("sentinel-user", sentinelOptions.User);
        Assert.Equal("sentinel-password", sentinelOptions.Password);
        Assert.Equal("data-user", primaryOptions.User);
        Assert.Equal("data-password", primaryOptions.Password);
        Assert.Null(sentinelOptions.ServiceName);
        Assert.Equal("data-service", primaryOptions.ServiceName);
        Assert.Same(CommandMap.Sentinel, sentinelOptions.CommandMap);
        Assert.Equal(string.Empty, sentinelOptions.TieBreaker);
        Assert.True(sentinelOptions.Ssl);
        Assert.Equal("redis.example.test", sentinelOptions.SslHost);
        Assert.Equal(1100, sentinelOptions.ConnectTimeout);
        Assert.Equal(2200, sentinelOptions.SyncTimeout);
        Assert.Equal(3300, sentinelOptions.AsyncTimeout);
        Assert.Equal(4, sentinelOptions.ConnectRetry);
        Assert.True(sentinelOptions.AbortOnConnectFail);
        Assert.True(sentinelOptions.AllowAdmin);
        Assert.Equal(primaryOptions.EndPoints, sentinelOptions.EndPoints);
    }

    [Fact]
    public void BuildSentinelOptions_WhenSentinelCredentialsAreOmitted_ShouldKeepDiscoveryUnauthenticated()
    {
        var config = new RedisConnectionConfiguration { User = "data-user", Password = "data-password" };

        var options = RedisConnectionFactory.BuildSentinelOptions(config);

        Assert.Null(options.User);
        Assert.Null(options.Password);
    }

    [Fact]
    public void BuildConnectionOptions_WhenAsyncTimeoutIsOmitted_ShouldInheritSyncTimeout()
    {
        var options = RedisConnectionFactory.BuildConnectionOptions(
            new RedisConnectionConfiguration { SyncTimeout = 9700 }, ERedisConnectionType.Normal);

        Assert.Equal(9700, options.AsyncTimeout);
    }

    [Theory]
    [InlineData("redis-without-port")]
    [InlineData("redis:0")]
    [InlineData("redis:65536")]
    [InlineData("redis:not-a-port")]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateConnection_WhenAdditionalEndpointIsInvalid_ShouldRejectBeforeOpeningSockets(string endpoint)
    {
        using var factory = CreateFactory();
        var options = new ModuleRedisStateStoreOption
        {
            Connection = new RedisConnectionConfiguration { AdditionalEndpoints = [endpoint] }
        };

        Assert.ThrowsAny<ArgumentException>(() => factory.CreateConnection(options));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Port")]
    [InlineData("ConnectTimeout")]
    [InlineData("SyncTimeout")]
    [InlineData("AsyncTimeout")]
    [InlineData("ConnectRetry")]
    [InlineData("ServiceName")]
    [InlineData("DatabaseIndex")]
    [InlineData("ClusterDatabase")]
    [InlineData("ConnectionType")]
    public void CreateConnection_WhenConfigurationIsInvalid_ShouldRejectBeforeOpeningSockets(string invalidSetting)
    {
        using var factory = CreateFactory();
        var config = new RedisConnectionConfiguration();
        var options = new ModuleRedisStateStoreOption { Connection = config };
        switch (invalidSetting)
        {
            case "Host": config.Host = " "; break;
            case "Port": config.Port = 0; break;
            case "ConnectTimeout": config.ConnectTimeout = 0; break;
            case "SyncTimeout": config.SyncTimeout = 0; break;
            case "AsyncTimeout": config.AsyncTimeout = 0; break;
            case "ConnectRetry": config.ConnectRetry = -1; break;
            case "ServiceName": options.ConnectionType = ERedisConnectionType.Sentinel; config.ServiceName = " "; break;
            case "DatabaseIndex": options.DatabaseIndex = -1; break;
            case "ClusterDatabase": options.ConnectionType = ERedisConnectionType.Cluster; options.DatabaseIndex = 1; break;
            case "ConnectionType": options.ConnectionType = (ERedisConnectionType)99; break;
        }

        Assert.ThrowsAny<ArgumentException>(() => factory.CreateConnection(options));
    }

    [Fact]
    public void CreateConnection_WhenConfigurationIsMissing_ShouldExplainRequiredConfiguration()
    {
        using var factory = CreateFactory();

        Assert.Throws<InvalidOperationException>(() => factory.CreateConnection(new ModuleRedisStateStoreOption()));
    }

    [Fact]
    public void GetPrimaryServer_WhenOnlyReplicasOrSentinelsAreConnected_ShouldRejectWriteTarget()
    {
        using var factory = CreateFactory();
        var replica = Substitute.For<IServer>();
        replica.IsConnected.Returns(true);
        replica.IsReplica.Returns(true);
        var sentinel = Substitute.For<IServer>();
        sentinel.IsConnected.Returns(true);
        sentinel.ServerType.Returns(ServerType.Sentinel);
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetServers().Returns([replica, sentinel]);

        Assert.Throws<InvalidOperationException>(() => factory.GetPrimaryServer(connection));
    }

    [Fact]
    public void GetPrimaryServer_WhenConnectedPrimaryExists_ShouldSelectIt()
    {
        using var factory = CreateFactory();
        var disconnected = Substitute.For<IServer>();
        var primary = Substitute.For<IServer>();
        primary.IsConnected.Returns(true);
        primary.IsReplica.Returns(false);
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetServers().Returns([disconnected, primary]);

        Assert.Same(primary, factory.GetPrimaryServer(connection));
    }

    [Fact]
    public void CreateConnection_WhenFactoryIsDisposed_ShouldRejectBeforeOpeningSockets()
    {
        var factory = CreateFactory();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.CreateConnection(
            new ModuleRedisStateStoreOption { Connection = new RedisConnectionConfiguration() }));
        factory.Dispose();
    }

    [Fact]
    public async Task CreateConnection_WhenFactoryIsDisposedAsync_ShouldRejectBeforeOpeningSockets()
    {
        var factory = CreateFactory();
        await factory.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => factory.CreateConnection(
            new ModuleRedisStateStoreOption { Connection = new RedisConnectionConfiguration() }));
        await factory.DisposeAsync();
    }

    private static RedisConnectionFactory CreateFactory() => new(NullLogger<RedisConnectionFactory>.Instance);
}
