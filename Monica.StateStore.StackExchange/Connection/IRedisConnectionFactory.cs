using Monica.Modules;
using StackExchange.Redis;

namespace Monica.StateStore.StackExchange.Connection;

/// <summary>
/// Creates caller-owned Redis data connections and owns their Sentinel discovery connections.
/// Keep the factory alive until all returned connections have been disposed, then dispose the factory.
/// Monica's singleton registrations provide this disposal order automatically.
/// </summary>
public interface IRedisConnectionFactory : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Creates a data connection using the selected mode. The caller owns and must dispose the returned multiplexer.
    /// The factory retains Sentinel discovery connections until factory disposal so failover remains active.
    /// </summary>
    /// <param name="options">Redis state store options</param>
    /// <returns>A caller-owned Redis data connection.</returns>
    /// <exception cref="ArgumentException">Connection settings are invalid.</exception>
    /// <exception cref="InvalidOperationException">Connection configuration is missing.</exception>
    /// <exception cref="ObjectDisposedException">The factory has already been disposed.</exception>
    IConnectionMultiplexer CreateConnection(ModuleRedisStateStoreOption options);

    /// <summary>
    /// Creates a data connection independently of the StateStore module options. The caller owns and must dispose
    /// the returned multiplexer before disposing this factory, which retains any Sentinel discovery connection.
    /// </summary>
    /// <param name="configuration">Data-node and optional Sentinel connection settings.</param>
    /// <param name="connectionType">Normal, Sentinel, or Cluster connection mode.</param>
    /// <returns>A caller-owned Redis data connection.</returns>
    /// <remarks>
    /// This overload does not select or validate a database. The caller selects a database with
    /// <see cref="IConnectionMultiplexer.GetDatabase"/> and must validate its own database setting;
    /// Redis Cluster supports only database zero. Register the factory and returned connection as host-owned
    /// singletons when sharing them with a singleton infrastructure provider.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException">Connection settings or the selected mode are invalid.</exception>
    /// <exception cref="ObjectDisposedException">The factory has already been disposed.</exception>
    IConnectionMultiplexer CreateConnection(
        RedisConnectionConfiguration configuration, ERedisConnectionType connectionType);

    /// <summary>
    /// Gets a connected primary server. A replica is never returned for a write operation.
    /// </summary>
    /// <param name="connection">Redis connection multiplexer</param>
    /// <returns>A connected primary server. A cluster may have several primaries.</returns>
    /// <exception cref="InvalidOperationException">No connected primary server is available.</exception>
    IServer GetPrimaryServer(IConnectionMultiplexer connection);
}
