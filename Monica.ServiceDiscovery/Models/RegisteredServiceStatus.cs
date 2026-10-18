namespace Monica.ServiceDiscovery.Models;

public class RegisteredServiceStatus
{
    /// <summary>ServiceAppId</summary>
    public required string AppId { get; set; }
    
    /// <summary>Service name</summary>
    public required string AppName { get; set; }
    
    /// <summary>Domain name</summary>
    public string? DomainName { get; set; }

    /// <summary>Gets or sets domains observed across the application's registered instances.</summary>
    public List<string> DomainNames { get; set; } = [];

    /// <summary>Gets or sets roles observed across registered instances. Inspect each instance during mixed-version rollouts.</summary>
    public List<string> RoleIds { get; set; } = [];

    /// <summary>Returns observed domain identities, including the legacy singular registration field.</summary>
    public IEnumerable<string> GetDomainNames() => DomainNames.Append(DomainName)
        .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any observed instance declares the domain; this does not establish process readiness.</summary>
    public bool BelongsToDomain(string? domainName) => domainName is not null &&
        GetDomainNames().Contains(domainName, StringComparer.OrdinalIgnoreCase);
    
    /// <summary>Project name</summary>
    public string? ProjectName { get; set; }
    /// <summary>
    /// Dependent subdomain list
    /// </summary>
    public List<string>? DependentSubDomains { get; set; }
    /// <summary>Service instance dictionary (Key: FromClient, Value: InstanceState)</summary>
    public Dictionary<string, InstanceState> Instances { get; set; } = new();

    /// <summary>Evicted instances (UI layer only, not serialized)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<EvictedInstanceInfo> EvictedInstances { get; set; } = new();
    
    /// <summary>Get the number of running instances</summary>
    public int RunningInstanceCount => 
        Instances.Count(x => x.Value.Status == ServiceStatus.Running);
    
    /// <summary>Get the total number of instances</summary>
    public int TotalInstanceCount => Instances.Count;
    
    /// <summary>The overall status of the service (based on the status of all instances)</summary>
    public ServiceStatus OverallStatus => DetermineOverallStatus();

    /// <summary>
    /// Get valid service instance information
    /// </summary>
    /// <returns></returns>
    public InstanceState? GetValidInstanceInfo() => Instances.Values.FirstOrDefault(x => x.Status is not ServiceStatus.Offline);

    private ServiceStatus DetermineOverallStatus()
    {
        if (!Instances.Any())
            return ServiceStatus.Offline;

        var statuses = Instances.Values.Select(x => x.Status).ToList();

        // Priority: Error > Running > Unhealthy > Updating > Offline
        if (statuses.Any(s => s == ServiceStatus.Error))
            return ServiceStatus.Error;

        if (statuses.Any(s => s == ServiceStatus.Running))
            return ServiceStatus.Running;

        if (statuses.Any(s => s == ServiceStatus.Unhealthy))
            return ServiceStatus.Unhealthy;

        if (statuses.Any(s => s == ServiceStatus.Updating))
            return ServiceStatus.Updating;

        return ServiceStatus.Offline;
    }
}
