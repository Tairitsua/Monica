using System.Net;
using Microsoft.Extensions.Logging;
using Monica.Modules;
using StackExchange.Redis;

namespace Monica.StateStore.StackExchange.Connection;

/// <summary>
/// Creates Redis data connections and retains Sentinel discovery connections for their failover lifecycle.
/// Returned data connections are caller-owned and must be disposed before this factory.
/// </summary>
public class RedisConnectionFactory(ILogger<RedisConnectionFactory> logger) : IRedisConnectionFactory
{
    private readonly object _lifetimeLock = new();
    private readonly List<ConnectionMultiplexer> _sentinelConnections = [];
    private bool _disposed;

    /// <inheritdoc />
    public IConnectionMultiplexer CreateConnection(ModuleRedisStateStoreOption options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var config = options.Connection
            ?? throw new InvalidOperationException("Redis connection configuration is required.");
        ValidateStoreOptions(options);
        var connectionOptions = BuildConnectionOptions(config, options.ConnectionType);

        logger.LogInformation("Creating Redis [{Mode}] connection to {Host}:{Port}",
            options.ConnectionType, config.Host, config.Port);

        try
        {
            return options.ConnectionType == ERedisConnectionType.Sentinel
                ? CreateSentinelConnection(config, connectionOptions)
                : Connect(connectionOptions, options.ConnectionType.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Redis [{Mode}] connection failed for {Host}:{Port}",
                options.ConnectionType, config.Host, config.Port);
            throw;
        }
    }

    /// <inheritdoc />
    public IServer GetPrimaryServer(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.GetServers().FirstOrDefault(server =>
                server.IsConnected && !server.IsReplica && server.ServerType != ServerType.Sentinel)
            ?? throw new InvalidOperationException("No connected Redis primary server is available.");
    }

    /// <summary>
    /// Releases Sentinel discovery connections. Dispose all returned data multiplexers before calling this method.
    /// </summary>
    public void Dispose()
    {
        foreach (var connection in TakeSentinelConnections())
        {
            connection.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases Sentinel discovery connections after all returned data multiplexers have been disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var connection in TakeSentinelConnections())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private ConnectionMultiplexer[] TakeSentinelConnections()
    {
        lock (_lifetimeLock)
        {
            _disposed = true;
            var connections = _sentinelConnections.ToArray();
            _sentinelConnections.Clear();
            return connections;
        }
    }

    private ConnectionMultiplexer Connect(ConfigurationOptions options, string label)
    {
        var connection = ConnectionMultiplexer.Connect(options);
        SubscribeConnectionEvents(connection, label);
        LogConnectionStatus(connection, label);
        return connection;
    }

    private IConnectionMultiplexer CreateSentinelConnection(
        RedisConnectionConfiguration config, ConfigurationOptions primaryOptions)
    {
        var sentinelOptions = BuildSentinelOptions(config);
        var sentinelConnection = ConnectionMultiplexer.SentinelConnect(sentinelOptions);
        ConnectionMultiplexer? primaryConnection = null;

        try
        {
            SubscribeConnectionEvents(sentinelConnection, "Sentinel");
            LogConnectionStatus(sentinelConnection, "Sentinel");
            primaryConnection = sentinelConnection.GetSentinelMasterConnection(primaryOptions);
            SubscribeConnectionEvents(primaryConnection, "Sentinel-Primary");
            LogConnectionStatus(primaryConnection, "Sentinel-Primary");

            // Manual Sentinel discovery does not transfer discovery-connection ownership to the data multiplexer.
            lock (_lifetimeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sentinelConnections.Add(sentinelConnection);
            }

            return primaryConnection;
        }
        catch
        {
            primaryConnection?.Dispose();
            sentinelConnection.Dispose();
            throw;
        }
    }

    internal static ConfigurationOptions BuildConnectionOptions(
        RedisConnectionConfiguration config, ERedisConnectionType connectionType)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(connectionType))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionType), connectionType, "Unknown Redis connection type.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(config.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.ConnectTimeout, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.SyncTimeout, 1);
        if (config.AsyncTimeout is { } asyncTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(asyncTimeout, 1);
        }
        ArgumentOutOfRangeException.ThrowIfNegative(config.ConnectRetry);
        ArgumentNullException.ThrowIfNull(config.AdditionalEndpoints);
        if (connectionType == ERedisConnectionType.Sentinel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(config.ServiceName);
        }

        var options = new ConfigurationOptions
        {
            User = config.User,
            Password = config.Password,
            ConnectTimeout = config.ConnectTimeout,
            SyncTimeout = config.SyncTimeout,
            ConnectRetry = config.ConnectRetry,
            AbortOnConnectFail = config.AbortOnConnectFail,
            AllowAdmin = config.AllowAdmin,
            Ssl = config.Ssl,
            SslHost = config.SslHost,
            ServiceName = connectionType == ERedisConnectionType.Sentinel ? config.ServiceName : null
        };
        if (config.AsyncTimeout is { } timeout)
        {
            options.AsyncTimeout = timeout;
        }

        options.EndPoints.Add(config.Host, config.Port);
        foreach (var endpoint in config.AdditionalEndpoints)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
            var parsed = EndPointCollection.TryParse(endpoint);
            var port = parsed switch
            {
                IPEndPoint ip => ip.Port,
                DnsEndPoint dns => dns.Port,
                _ => 0
            };
            if (port is < 1 or > 65535)
            {
                throw new ArgumentException("Each additional Redis endpoint must specify a valid host and port.",
                    nameof(config.AdditionalEndpoints));
            }

            options.EndPoints.TryAdd(parsed!);
        }

        return options;
    }

    internal static ConfigurationOptions BuildSentinelOptions(RedisConnectionConfiguration config)
    {
        var options = BuildConnectionOptions(config, ERedisConnectionType.Sentinel);
        options.User = config.SentinelUser;
        options.Password = config.SentinelPassword;
        options.ServiceName = null;
        options.TieBreaker = string.Empty;
        options.CommandMap = CommandMap.Sentinel;
        return options;
    }

    private static void ValidateStoreOptions(ModuleRedisStateStoreOption options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.DatabaseIndex);
        if (options.ConnectionType == ERedisConnectionType.Cluster && options.DatabaseIndex != 0)
        {
            throw new ArgumentException("Redis Cluster supports only database zero.", nameof(options.DatabaseIndex));
        }
    }

    private void SubscribeConnectionEvents(IConnectionMultiplexer connection, string label)
    {
        connection.ConnectionFailed += (_, e) =>
            logger.LogWarning("Redis [{Label}] connection failed: {Endpoint} ({FailureType}) - {Exception}",
                label, e.EndPoint, e.FailureType, e.Exception?.Message);

        connection.ConnectionRestored += (_, e) =>
            logger.LogInformation("Redis [{Label}] connection restored: {Endpoint}", label, e.EndPoint);

        connection.InternalError += (_, e) =>
            logger.LogError(e.Exception, "Redis [{Label}] internal error (origin: {Origin})", label, e.Origin);

        connection.ErrorMessage += (_, e) =>
            logger.LogWarning("Redis [{Label}] server error: {Message}", label, e.Message);
    }

    private void LogConnectionStatus(IConnectionMultiplexer connection, string label)
    {
        if (connection.IsConnected)
        {
            logger.LogInformation("Redis [{Label}] connected successfully", label);
        }
        else
        {
            logger.LogWarning("Redis [{Label}] connection not yet established, will reconnect in background", label);
        }
    }
}
