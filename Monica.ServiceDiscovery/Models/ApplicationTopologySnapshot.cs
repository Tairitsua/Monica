using Monica.ApplicationModel.Models;
using Monica.Core.ApplicationRoles;

namespace Monica.ServiceDiscovery.Models;

/// <summary>Compiled application and deployment evidence, independent of registry membership and live traffic.</summary>
public sealed record ApplicationTopologySnapshot(bool IsAvailable, IReadOnlyList<DomainDependencyInfo> Dependencies)
{
    /// <summary>Gets the configured application placement, or null without an application model.</summary>
    public ApplicationDeployment? Deployment { get; init; }

    /// <summary>Gets the configured role inventory and its ownership, including roles absent from this artifact.</summary>
    public IReadOnlyDictionary<string, ApplicationRoleDefinition> Roles { get; init; } =
        new Dictionary<string, ApplicationRoleDefinition>(StringComparer.Ordinal);

    /// <summary>Gets process-local admission evidence, or null if the host has no role health provider.</summary>
    public RoleHealthSnapshot? LocalRoleHealth { get; init; }
}

/// <summary>One compiled published API use with both logical owners and its source type.</summary>
public sealed record DomainDependencyInfo(string SourceDomainId, string? SourceRoleId, string TargetDomainId,
    string TargetRoleId, string Operation, string SourceType)
{
    /// <summary>The required active role, when the contract uses RunOnRole.</summary>
    public string? RequiredRole { get; init; }

    /// <summary>Whether the querying executable has the exact target handler binding.</summary>
    public bool IsLocallyAvailable { get; init; }

    /// <summary>Whether an application's observed ownership includes the source owner. It does not prove execution.</summary>
    public bool IsOwnedBy(RegisteredServiceStatus service) => SourceRoleId is { } role
        ? service.RoleIds.Contains(role, StringComparer.Ordinal) : service.BelongsToDomain(SourceDomainId);
}
