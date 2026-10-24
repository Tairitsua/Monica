using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

public sealed class ApplicationIdentityCompositionTests
{
    [Fact]
    public async Task ManualHost_ReadsCatalogWithoutRegistrationAndRejectsRegistryMutations()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new DiscoveryFactory(manual: true).CreateAsync(cancellationToken: token);
        var coordinator = app.Services.GetRequiredService<IServiceRegistrationCoordinator>();
        Assert.False(coordinator.IsRegistered);
        Assert.False(await coordinator.WaitForRegistrationAsync(Timeout.InfiniteTimeSpan, token));
        var manager = app.Services.GetRequiredService<IRegistrationStateManager>();
        Assert.Empty(await manager.GetAllInstancesAsync(token));
        Assert.Equal(3, (await app.Services.GetRequiredService<IServiceDiscoveryCatalogProvider>().GetPreloadedServicesAsync()).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RegisterOrHeartbeatAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryBecomeLeaderAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RenewLeaderLeaseAsync("etag", token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteLeaderKeyAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ForceDeleteLeaderKeyAsync("local", token));
        Assert.Empty(await manager.GetAllInstancesAsync(token));
    }
    [Fact]
    public async Task Discovery_WithCanonicalDeployment_ShouldRetainNamesAndApplicationsWithoutRoles()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var catalog = await app.Services.GetRequiredService<IServiceDiscoveryCatalogProvider>().GetPreloadedServicesAsync();

        Assert.Equal(["empty-remote", "external", "local"], catalog.Select(service => service.AppId));
        Assert.Equal(["Remote maintenance", "External service", "Local application"], catalog.Select(service => service.AppName));
        var instance = app.Services.GetRequiredService<IServiceDiscoveryClientInfo>().GetServiceStatus();
        Assert.Equal("local", instance.AppId);
        Assert.Equal("Local application", instance.AppName);
        Assert.Empty(instance.RoleIds);
    }

    [Theory]
    [InlineData("AppId")]
    [InlineData("AppName")]
    [InlineData("PredefinedServices")]
    [InlineData("CaseAppId")]
    [InlineData("DuplicateAppId")]
    public async Task Discovery_WithContradictoryIdentity_ShouldFailBeforeHostStartup(string conflict)
    {
        var factory = new DiscoveryFactory(conflict);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(conflict.EndsWith("AppId", StringComparison.Ordinal) ? "AppId" : "AppName", failure.ToString());
        Assert.False(factory.ServicesConfigured);
    }

    private sealed class DiscoveryFactory(string? conflict = null, bool manual = false) : MonicaTestApplicationFactory<ApplicationIdentityCompositionTests>
    {
        internal bool ServicesConfigured { get; private set; }

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            base.ConfigureHost(builder);
            builder.Configuration["Application:AutonomousWork"] = manual ? "false" : "true";
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = "local");
            // Declare discovery first to exercise call-order independence of canonical identity projection.
            builder.AddServiceDiscovery(options =>
            {
                options.PredefinedServices = [new PredefinedServiceInfo { AppId = "external", AppName = "External service" }];
                if (conflict == "AppId") options.AppId = "other";
                if (conflict == "AppName") options.AppName = "Conflicting local name";
                if (conflict == "PredefinedServices")
                    options.PredefinedServices.Add(new PredefinedServiceInfo { AppId = "empty-remote", AppName = "Conflicting remote name" });
                if (conflict == "CaseAppId")
                    options.PredefinedServices.Add(new PredefinedServiceInfo { AppId = "LOCAL", AppName = "Local application" });
                if (conflict == "DuplicateAppId")
                    options.PredefinedServices.Add(new PredefinedServiceInfo { AppId = "EXTERNAL", AppName = "External service" });
            }).AsStandalone().UseMemoryStorage();
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = typeof(EmptyManifest);
                options.Deployments = new Dictionary<string, ApplicationPlacement>
                {
                    ["local"] = new() { AppName = "Local application", Roles = [] },
                    ["empty-remote"] = new() { AppName = "Remote maintenance", Roles = [] }
                };
            });
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            ServicesConfigured = true;
            base.ConfigureServices(services);
        }
    }

    public sealed class EmptyManifest : IApplicationManifest
    {
        public IReadOnlyList<ApplicationDomainDefinition> Domains => [];
        public IReadOnlyList<ApplicationRoleDefinition> Roles => [];
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }
}
