using AwesomeAssertions;
using Monica.ServiceDiscovery.Models;
using Xunit;

namespace Test.Monica.Framework.UI.ServiceDiscovery;

public sealed class ServiceDiscoverySnapshotTests
{
    [Fact]
    public void Copy_WhenInspectorContainsCurrentAndEvictedMembers_ShouldOwnAllMutableEvidence()
    {
        var registration = ServiceDiscoveryCaptureContractTests.CreateInstance();
        var eviction = new EvictedInstanceInfo
        {
            AppId = registration.AppId,
            AppName = "Previous service identity",
            InstanceId = "departed-instance",
            ProjectName = registration.ProjectName,
            DomainName = registration.DomainName,
            Status = ServiceStatus.Offline,
            IsLeader = true,
            EvictionTime = new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc),
            LastHeartbeatTime = registration.LastHeartbeatTime,
            RegistrationTime = registration.RegistrationTime,
            BuildTime = registration.BuildTime,
            AssemblyVersion = registration.AssemblyVersion,
            ReleaseVersion = registration.ReleaseVersion
        };
        var source = new RegisteredServiceStatus
        {
            AppId = registration.AppId,
            AppName = registration.AppName,
            ProjectName = registration.ProjectName,
            DomainName = registration.DomainName,
            Instances = new Dictionary<string, InstanceState>(StringComparer.OrdinalIgnoreCase)
            {
                [registration.InstanceId] = registration
            },
            EvictedInstances = [eviction]
        };

        var captured = ServiceDiscoverySnapshot.Copy(source);
        registration.Metadata["TRACE"] = "new provider evidence";
        registration.Status = ServiceStatus.Running;
        registration.BuildTime = registration.BuildTime.AddHours(1);
        registration.AssemblyVersion = "2.0.0";
        registration.ReleaseVersion = "release-2";
        eviction.AppName = "Changed retained identity";
        eviction.EvictionTime = eviction.EvictionTime.AddHours(1);
        source.Instances.Clear();
        source.EvictedInstances.Clear();

        captured.ProjectName.Should().Be("Operations");
        captured.DomainName.Should().Be("Dispatch");
        var member = captured.Instances["REMOTE-INSTANCE"];
        member.Metadata["TRACE"].Should().Be("original evidence");
        member.Status.Should().Be(ServiceStatus.Error);
        member.IsLeader.Should().BeTrue();
        member.BuildTime.Should().Be(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        member.AssemblyVersion.Should().Be("1.2.3");
        member.ReleaseVersion.Should().Be("release-1");
        member.RegistrationTime.Should().Be(new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));
        member.LastHeartbeatTime.Should().Be(new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc));
        var retained = captured.EvictedInstances.Should().ContainSingle().Which;
        retained.AppName.Should().Be("Previous service identity");
        retained.EvictionTime.Should().Be(new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc));
        retained.IsLeader.Should().BeTrue();
        retained.Status.Should().Be(ServiceStatus.Offline);
        retained.BuildTime.Should().Be(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        retained.AssemblyVersion.Should().Be("1.2.3");
        retained.ReleaseVersion.Should().Be("release-1");
        retained.RegistrationTime.Should().Be(member.RegistrationTime);
        retained.LastHeartbeatTime.Should().Be(member.LastHeartbeatTime);

        member.Metadata["trace"] = "inspector-local evidence";
        registration.Metadata["trace"].Should().Be("new provider evidence");
    }
}
