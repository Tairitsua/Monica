using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Providers;

/// <summary>Combines configured application labels with the expected deployment applications.</summary>
public class DefaultServiceDiscoveryCatalogProvider(IOptions<ModuleServiceDiscoveryOption>? options = null,
    IApplicationModel? applicationModel = null) : IServiceDiscoveryCatalogProvider
{
    /// <inheritdoc />
    public virtual Task<List<PredefinedServiceInfo>> GetPreloadedServicesAsync()
    {
        var configured = options?.Value.PredefinedServices ?? [];
        var names = configured.ToDictionary(service => service.AppId, service => service.AppName, StringComparer.Ordinal);
        IEnumerable<string> ids = applicationModel is null ? names.Keys
            : applicationModel.RoleApplications.Values.Append(applicationModel.AppId).Concat(names.Keys);
        return Task.FromResult(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => new PredefinedServiceInfo { AppId = id,
                AppName = names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id }).ToList());
    }
}
