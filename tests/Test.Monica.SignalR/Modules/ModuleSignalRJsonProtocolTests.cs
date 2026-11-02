using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Authority.Identity.Abstractions;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.SignalR.Abstractions;
using Monica.SignalR.Metrics;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.SignalR.Modules;

public sealed class ModuleSignalRJsonProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_WhenDebugModeChanges_ShouldConfigureGlobalAndMappedHubErrors(bool enabled)
    {
        await using var application = await new DebugFactory(enabled).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(enabled, application.Services.GetRequiredService<IOptions<HubOptions>>().Value.EnableDetailedErrors);
        Assert.Equal(enabled, application.Services.GetRequiredService<IOptions<HubOptions<TestHub<IFirstClientContract>>>>().Value.EnableDetailedErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_WhenNativeHubErrorsContradictPolicy_ShouldRejectConfiguration(bool typedOverride)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new DebugFactory(false, typedOverride).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Application:DebugMode", error.ToString());
    }

    private sealed class DebugFactory(bool enabled, bool? typedOverride = null)
        : MonicaTestApplicationFactory<ModuleSignalRJsonProtocolTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.DebugMode = enabled);
            builder.AddSignalR()
                .AddSignalR<ISignalRHubOperator<IFirstClientContract, ICurrentUser>, TestHubOperator<IFirstClientContract>,
                    IFirstClientContract, ICurrentUser>()
                .MapSignalRHub<TestHub<IFirstClientContract>>("/debug-policy-test");
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            if (typedOverride == true)
                services.Configure<HubOptions<TestHub<IFirstClientContract>>>(options => options.EnableDetailedErrors = !enabled);
            else if (typedOverride == false)
                services.Configure<HubOptions>(options => options.EnableDetailedErrors = !enabled);
        }
    }

    [Fact]
    public async Task AddSignalR_WhenTwoRegistrationsConfigureJsonProtocol_ShouldComposeBeforeSealing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddJsonSerialization(options =>
                options.ConfigureSerializer(static serializerOptions => serializerOptions.MaxDepth = 17));

            var signalR = monica.AddSignalR();
            signalR.AddSignalR<
                ISignalRHubOperator<IFirstClientContract, ICurrentUser>,
                TestHubOperator<IFirstClientContract>,
                IFirstClientContract,
                ICurrentUser>(jsonConfigure: static options =>
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
            signalR.AddSignalR<
                ISignalRHubOperator<ISecondClientContract, ICurrentUser>,
                TestHubOperator<ISecondClientContract>,
                ISecondClientContract,
                ICurrentUser>(jsonConfigure: static options =>
                    options.PayloadSerializerOptions.WriteIndented = true);
        });

        await using var application = builder.Build();
        var payloadOptions = application.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>()
            .Value
            .PayloadSerializerOptions;

        payloadOptions.MaxDepth.Should().Be(17);
        payloadOptions.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.SnakeCaseLower);
        payloadOptions.WriteIndented.Should().BeTrue();
        payloadOptions.IsReadOnly.Should().BeTrue();
    }

    private interface IFirstClientContract : ISignalRHubContract;

    private interface ISecondClientContract : ISignalRHubContract;

    private sealed class TestHub<TContract>(
        ISignalRConnectionRegistry connectionRegistry,
        ILogger<TestHub<TContract>> logger)
        : SignalRHub<TContract>(connectionRegistry, logger)
        where TContract : class, ISignalRHubContract;

    private sealed class TestHubOperator<TContract>(
        IHubContext<TestHub<TContract>, TContract> hubContext,
        ISignalRConnectionRegistry connectionRegistry,
        SignalRSendMetrics sendMetrics)
        : CurrentUserSignalRHubOperator<TContract, TestHub<TContract>>(
            hubContext,
            connectionRegistry,
            sendMetrics)
        where TContract : class, ISignalRHubContract;
}
