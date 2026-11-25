using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.Core;
using Monica.Core.ApplicationRoles;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.HealthCheck.Services;

namespace Monica.HealthCheck.Facades;

/// <summary>Current-instance role health, captured only for the operational role dashboard.</summary>
public sealed record RoleHealthDashboardSnapshot(RoleHealthSnapshot Host, HealthStatus RoleReadiness, DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, IReadOnlyList<RoleResourceSnapshot>> Resources);

/// <summary>Public resource metadata for a single role. Contains no implementation instance or configuration values.</summary>
public sealed record RoleResourceSnapshot(string Key, TypeActivationKind Kind, ActivationMode Mode);

/// <summary>Combines startup state, the latest completed role probes and the module-owned activation inventory.</summary>
public sealed class RoleHealthFacade
{
    private readonly MonicaApplication _application;
    private readonly IRoleHealthStateProvider? _roles;

    internal RoleHealthFacade(MonicaApplication application, IRoleHealthStateProvider? roles)
        => (_application, _roles) = (application, roles);

    /// <summary>Captures read-only evidence, including startup candidates visible in this instance. Returns null on hosts without an application model.</summary>
    public RoleHealthDashboardSnapshot? GetSnapshot()
    {
        if (_roles is null) return null;
        var snapshot = _roles.GetSnapshot(includeCandidateValidation: true);
        var resources = _application.ActivationResources
            .Where(resource => resource.Kind is not (TypeActivationKind.Service or TypeActivationKind.RequestHandler))
            .Select(resource => (Role: _roles.GetRoleId(resource.ImplementationType ?? typeof(object),
                resource.Kind == TypeActivationKind.Outbox ? null : resource.RegistrationOwner), Resource: resource))
            .Where(item => item.Role is not null)
            .GroupBy(item => item.Role!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RoleResourceSnapshot>)group
                .Select(item => new RoleResourceSnapshot(item.Resource.Key, item.Resource.Kind, item.Resource.Mode)).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return new RoleHealthDashboardSnapshot(snapshot, ApplicationRolesHealthCheck.Evaluate(snapshot), DateTimeOffset.UtcNow, resources);
    }
}
