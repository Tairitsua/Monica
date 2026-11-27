using Monica.Core.ApplicationRoles;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.State;

internal sealed class DiscoveryEvidence
{
    public DiscoveryEvidence(DateTime at, ElectionConfig election, IReadOnlyList<RegisteredServiceStatus> services,
        IReadOnlyList<DomainInfo> catalog, ApplicationTopologySnapshot topology, CurrentInstanceSnapshot host)
    {
        At = at;
        Election = election;
        Host = host;
        Topology = topology;
        Services = services.Select(service => new ServiceRow(service, at, election))
            .OrderByDescending(row => row.Attention).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ServiceById = Services.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        var localHealth = topology.LocalRoleHealth?.Roles.Where(role => role.IsActiveOnCurrentHost)
            .ToDictionary(role => role.RoleId, StringComparer.Ordinal);
        Roles = topology.Roles.Values.Select(role =>
        {
            var appId = topology.Deployment!.RoleApplications.GetValueOrDefault(role.Id);
            var service = appId is null ? null : ServiceById.GetValueOrDefault(appId);
            RoleHealthEntrySnapshot? local = null;
            localHealth?.TryGetValue(role.Id, out local);
            var summaries = service?.Instances.Select(instance => instance.State.Diagnostics?.RoleHealth?
                .FirstOrDefault(health => health.RoleId == role.Id)).OfType<RegisteredRoleHealth>().ToArray() ?? [];
            var worst = summaries.OrderByDescending(summary => summary.Status).ThenByDescending(summary => summary.LastTransitionAt).FirstOrDefault();
            return new RoleRow(role.Id, role.DomainId, appId, local?.EffectiveCriticality ?? role.Criticality,
                local?.Status ?? worst?.Status, local, worst, RoleHealthSource(local, worst, service), service);
        }).OrderBy(role => role.Id, StringComparer.Ordinal).ToArray();
        RoleById = Roles.ToDictionary(role => role.Id, StringComparer.Ordinal);
        var known = catalog.Select(domain => domain.Id).ToHashSet(StringComparer.Ordinal);
        Calls = topology.Dependencies.SelectMany(dependency => DescribeCalls(dependency, known)).ToArray();
        var catalogById = catalog.ToDictionary(domain => domain.Id, StringComparer.Ordinal);
        Domains = known.Concat(Services.SelectMany(service => service.Domains))
            .Concat(Calls.SelectMany(call => new[] { call.SourceDomain, call.TargetDomain }))
            .Distinct(StringComparer.Ordinal).Select(id => new DomainRow(id, catalogById.GetValueOrDefault(id),
                Services.Where(service => !service.Offline && service.Domains.Contains(id)).ToArray(),
                Roles.Where(role => role.Domain == id).ToArray(),
                Calls.Where(call => call.TargetDomain == id && call.SourceDomain != id).ToArray(),
                Calls.Where(call => call.SourceDomain == id && call.TargetDomain != id).ToArray()))
            .OrderBy(domain => domain.Id, StringComparer.Ordinal).ToArray();
        LocalRegistration = ServiceById.GetValueOrDefault(host.CurrentInstance.AppId)?.Instances.FirstOrDefault(
            instance => instance.State.InstanceId == host.CurrentInstance.InstanceId);
        LocalInstance = new InstanceRow(host.CurrentInstance, at, election);
        Agreement = host.ClusterLeaderState switch
        {
            null when host.IsLeader => "split",
            null => "none",
            { } holder when holder.InstanceId == host.CurrentInstance.InstanceId => host.IsLeader ? "match" : "split",
            _ => host.IsLeader ? "split" : "sibling"
        };
    }

    public DateTime At { get; }
    public ElectionConfig Election { get; }
    public CurrentInstanceSnapshot Host { get; }
    public ApplicationTopologySnapshot Topology { get; }
    public IReadOnlyList<ServiceRow> Services { get; }
    public IReadOnlyDictionary<string, ServiceRow> ServiceById { get; }
    public IReadOnlyList<RoleRow> Roles { get; }
    public IReadOnlyDictionary<string, RoleRow> RoleById { get; }
    public IReadOnlyList<CallRow> Calls { get; }
    public IReadOnlyList<DomainRow> Domains { get; }
    public InstanceRow LocalInstance { get; }
    public InstanceRow? LocalRegistration { get; }
    public string Agreement { get; }
    public int Attention => Services.Count(service => service.Attention);
    public int HostedCatalog => Domains.Count(domain => domain.Catalog is not null && domain.Hosts.Count != 0);

    public IEnumerable<CallPair> Pairs(bool roles) => Calls
        .Where(call => roles ? call.SourceRole != call.TargetRole : call.SourceDomain != call.TargetDomain)
        .GroupBy(call => roles ? (call.SourceRole ?? call.SourceDomain, call.TargetRole) : (call.SourceDomain, call.TargetDomain))
        .Select(group => new CallPair(group.Key.Item1, group.Key.Item2, group.ToArray()))
        .OrderByDescending(pair => pair.Severity).ThenByDescending(pair => pair.Remote)
        .ThenBy(pair => pair.Source, StringComparer.Ordinal).ThenBy(pair => pair.Target, StringComparer.Ordinal);

    private static string RoleHealthSource(RoleHealthEntrySnapshot? local, RegisteredRoleHealth? remote, ServiceRow? service) =>
        local is not null ? "Local" : remote is not null ? "Heartbeat" : service?.Offline != false ? "Offline" : "Unknown";

