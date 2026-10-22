using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Models;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Providers;
using Monica.ServiceDiscovery.Services;
using NSubstitute;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

public sealed class ServiceDiscoveryQueryServiceTests
{
    [Fact]
    public async Task Topology_PreservesApiEvidenceAcrossPlacementChangesAndReplicaMembership()
    {
        var model = Substitute.For<IApplicationModel>();
        model.Domains.Returns(new Dictionary<string, ApplicationDomainDefinition>
        {
            ["Flight"] = new("Flight", "Domains.Flight"),
            ["User"] = new("User", "Domains.User"),
            ["Unused"] = new("Unused", "Domains.Unused")
        });
        model.Roles.Returns(new Dictionary<string, ApplicationRoleDefinition>
        {
            ["flight.api"] = new("flight.api", "Flight", "Roles.Flight"),
            ["user.api"] = new("user.api", "User", "Roles.User")
        });
        model.Dependencies.Returns(new[] { new ApplicationDependencyDefinition("Roles.Flight", "Flight.FindOwner", typeof(FindUser)) });
        model.GetOperation(typeof(FindUser)).Returns(new ApplicationOperationDefinition(typeof(FindUser),
            "User.FindUserHandler", "Roles.User", "User", "user.api", null));
        var registry = Substitute.For<IRegistrationStateManager>();
        var instances = new List<InstanceState>();
        registry.GetAllInstancesAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(instances));
        using var provider = new ServiceCollection().AddSingleton(model).AddSingleton(registry).BuildServiceProvider();
        var query = new ServiceDiscoveryQueryService(provider);
        model.RoleApplications.Returns(new Dictionary<string, string> { ["flight.api"] = "combined", ["user.api"] = "combined" });
        var combined = query.GetApplicationTopology();
        combined.IsAvailable.Should().BeTrue();
        var edge = combined.Dependencies.Should().ContainSingle().Subject;
        edge.SourceDomainId.Should().Be("Flight");
        edge.TargetDomainId.Should().Be("User");
        edge.SourceType.Should().Be("Flight.FindOwner");

        model.RoleApplications.Returns(new Dictionary<string, string> { ["flight.api"] = "flight", ["user.api"] = "user" });
        instances.AddRange(Enumerable.Range(1, 3).Select(index => new InstanceState
        {
            AppId = "flight", AppName = "Flight API", ProjectName = "Product.Host", InstanceId = index.ToString(),
            DomainNames = ["Flight", "User", "Unused"], RoleIds = ["flight.api"]
        }));
        var registered = (await query.GetServicesStatusAsync()).Single();
        registered.TotalInstanceCount.Should().Be(3);
        query.GetApplicationTopology().Dependencies.Should().Equal(combined.Dependencies);
        edge.IsOwnedBy(registered).Should().BeTrue();
        registered.RoleIds.Clear();
        edge.IsOwnedBy(registered).Should().BeFalse("domain membership alone does not identify a calling role");
    }

    [Fact]
    public void Topology_WithoutApplicationModel_IsUnavailableInsteadOfAnEmptyDependencyClaim()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        new ServiceDiscoveryQueryService(provider).GetApplicationTopology().IsAvailable.Should().BeFalse();
    }

    private sealed record FindUser;

    [Fact]
    public void DomainMembership_PreservesOrdinalDomainIdentities()
    {
        var service = new RegisteredServiceStatus { AppId = "app", AppName = "App", DomainNames = ["User", "user"] };
        service.GetDomainNames().Should().Equal("User", "user");
        service.BelongsToDomain("USER").Should().BeFalse();
    }

    [Fact]
    public async Task GetDomainDetail_WhenReplicasDeclareSeveralDomains_ShouldRetainEveryMembershipAndDetachedEvidence()
    {
        var current = new InstanceState
        {
            AppId = "complete-app", AppName = "Complete application", ProjectName = "Host", InstanceId = "current",
            DomainNames = ["Flight", "User"], RoleIds = ["flight", "user.chat"]
        };
        var legacy = new InstanceState
        {
            AppId = current.AppId, AppName = current.AppName, ProjectName = current.ProjectName, InstanceId = "legacy",
            DomainName = "System"
        };
        var registry = Substitute.For<IRegistrationStateManager>();
        registry.GetAllInstancesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<InstanceState> { current, legacy }));
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton<IServiceDiscoveryCatalogProvider>(new DefaultServiceDiscoveryCatalogProvider());
        var domains = Substitute.For<global::Monica.Core.Domains.Abstractions.IDomainCatalog>();
        domains.Domains.Returns(new[] { "Flight", "User", "System" }.ToDictionary(id => id,
            id => new global::Monica.Core.Domains.Models.DomainInfo { Id = id }));
        services.AddSingleton(domains);
        using var provider = services.BuildServiceProvider();
        var query = new ServiceDiscoveryQueryService(provider);

        foreach (var domain in new[] { "Flight", "User", "System" })
        {
            var detail = await query.GetDomainDetailAsync(domain);
            var service = detail.RelatedServices.Should().ContainSingle().Subject;
            service.BelongsToDomain(domain).Should().BeTrue();
            service.GetDomainNames().Should().BeEquivalentTo("Flight", "User", "System");
            service.RoleIds.Should().BeEquivalentTo("flight", "user.chat");
        }
        var snapshot = (await query.GetServicesStatusAsync()).Single();
        current.RoleIds.Clear();
        current.DomainNames.Clear();
        snapshot.Instances["current"].RoleIds.Should().HaveCount(2);
        snapshot.Instances["current"].DomainNames.Should().HaveCount(2);
    }
}
