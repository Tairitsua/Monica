using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Services.Support;

namespace Monica.ServiceDiscovery.Providers;

/// <summary>Lists canonical deployment applications and any additional predefined external applications.</summary>
public class DefaultServiceDiscoveryCatalogProvider(IOptions<ModuleServiceDiscoveryOption>? options = null,
    IApplicationModel? applicationModel = null) : IServiceDiscoveryCatalogProvider
{
    /// <inheritdoc />
    public virtual Task<List<PredefinedServiceInfo>> GetPreloadedServicesAsync()
    {
        var moduleOptions = options?.Value ?? new ModuleServiceDiscoveryOption();
        if (applicationModel is not null)
            ServiceDiscoveryApplicationIdentity.Validate(moduleOptions, applicationModel.Deployment);
        var configured = moduleOptions.PredefinedServices;
        var names = configured.ToDictionary(service => service.AppId, service => service.AppName, StringComparer.Ordinal);
        if (applicationModel is not null)
            foreach (var (id, entry) in applicationModel.Deployment.Deployments) names[id] = entry.AppName;
        return Task.FromResult(names.Keys.Order(StringComparer.Ordinal)
            .Select(id => new PredefinedServiceInfo { AppId = id,
                AppName = string.IsNullOrWhiteSpace(names[id]) ? id : names[id] }).ToList());
    }
}
