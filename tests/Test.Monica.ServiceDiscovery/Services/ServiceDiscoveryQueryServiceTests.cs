using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IServiceDiscoveryCatalogProvider>(new DefaultServiceDiscoveryCatalogProvider(registry));
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
