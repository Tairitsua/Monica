using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.StateStore.Abstractions;
using Monica.StateStore.Providers.Memory;
using Monica.Testing.Hosting;

namespace Test.Monica.Framework.UI.ServiceDiscovery;

internal sealed class ServiceDiscoveryTestApplicationFactory(ServiceDiscoveryRole role = ServiceDiscoveryRole.Worker)
    : MonicaTestApplicationFactory<ModuleServiceDiscovery>
{
    internal const string AppId = "discovery-contract-host";

    protected override void ConfigureHost(WebApplicationBuilder builder)
    {
        builder.Environment.ApplicationName = typeof(ModuleServiceDiscovery).Assembly.GetName().Name!;
    }

    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.ConfigureModuleSystem(options =>
        {
            options.AutoAddMonicaHttpListener = false;
            options.EnableMinimalApiByDefault = false;
        });
        builder.AddServiceDiscovery(options =>
        {
            options.AppId = AppId;
            options.AppName = "Discovery contract host";
            options.ProjectName = "Discovery contracts";
            options.DomainName = "Infrastructure";
            options.FromInstance = "local-contract-instance";
            options.Role = role;
            options.Election.HeartbeatPeriodSeconds = 600;
            options.Election.HeartbeatJitterMilliseconds = 1;
        }).UseMemoryStorage();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<ReadFailureMemoryStore>();
        services.Replace(ServiceDescriptor.Singleton<IMemoryStateStore>(provider =>
            provider.GetRequiredService<ReadFailureMemoryStore>()));
    }
}

// All successful operations remain the actual MemoryCacheProvider implementation. Only reads at the
// external store boundary can fail or complete at a controlled cancellation boundary. The production
// registration manager and facade remain intact.
internal sealed class ReadFailureMemoryStore(IMemoryCache memoryCache, ILogger<MemoryCacheProvider> logger)
    : MemoryCacheProvider(memoryCache, logger)
{
    internal const string RegistryFailure = "Registration provider read failed.";
    internal const string LeaseFailure = "Leader provider read failed.";

    internal bool FailRegistryRead { get; set; }
    internal bool FailLeaseRead { get; set; }
    internal Action? RegistryScanCompleted { get; set; }
    internal Action? RegistryBulkReadCompleted { get; set; }
    internal Action? LeaseReadCompleted { get; set; }

    public override async Task<List<string>> ScanKeysAsync(string pattern, CancellationToken cancellationToken = default)
    {
        if (FailRegistryRead && pattern.StartsWith("reg:", StringComparison.Ordinal))
        {
            throw new IOException(RegistryFailure);
        }

        var keys = await base.ScanKeysAsync(pattern, cancellationToken);
        if (pattern.StartsWith("reg:", StringComparison.Ordinal))
        {
            RegistryScanCompleted?.Invoke();
        }

        return keys;
    }

    public override async Task<Dictionary<string, T?>> GetBulkStateAsync<T>(IReadOnlyList<string> keys,
        bool removeEmptyValue = true, CancellationToken cancellationToken = default) where T : default
    {
        var values = await base.GetBulkStateAsync<T>(keys, removeEmptyValue, cancellationToken);
        if (typeof(T) == typeof(InstanceState))
        {
            RegistryBulkReadCompleted?.Invoke();
        }

        return values;
    }

    public override async Task<T?> GetStateAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : default
    {
        if (FailLeaseRead && typeof(T) == typeof(LeaderState))
        {
            throw new IOException(LeaseFailure);
        }

        var value = await base.GetStateAsync<T>(key, cancellationToken);
        if (typeof(T) == typeof(LeaderState))
        {
            LeaseReadCompleted?.Invoke();
        }

        return value;
    }
}

internal sealed class ControlledDiscoveryCatalog : IServiceDiscoveryCatalogProvider
{
    internal List<PredefinedServiceInfo> PreloadedServices { get; } = [];
    internal Func<Task<List<PredefinedServiceInfo>>>? ReadPreloadedServices { get; set; }
    internal bool FailPreloadRead { get; set; }
    internal const string PreloadFailure = "Predefined service provider read failed.";


    public Task<List<PredefinedServiceInfo>> GetPreloadedServicesAsync()
    {
        if (FailPreloadRead)
        {
            throw new IOException(PreloadFailure);
        }

        return ReadPreloadedServices?.Invoke() ?? Task.FromResult(PreloadedServices);
    }
}
