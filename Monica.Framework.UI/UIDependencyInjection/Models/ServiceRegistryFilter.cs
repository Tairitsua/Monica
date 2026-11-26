using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Models;

namespace Monica.Framework.UI.UIDependencyInjection.Models;

/// <summary>The filter and paging selections for the service registry.</summary>
public sealed record ServiceRegistryFilter(
    string Query, string? Source, ServiceMarkers Markers,
    ServiceLifetime? Lifetime, DependencyInjectionDescriptorImplementationKind? Kind,
    int? From, int? To, string? Sort, int Page)
{
    public ServiceIdentity[] Apply(IEnumerable<ServiceIdentity> identities)
    {
        var filtered = identities.Where(id =>
            (Lifetime is null || id.Winner.Lifetime == Lifetime) &&
            (Kind is null || id.Winner.ImplementationKind == Kind) &&
            (Source is null || (Source == "auto") == id.Conventional) &&
            (Markers == 0 || (id.Markers & Markers) != 0) &&
            (From is null || !To.HasValue || id.Descriptors.Any(d => d.Index >= From && d.Index <= To)) &&
            MatchesText(id));

        return Sort switch
        {
            "name" => filtered.OrderBy(id => id.Display, StringComparer.OrdinalIgnoreCase).ThenBy(id => id.Winner.Index).ToArray(),
            "count" => filtered.OrderByDescending(id => id.Descriptors.Length).ThenBy(id => id.Winner.Index).ToArray(),
            _ => filtered.OrderBy(id => id.Winner.Index).ToArray()
        };
    }

    public bool Active => Lifetime is not null || Kind is not null || Source is not null || Markers != 0
        || From is not null || !string.IsNullOrWhiteSpace(Query);

    private bool MatchesText(ServiceIdentity id) => Query.Length == 0 || id.Descriptors.Any(d =>
        Contains(d.ServiceType) || Contains(d.ServiceAssemblyName) || Contains(d.ServiceKey) || Contains(d.ImplementationType) ||
        Contains(d.ImplementationAssemblyName) || Contains(d.FactoryDisplay) ||
        (d.AutoRegistration is { } auto && (Contains(auto.SourceImplementationType) || Contains(auto.RegistrationMode.ToString()) ||
            Contains(auto.LifetimeSource.ToString()) || auto.ExposedServices.Any(e => Contains(e.ServiceType) || Contains(e.ServiceKey)))));

    private bool Contains(string? value) => value?.Contains(Query, StringComparison.OrdinalIgnoreCase) == true;
}
