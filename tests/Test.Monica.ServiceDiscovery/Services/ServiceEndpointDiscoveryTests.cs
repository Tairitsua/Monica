using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ServiceDiscovery;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;
using Monica.StateStore.Abstractions;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

public sealed class ServiceEndpointDiscoveryTests
{
    [Fact]
    public async Task Compose_WhenOnlyEndpointDiscoveryIsEnabled_ShouldResolveWithoutStateStoreOrRegistration()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(app.Services.GetService<IServiceRegistrationCoordinator>());
        Assert.Null(app.Services.GetService<IStateStore>());
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        Assert.Equal("Unknown", Assert.Single(facade.Capture().Data!.Targets).Resolution);
        await facade.RefreshAsync(TestContext.Current.CancellationToken);
        var target = Assert.Single(facade.Capture().Data!.Targets);
        Assert.Equal("Resolved", target.Resolution);
        Assert.Equal("https://gateway.test:8443", Assert.Single(target.Candidates).Address);
        Assert.Contains("Configuration", target.Candidates[0].Provider);
        Assert.Empty(target.Calls);
        Assert.Empty(app.Services.GetRequiredService<CaptureTransport>().Uris);
    }

    [Fact]
    public async Task Send_WhenNativeDiscoverySelectsEndpoint_ShouldPreservePathPrefixAndSanitizeEvidence()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("discovery");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/secret-id?token=secret");
        request.Headers.Add("Authorization", "Bearer secret");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("https://gateway.test:8443/edge/api/orders/secret-id?token=secret",
            Assert.Single(app.Services.GetRequiredService<CaptureTransport>().Uris));
        Assert.Equal("https://orders/api/orders/secret-id?token=secret", request.RequestUri!.AbsoluteUri);
        var observation = Assert.Single(Assert.Single(app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>()
            .Capture().Data!.Targets).Calls);
        Assert.Equal("https://gateway.test:8443", observation.Endpoint);
        Assert.Equal(200, observation.StatusCode);
        Assert.Equal("HttpSuccess", observation.Outcome);
    }

    [Fact]
    public async Task Send_WhenTransportFails_ShouldRetainSelectedEndpointAndBoundHistory()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var transport = app.Services.GetRequiredService<CaptureTransport>();
        transport.Fail = true;
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("discovery");
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", TestContext.Current.CancellationToken));
        var calls = Assert.Single(app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>().Capture().Data!.Targets).Calls;
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => { Assert.Equal("Failure", c.Outcome); Assert.NotNull(c.Endpoint); });
        Assert.Equal(3, transport.Uris.Count);
    }

    [Fact]
    public async Task Resolve_WhenNoConfigurationExists_ShouldNotSilentlyUseDnsPassThrough()
    {
        await using var app = await new DiscoveryFactory(configureEndpoint: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        await facade.RefreshAsync(TestContext.Current.CancellationToken);
        var target = Assert.Single(facade.Capture().Data!.Targets);
        Assert.Equal("Empty", target.Resolution);
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("discovery");
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/", TestContext.Current.CancellationToken));
        Assert.Empty(app.Services.GetRequiredService<CaptureTransport>().Uris);
        Assert.Null(Assert.Single(Assert.Single(facade.Capture().Data!.Targets).Calls).Endpoint);
    }

    [Fact]
    public async Task Refresh_WhenCallerIsCanceled_ShouldPropagateWithoutCreatingObservations()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.RefreshAsync(canceled.Token));
        Assert.Equal("Unknown", Assert.Single(facade.Capture().Data!.Targets).Resolution);
    }

    [Fact]
    public async Task Resolve_WhenConfigurationReloads_ShouldFollowNativeChangeToken()
    {
        await using var app = await new DiscoveryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var resolver = app.Services.GetRequiredService<ServiceEndpointResolver>();
        var previous = await resolver.GetEndpointsAsync("https://orders/", TestContext.Current.CancellationToken);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = previous.ChangeToken.RegisterChangeCallback(_ => changed.TrySetResult(), null);
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        configuration["Services:orders:https:0"] = "https://changed.test:9443";
        ((IConfigurationRoot)configuration).Reload();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        await facade.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("https://changed.test:9443", Assert.Single(Assert.Single(facade.Capture().Data!.Targets).Candidates).Address);
    }

    [Fact]
    public async Task Capture_WhenConfiguredEndpointContainsCredentials_ShouldExcludeCredentialsAndPaths()
    {
        await using var app = await new DiscoveryFactory(endpoint: "https://user:secret@gateway.test:8443/private")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        await facade.RefreshAsync(TestContext.Current.CancellationToken);
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("discovery");
        using var response = await client.GetAsync("/secret-path?token=secret", TestContext.Current.CancellationToken);
        var json = System.Text.Json.JsonSerializer.Serialize(facade.Capture().Data);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("user:", json);
    }

    [Fact]
    public async Task Resolve_WhenPassThroughIsExplicit_ShouldReportDnsEvidenceWithoutNetworkCall()
    {
        await using var app = await new DiscoveryFactory(configureEndpoint: false, passThrough: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        await facade.RefreshAsync(TestContext.Current.CancellationToken);
        var candidate = Assert.Single(Assert.Single(facade.Capture().Data!.Targets).Candidates);
        Assert.Contains("PassThrough", candidate.Provider);
        Assert.StartsWith("orders:", candidate.Address);
        Assert.Empty(app.Services.GetRequiredService<CaptureTransport>().Uris);
    }

    private sealed class DiscoveryFactory(bool configureEndpoint = true, bool passThrough = false,
        string endpoint = "https://gateway.test:8443/edge") : MonicaTestApplicationFactory<ServiceEndpointDiscoveryTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            if (configureEndpoint)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Services:orders:https:0"] = endpoint
                });
        }

        protected override void ConfigureMonica(IMonicaBuilder monica) =>
            monica.AddServiceEndpointDiscovery(o =>
            {
                o.MaxCallsPerTarget = 2;
                o.EnablePassThrough = passThrough;
                o.Targets.Add(new ServiceEndpointTarget("primary", "orders-app", new Uri("https://orders")));
            });

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<CaptureTransport>();
            services.AddHttpClient("discovery", c => c.BaseAddress = new Uri("https://orders"))
                .AddMonicaServiceDiscovery()
                .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<CaptureTransport>());
        }
    }

    private sealed class CaptureTransport : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.AbsoluteUri);
            if (Fail) throw new HttpRequestException("Boundary failure containing sensitive upstream text.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
