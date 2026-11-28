using AwesomeAssertions;
using Monica.ApplicationModel.Models;
using Monica.Core.ApplicationRoles;
using Monica.Core.Domains.Models;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.UIServiceDiscovery.State;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

public sealed class DiscoveryEvidenceTests
{
    [Theory]
    [InlineData(null, "unknown", null)]
    [InlineData(false, "remote-artifact", "target-faulted")]
    [InlineData(true, "local-ordinary", null)]
    public void Ordinary_peer_route_uses_the_reporting_artifact_and_preserves_missing_evidence(
        bool? includesTarget, string route, string? risk)
    {
        var at = DateTime.UnixEpoch.AddHours(1);
        var host = Instance("query", "query-1", at);
        var source = Instance("peer", "peer-1", at);
        var target = Instance("target", "target-1", at);
        if (includesTarget is { } known)
            source.Diagnostics = new("Peer.Artifact", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(46),
                known ? ["caller", "callee"] : ["caller"], []);
        target.Diagnostics = new("Receiver.Artifact", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(46),
            ["callee"], [new("callee", RoleHealthStatus.Faulted, at)]);
        var topology = new ApplicationTopologySnapshot(true, [new("Sender", "caller", "Receiver", "callee", "Query", "Caller")])
        {
            Deployment = ApplicationDeployment.Create(host.AppId, new Dictionary<string, ApplicationPlacement>
            {
                [host.AppId] = new() { Roles = [] },
                [source.AppId] = new() { Roles = ["caller"] },
                [target.AppId] = new() { Roles = ["callee"] }
            }),
            Roles = new Dictionary<string, ApplicationRoleDefinition>
            {
                ["caller"] = new("caller", "Sender", "Sender.Role"),
                ["callee"] = new("callee", "Receiver", "Receiver.Role")
            }
        };

        var evidence = new DiscoveryEvidence(at, new ElectionConfig(), [Registered(source), Registered(target)],
            [new DomainInfo { Id = "Sender" }, new DomainInfo { Id = "Receiver" }], topology,
            new CurrentInstanceSnapshot { CurrentInstance = host });

        var call = evidence.Calls.Should().ContainSingle().Subject;
        call.Route.Should().Be(route);
        call.Risk.Should().Be(risk);
        evidence.RoleById["callee"].Provenance.Should().Be("Heartbeat");
    }

    [Fact]
    public void Heartbeat_uses_the_reporting_instances_policy_when_the_querying_host_differs()
    {
        var at = DateTime.UnixEpoch.AddHours(1);
        var state = Instance("peer", "peer-1", at.AddSeconds(-18));
        var policy = new ElectionConfig();
        var legacy = new InstanceRow(state, at, policy);
        state.Diagnostics = new(null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(16), null, null);
        var detached = new InstanceRow(ServiceDiscoverySnapshot.Copy(state), at, policy);
        state.Diagnostics = null;

        legacy.Health.Should().Be("Healthy");
        detached.Health.Should().Be("Offline");
        detached.Period.Should().Be(5);
        detached.Ttl.Should().Be(16);
    }

    private static InstanceState Instance(string app, string id, DateTime heartbeat) => new()
    {
        AppId = app, InstanceId = id, AppName = app, ProjectName = "Test", LastHeartbeatTime = heartbeat
    };

    private static RegisteredServiceStatus Registered(InstanceState instance) => new()
    {
        AppId = instance.AppId, AppName = instance.AppName, Instances = new() { [instance.InstanceId] = instance }
    };
}
