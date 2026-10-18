namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Detaches discovery evidence from mutable provider and refresh objects.
/// Copies preserve timestamps and heartbeat-reported leadership; they do not infer health or lease ownership.
/// </summary>
public static class ServiceDiscoverySnapshot
{
    /// <summary>Copies a service, its registrations, and the UI's retained disappearance observations.</summary>
    public static RegisteredServiceStatus Copy(RegisteredServiceStatus source) => new()
    {
        AppId = source.AppId,
        AppName = source.AppName,
        DomainName = source.DomainName,
        DomainNames = source.DomainNames.ToList(),
        RoleIds = source.RoleIds.ToList(),
        ProjectName = source.ProjectName,
        DependentSubDomains = source.DependentSubDomains?.ToList(),
        Instances = source.Instances.ToDictionary(pair => pair.Key, pair => Copy(pair.Value), source.Instances.Comparer),
        EvictedInstances = source.EvictedInstances.Select(item => new EvictedInstanceInfo
        {
            AppId = item.AppId, InstanceId = item.InstanceId, AppName = item.AppName,
            ProjectName = item.ProjectName, DomainName = item.DomainName, Status = item.Status,
            EvictionTime = item.EvictionTime, LastHeartbeatTime = item.LastHeartbeatTime,
            RegistrationTime = item.RegistrationTime, AssemblyVersion = item.AssemblyVersion,
            ReleaseVersion = item.ReleaseVersion, BuildTime = item.BuildTime, IsLeader = item.IsLeader
        }).ToList()
    };

    /// <summary>Copies a registration, including independently owned metadata and dependency collections.</summary>
    public static InstanceState Copy(InstanceState source) => new()
    {
        AppId = source.AppId, InstanceId = source.InstanceId, AppName = source.AppName,
        ProjectName = source.ProjectName, DomainName = source.DomainName,
        DomainNames = source.DomainNames.ToList(), RoleIds = source.RoleIds.ToList(),
        Status = source.Status, IsLeader = source.IsLeader, BuildTime = source.BuildTime,
        AssemblyVersion = source.AssemblyVersion, ReleaseVersion = source.ReleaseVersion,
        RegistrationTime = source.RegistrationTime, LastHeartbeatTime = source.LastHeartbeatTime,
        DependentSubDomains = source.DependentSubDomains?.ToList(),
        Metadata = new Dictionary<string, string>(source.Metadata, source.Metadata.Comparer)
    };
}