    private IEnumerable<CallRow> DescribeCalls(DomainDependencyInfo dependency, HashSet<string> known)
    {
        var target = RoleById.GetValueOrDefault(dependency.TargetRoleId);
        var sourceApps = dependency.SourceRoleId is { } id
            ? new[] { RoleById.GetValueOrDefault(id)?.App }
            : Roles.Where(role => role.Domain == dependency.SourceDomainId).Select(role => role.App).Distinct().DefaultIfEmpty();
        foreach (var source in sourceApps)
        {
            var binding = SourceBinding(source, dependency);
            var route = dependency.RequiredRole is not null
                ? source is null || target?.App is null ? "unknown"
                    : source == target.App ? "local-runonrole" : "remote-runonrole"
                : binding switch { true => "local-ordinary", false => "remote-artifact", _ => "unknown" };
            var remote = route.StartsWith("remote-", StringComparison.Ordinal);
            var admission = route is "remote-runonrole" or "local-runonrole" or "remote-artifact";
            var risk = remote && target?.Service?.Offline != false ? "target-offline"
                : admission && target?.Status == RoleHealthStatus.Faulted ? "target-faulted"
                : admission && target?.Status == RoleHealthStatus.Degraded ? "target-degraded"
                : !known.Contains(dependency.SourceDomainId) || !known.Contains(dependency.TargetDomainId) ? "unlisted" : null;
            yield return new CallRow(dependency, source, target?.App, route, risk,
                target?.Service?.Instances.Count ?? 0);
        }
    }

    private bool? SourceBinding(string? source, DomainDependencyInfo dependency)
    {
        if (source is null) return null;
        if (source == Topology.Deployment?.AppId) return dependency.IsLocallyAvailable;
        if (!ServiceById.TryGetValue(source, out var service) || service.Offline) return null;
        var observed = service.Instances.Select(instance => instance.State.Diagnostics?.IncludedRoles?
            .Contains(dependency.TargetRoleId, StringComparer.Ordinal)).Distinct().ToArray();
        return observed.Length == 1 ? observed[0] : null;
    }
}

internal sealed record InstanceRow(InstanceState State, DateTime At, ElectionConfig Election)
{
    public double Period => State.Diagnostics?.HeartbeatPeriod.TotalSeconds ?? Election.HeartbeatPeriod.TotalSeconds;
    public double Ttl => State.Diagnostics?.RegistrationTtl.TotalSeconds ?? Election.RegistrationTTL.TotalSeconds;
    public double Warn => Period * 1.5;
    public double? Age => State.LastHeartbeatTime == default ? null :
        Math.Max(0, (At - State.LastHeartbeatTime).TotalSeconds);
    public string Health => Age is not { } age ? "Unknown" : age >= Ttl ? "Offline" : age >= Warn ? "Unhealthy" : "Healthy";
    public string? Version => State.AssemblyVersion ?? State.ReleaseVersion;
}

internal sealed class ServiceRow
{
    public ServiceRow(RegisteredServiceStatus state, DateTime at, ElectionConfig election)
    {
        State = state;
        Domains = state.GetDomainNames().Order(StringComparer.Ordinal).ToArray();
        Instances = state.Instances.Values.Select(instance => new InstanceRow(instance, at, election))
            .OrderByDescending(instance => instance.State.IsLeader).ThenBy(instance => instance.State.InstanceId, StringComparer.Ordinal).ToArray();
        Versions = Instances.Select(instance => instance.Version).OfType<string>().Distinct(StringComparer.Ordinal).Order().ToArray();
        Oldest = Instances.Where(instance => instance.Age is not null).MaxBy(instance => instance.Age);
    }

    public RegisteredServiceStatus State { get; }
    public string Id => State.AppId;
    public string Name => State.AppName;
    public IReadOnlyList<string> Domains { get; }
    public IReadOnlyList<InstanceRow> Instances { get; }
    public IReadOnlyList<string> Versions { get; }
    public InstanceRow? Oldest { get; }
    public bool Offline => Instances.Count == 0;
    public bool Skew => Versions.Count > 1;
    public bool Overdue => Instances.Any(instance => instance.Health is "Unhealthy" or "Offline");
    public bool Attention => Offline || Overdue || Skew || State.OverallStatus == ServiceStatus.Error || State.EvictedInstances.Count > 0;
}

internal sealed record RoleRow(string Id, string Domain, string? App, RoleCriticality Criticality,
    RoleHealthStatus? Status, RoleHealthEntrySnapshot? Local, RegisteredRoleHealth? Heartbeat, string Provenance, ServiceRow? Service);

/// <summary>A compiled use projected against one source deployment and observed evidence.</summary>
public sealed record CallRow(DomainDependencyInfo Definition, string? SourceApp, string? TargetApp,
    string Route, string? Risk, int TargetInstances)
{
    public string? SourceRole => Definition.SourceRoleId;
    public string TargetRole => Definition.TargetRoleId;
    public string SourceDomain => Definition.SourceDomainId;
    public string TargetDomain => Definition.TargetDomainId;
    public bool Remote => Route is "remote-runonrole" or "remote-artifact";
    public int Severity => Risk switch { "target-offline" or "target-faulted" => 2, null => 0, _ => 1 };
}

internal sealed record CallPair(string Source, string Target, IReadOnlyList<CallRow> Calls)
{
    public string Key => $"{Source}\u2192{Target}";
    public bool Remote => Calls.Any(call => call.Remote);
    public int Severity => Calls.Max(call => call.Severity);
}

internal sealed record DomainRow(string Id, DomainInfo? Catalog, IReadOnlyList<ServiceRow> Hosts,
    IReadOnlyList<RoleRow> Roles, IReadOnlyList<CallRow> Inbound, IReadOnlyList<CallRow> Outbound)
{
    public string Coverage => Catalog is null ? "unlisted" : Hosts.Count switch { 0 => "unhosted", 1 => "single", _ => "shared" };
}
