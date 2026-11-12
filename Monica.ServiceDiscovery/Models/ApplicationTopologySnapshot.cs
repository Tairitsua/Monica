namespace Monica.ServiceDiscovery.Models;

/// <summary>Compiled application dependency evidence, independent of registry membership and live traffic.</summary>
public sealed record ApplicationTopologySnapshot(bool IsAvailable, IReadOnlyList<DomainDependencyInfo> Dependencies);

/// <summary>One published API use with both logical owners and its source type.</summary>
public sealed record DomainDependencyInfo(string SourceDomainId, string? SourceRoleId, string TargetDomainId,
    string TargetRoleId, string Operation, string SourceType)
{
    /// <summary>Whether an application's observed ownership includes the source owner; this does not prove execution.</summary>
    public bool IsOwnedBy(RegisteredServiceStatus service) => SourceRoleId is { } role
        ? service.RoleIds.Contains(role, StringComparer.Ordinal) : service.BelongsToDomain(SourceDomainId);
}
