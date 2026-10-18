using Dapr.DistributedLock.Extensions;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Dapr.Services;
using Monica.Locker.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDaprLockerBuilderExtensions
{
    /// <summary>
    /// Registers the Dapr lock provider on top of the Locker module and returns its module registration.
    /// </summary>
    /// <remarks>The provider and Dapr distributed lock client are host-owned singletons.</remarks>
    /// <param name="module">The Locker registration that should use the Dapr provider.</param>
    /// <param name="action">Optional Dapr locker option configuration.</param>
    /// <returns>The Dapr locker module registration.</returns>
    public static ModuleRegistration<ModuleDaprLocker, ModuleDaprLockerOption> UseDaprProvider(
        this ModuleRegistration<ModuleLocker, ModuleLockerOption> module,
        Action<ModuleDaprLockerOption>? action = null)
    {
        module.UseProvider<DaprLockProvider>();
        return module.Include<ModuleDaprLocker, ModuleDaprLockerOption>(action);
    }
}

/// <summary>
/// Registers the Dapr distributed lock client integration used by <see cref="DaprLockProvider"/>.
/// </summary>
public class ModuleDaprLocker : MonicaModule<ModuleDaprLockerOption>
{
    public override void ConfigureServices(ModuleContext<ModuleDaprLockerOption> context)
    {
        var services = context.Services;
        // The singleton Locker provider must not capture the SDK's default scoped client.
        services.AddDaprDistributedLock((serviceProvider, clientBuilder) =>
        {
            if (!string.IsNullOrWhiteSpace(Option.DaprHttpEndpoint))
            {
                clientBuilder.UseHttpEndpoint(Option.DaprHttpEndpoint);
            }

            if (!string.IsNullOrWhiteSpace(Option.DaprApiToken))
            {
                clientBuilder.UseDaprApiToken(Option.DaprApiToken);
            }

            if (Option.GrpcChannelOptions != null)
            {
                clientBuilder.UseGrpcChannelOptions(Option.GrpcChannelOptions);
            }
        }, lifetime: ServiceLifetime.Singleton);
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocker, ModuleLockerOption>();
    }
}

/// <summary>
/// Configures the Dapr-backed locker integration.
/// </summary>


/// <summary>
/// Configures how Monica talks to Dapr when acquiring distributed locks.
/// </summary>
public class ModuleDaprLockerOption : ModuleOptions<ModuleDaprLocker>
{
    /// <summary>
    /// Name of the Dapr lock store component used by lock requests.
    /// </summary>
    public string StoreName { get; set; } = default!;

    /// <summary>
    /// Prefix added to auto-generated owner ids when a caller does not supply <see cref="LockAcquisitionOptions.Owner"/>.
    /// </summary>
    public string? LockOwnerPrefix { get; set; }

    /// <summary>
    /// Default lease duration used when <see cref="LockAcquisitionOptions.LeaseDuration"/> is not provided.
    /// </summary>
    public TimeSpan DefaultLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Custom HTTP endpoint for the Dapr sidecar. When omitted, Dapr's default endpoint resolution still applies.
    /// </summary>
    public string? DaprHttpEndpoint { get; set; }

    /// <summary>
    /// Reserved for future explicit gRPC endpoint customization. The current registration path relies on Dapr's default
    /// endpoint resolution and optional <see cref="GrpcChannelOptions"/> overrides.
    /// </summary>
    public string? DaprGrpcEndpoint { get; set; }

    /// <summary>
    /// API token used for Dapr authentication. When omitted, Dapr's default token resolution still applies.
    /// </summary>
    public string? DaprApiToken { get; set; }

    /// <summary>
    /// Custom gRPC channel options applied to the distributed lock client.
    /// </summary>
    public GrpcChannelOptions? GrpcChannelOptions { get; set; }
}
