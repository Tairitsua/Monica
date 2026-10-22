namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Represents an evicted service instance with eviction metadata.
/// Stores minimal data to reduce memory usage.
/// </summary>
public class EvictedInstanceInfo
{
    public required string InstanceId { get; set; }
    public required string AppId { get; set; }
    public required string AppName { get; set; }
    public required string ProjectName { get; set; }
    public string? DomainName { get; set; }
    public ServiceStatus Status { get; set; }
    public DateTime EvictionTime { get; set; }
    public DateTime LastHeartbeatTime { get; set; }
    public DateTime RegistrationTime { get; set; }
    public string? AssemblyVersion { get; set; }
    public string? ReleaseVersion { get; set; }
    public DateTime BuildTime { get; set; }
    public bool IsLeader { get; set; }

    /// <summary>
    /// Create a lightweight InstanceState for display purposes
    /// </summary>
    public InstanceState ToInstanceState()
    {
        return new InstanceState
        {
            InstanceId = InstanceId,
            AppId = AppId,
            AppName = AppName,
            ProjectName = ProjectName,
            DomainName = DomainName,
            Status = Status,
            LastHeartbeatTime = LastHeartbeatTime,
            RegistrationTime = RegistrationTime,
            AssemblyVersion = AssemblyVersion,
            ReleaseVersion = ReleaseVersion,
            BuildTime = BuildTime,
            IsLeader = IsLeader,
            // Other properties use defaults to minimize memory
            Metadata = new Dictionary<string, string>(),
        };
    }
}
