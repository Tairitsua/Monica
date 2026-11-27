using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Authority.Identity.Abstractions;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.SignalR.Abstractions;
using Monica.SignalR.Facades;
using Monica.SignalR.Models;
using Monica.SignalR.Metrics;
using Monica.Testing.Hosting;
using NSubstitute;
using Xunit;

namespace Test.Monica.SignalR.Facades;

public sealed class SignalRDebugCallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendDebugCallbackAsync_WhenMapped_ShouldSendOnlyToSelectedConnectionAndRespectRedaction(bool includeIdentifiers)
    {
        var factory = new ProbeFactory(includeIdentifiers);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<SignalRFacade>();
        var result = await facade.SendDebugCallbackAsync("/probe", "browser-id", nameof(ProbeHub.Echo), [42], TestContext.Current.CancellationToken);
        Assert.Equal(ResultStatus.Ok, result.Status);
        await factory.Client.Received(1).SendCoreAsync(nameof(ProbeHub.Echo), Arg.Is<object?[]>(values => values.Length == 1 && (int)values[0]! == 42), TestContext.Current.CancellationToken);
        factory.HubClients.Received(1).Client("browser-id");
        factory.HubClients.DidNotReceive().AllExcept(Arg.Any<IReadOnlyList<string>>());
        var snapshot = (await facade.GetSendDiagnosticsAsync()).Data!;
        Assert.Equal(includeIdentifiers, snapshot.IncludesTargetIdentifiers);
        Assert.Equal(1, snapshot.TotalCompletedCount);
        Assert.Equal(0, snapshot.TotalFailedCount);
        var target = Assert.Single(snapshot.Metrics);
        Assert.Equal(SignalRSendTargetKind.Client, target.TargetKind);
        Assert.Equal(1, target.TargetCount);
        Assert.Equal(includeIdentifiers ? ["browser-id"] : Array.Empty<string>(), target.TargetIdentifiers);
    }

    [Fact]
    public async Task SendDebugCallbackAsync_WhenMethodIsNotDeclared_ShouldFailWithoutSending()
    {
        var factory = new ProbeFactory(false);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<SignalRFacade>();
        var result = await facade.SendDebugCallbackAsync("/probe", "browser-id", "Missing", [], TestContext.Current.CancellationToken);
        Assert.Equal(ResultStatus.InternalError, result.Status);
        Assert.NotEmpty(result.Message!);
        Assert.Empty((await facade.GetSendDiagnosticsAsync()).Data!.Metrics);
        factory.HubClients.DidNotReceive().Client(Arg.Any<string>());
    }

    [Fact]
    public async Task SendDebugCallbackAsync_WhenTransportFails_ShouldRetainFailedObservation()
    {
        var factory = new ProbeFactory(false);
        factory.Client.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Transport failed.")));
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<SignalRFacade>();
        var result = await facade.SendDebugCallbackAsync("/probe", "browser-id", nameof(ProbeHub.Echo), [42], TestContext.Current.CancellationToken);
        Assert.Equal(ResultStatus.InternalError, result.Status);
        var snapshot = (await facade.GetSendDiagnosticsAsync()).Data!;
        Assert.Equal(1, snapshot.TotalStartedCount);
        Assert.Equal(1, snapshot.TotalFailedCount);
        Assert.Equal(0, snapshot.PendingSendCount);
        Assert.Equal(0, snapshot.TotalCompletedCount);
    }

    private sealed class ProbeFactory(bool includeIdentifiers) : MonicaTestApplicationFactory<SignalRDebugCallbackTests>
    {
        public ISingleClientProxy Client { get; } = Substitute.For<ISingleClientProxy>();
        public IHubClients HubClients { get; } = Substitute.For<IHubClients>();
        protected override void ConfigureMonica(IMonicaBuilder builder) => builder.AddSignalR(options =>
            { options.EnableSendMetrics = true; options.IncludeSendDiagnosticTargetIdentifiers = includeIdentifiers; })
            .AddSignalR<ISignalRHubOperator<IProbeClient, ICurrentUser>, ProbeOperator, IProbeClient, ICurrentUser>()
            .MapSignalRHub<ProbeHub>("/probe");
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            var context = Substitute.For<IHubContext<ProbeHub>>();
            context.Clients.Returns(HubClients);
            HubClients.Client(Arg.Any<string>()).Returns(Client);
            services.AddSingleton(context);
        }
    }
    public interface IProbeClient : ISignalRHubContract { Task Echo(int value); }
    public sealed class ProbeHub(ISignalRConnectionRegistry registry, ILogger<ProbeHub> logger) : SignalRHub<IProbeClient>(registry, logger)
    { public Task Echo(int value) => Task.CompletedTask; }
    public sealed class ProbeOperator(IHubContext<ProbeHub, IProbeClient> context, ISignalRConnectionRegistry registry, SignalRSendMetrics metrics)
        : CurrentUserSignalRHubOperator<IProbeClient, ProbeHub>(context, registry, metrics);
}
