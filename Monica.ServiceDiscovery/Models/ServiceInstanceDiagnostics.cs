using Monica.Core.ApplicationRoles;

namespace Monica.ServiceDiscovery.Models;

/// <summary>Optional evidence published by the instance itself with its registration heartbeat.</summary>
/// <param name="Artifact">The entry assembly name, when known.</param>
/// <param name="HeartbeatPeriod">The sending instance's configured heartbeat period.</param>
/// <param name="RegistrationTtl">The sending instance's registration lifetime.</param>
/// <param name="IncludedRoles">All role implementations included in the artifact, or null without an application model.</param>
/// <param name="RoleHealth">Status and transition time for selected roles only. Null means no health provider.</param>
public sealed record ServiceInstanceDiagnostics(string? Artifact, TimeSpan HeartbeatPeriod, TimeSpan RegistrationTtl,
    IReadOnlyList<string>? IncludedRoles, IReadOnlyList<RegisteredRoleHealth>? RoleHealth);

/// <summary>A heartbeat-reported role status. Raw faults and configuration values stay in the source process.</summary>
public sealed record RegisteredRoleHealth(string RoleId, RoleHealthStatus Status, DateTimeOffset LastTransitionAt);
