namespace Monica.ServiceDiscovery.Models;

public sealed class DomainDetailInfo
{
    public required DomainInfo Domain { get; init; }

    /// <summary>Whether this host has compiled application dependency evidence.</summary>
    public bool HasDependencyEvidence { get; init; }

    /// <summary>Gets static API uses targeting this domain.</summary>
    public IReadOnlyList<DomainDependencyInfo> Dependencies { get; init; } = [];

    public List<RegisteredServiceStatus> RelatedServices { get; init; } = [];
}
