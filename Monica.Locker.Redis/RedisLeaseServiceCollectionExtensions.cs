using Microsoft.Extensions.DependencyInjection;
using Monica.Locker.Leases;
using StackExchange.Redis;

namespace Monica.Locker.Redis;

/// <summary>Registers Redis coordination without changing the host's locks, StateStore, or relational lease authority.</summary>
public static class RedisLeaseServiceCollectionExtensions
{
    /// <summary>Registers one keyed singleton lease store using an existing host-owned Redis multiplexer.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="serviceKey">Unique lease-store key used by consumer bindings.</param>
    /// <param name="options">Immutable database and deployment namespace settings.</param>
    /// <param name="connectionServiceKey">Existing keyed multiplexer, or null for the unkeyed multiplexer.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>
    /// The multiplexer must be a singleton kept alive until consumers stop; this provider does not dispose it.
    /// Redis failover can lose acknowledged writes. Select this authority for duplicate-tolerant work, not as a
    /// replacement for a relational transaction guard. A missing connection or store never falls back to memory.
    /// </remarks>
    public static IServiceCollection AddRedisLeaseStore(this IServiceCollection services, string serviceKey,
        RedisLeaseStoreOptions options, string? connectionServiceKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IExclusiveLeaseStore)
            && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, serviceKey)))
            throw new InvalidOperationException($"Lease store '{serviceKey}' is already registered.");
        services.AddKeyedSingleton<IExclusiveLeaseStore>(serviceKey, (provider, _) => new RedisLeaseStore(
            connectionServiceKey is null ? provider.GetRequiredService<IConnectionMultiplexer>()
                : provider.GetRequiredKeyedService<IConnectionMultiplexer>(connectionServiceKey), options));
        return services;
    }
}
