using System.Text.Json.Serialization;
using Monica.Core.JsonSerialization.Converters;

namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Instance registration status
/// </summary>
public class InstanceState
{
    /// <summary>
    /// Whether to be the leader (updated from memory during heartbeat)
    /// </summary>
    public bool IsLeader { get; set; }

    /// <summary>
    /// Instance status (computed on demand, not persisted)
    /// </summary>
    [JsonIgnore]
    public ServiceStatus Status { get; set; }

    /// <summary>
    /// Application identifier used as the service registration key.
    /// </summary>
    public required string AppId { get; set; }

    /// <summary>
    /// Instance ID (corresponding to FromInstance)
    /// </summary>
    public required string InstanceId { get; set; }

    /// <summary>
    /// Microservice display name
    /// </summary>
    public required string AppName { get; set; }

    /// <summary>
    /// Project name
    /// </summary>
    public required string ProjectName { get; set; }

    /// <summary>
    /// Subdomain name
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>Gets or sets the domains owned by this instance's selected roles. Empty for legacy registrations.</summary>
    public List<string> DomainNames { get; set; } = [];

    /// <summary>Gets or sets the exact application roles selected at host startup. This is observed placement, not a security boundary.</summary>
    public List<string> RoleIds { get; set; } = [];

    /// <summary>Returns selected domain identities, including the legacy singular registration field.</summary>
    public IEnumerable<string> GetDomainNames() => DomainNames.Append(DomainName)
        .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Microservice build time stored in UTC.
    /// </summary>
    public DateTime BuildTime { get; set; }

    /// <summary>
    /// Microservice assembly version number
    /// </summary>
    public string? AssemblyVersion { get; set; }

    /// <summary>
    /// Microservice release version number
    /// </summary>
    public string? ReleaseVersion { get; set; }

    /// <summary>
    /// Dependent subdomain list
    /// </summary>
    public List<string>? DependentSubDomains { get; set; }

    /// <summary>
    /// Registration time
    /// </summary>
    public DateTime RegistrationTime { get; set; }

    /// <summary>
    /// Last heartbeat time
    /// </summary>
    public DateTime LastHeartbeatTime { get; set; }

    /// <summary>
    /// Service instance metadata
    /// </summary>
    [JsonConverter(typeof(PreserveOriginalConverter<Dictionary<string, string>>))]
    public Dictionary<string, string> Metadata { get; set; } = new();
}
