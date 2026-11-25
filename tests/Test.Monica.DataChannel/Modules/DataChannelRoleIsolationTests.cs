using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.ApplicationRoles;
using Monica.Core.Modularity.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Default;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.DataChannel.Modules;

public sealed class DataChannelRoleIsolationTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public async Task StartupFailure_RetiresEveryOwnedEndpointAndStartsTheOtherSetup(bool failDeclaration, int expectedDisposals)
    {
        var scenario = new Scenario { FailDeclaration = failDeclaration, FailMaterialization = !failDeclaration };
        await using var host = await new Factory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var channels = host.Services.GetRequiredService<IDataChannelManager>();
        var lifecycle = host.Services.GetRequiredService<IDataChannelLifecycle>();
        Assert.True(Assert.Single(channels.FetchAll()).Pipe.IsInitialized);
        Assert.Equal("healthy", Assert.Single(channels.FetchAll()).Id);
        Assert.Equal(expectedDisposals, scenario.Disposals);
        Assert.Equal(RoleFaultStage.PipelineSetup, scenario.Fault?.Stage);
        Assert.DoesNotContain("private-detail", scenario.Fault!.Summary);
        Assert.All(lifecycle.GetSetups().Where(setup => setup.State == DataChannelSetupState.Disabled),
            setup => Assert.DoesNotContain("private-detail", setup.Error!));
        var owned = Assert.Single(lifecycle.GetSetups(), setup => setup.ImplementationType == typeof(FirstSetup).FullName);
        await Assert.ThrowsAsync<RoleUnavailableException>(() => lifecycle.StartSetupAsync(owned.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Quarantine_RefusesNewDeliveryAndWaitsForAdmittedDeliveryBeforeEndpointDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = new Scenario();
        await using var host = await new Factory(scenario).CreateAsync(cancellationToken: token);
        var manager = host.Services.GetRequiredService<IDataChannelManager>();
        var channel = Assert.IsType<global::Monica.DataChannel.DataChannel>(manager.Fetch("first"));
        var delivery = channel.SendDataFromOuterAsync("held", token);
        await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        var coordinator = host.Services.GetRequiredService<IRoleFaultCoordinator>();
        var drain = coordinator.ReportRuntimeResourceFaultAsync("selected", true,
            RoleFaultRecord.ForResource(RoleFaultStage.RuntimeResource, typeof(FirstSetup)), token);
        try
        {
            await Assert.ThrowsAsync<RoleUnavailableException>(() => channel.SendDataFromOuterAsync("late", token));
            Assert.False(drain.IsCompleted);
            Assert.Equal(0, scenario.Disposals);
            await manager.Fetch("healthy")!.SendDataFromOuterAsync("ok", token);
        }
        finally { scenario.Release.TrySetResult(); }
        await delivery;
        await drain.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(1, scenario.Disposals);
        Assert.Null(manager.Fetch("first"));
    }

    [Fact]
    public async Task DeliveryReportingOwnFault_CanSettleWhileHealthySetupRestartsAndHostShutdownCancelsItsWait()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = new Scenario { ReportOwnFault = true };
        await using var host = await new Factory(scenario).CreateAsync(cancellationToken: token);
        var manager = host.Services.GetRequiredService<IDataChannelManager>();
        var lifecycle = host.Services.GetRequiredService<IDataChannelLifecycle>();
        var channel = manager.Fetch("first")!;
        var delivery = channel.SendDataFromOuterAsync("report", token);
        try
        {
            await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(scenario.ReportedDrain.IsCompleted);
            Assert.False(delivery.IsCompleted);
            var healthy = Assert.Single(lifecycle.GetSetups(), setup => setup.ImplementationType == typeof(HealthySetup).FullName);
            await lifecycle.RestartSetupAsync(healthy.Id, token);
            await manager.Fetch("healthy")!.SendDataFromInnerAsync("next", token);
        }
        finally { host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(5), token));
        await scenario.ReportedDrain.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(1, scenario.Disposals);
    }

    private sealed class Factory(Scenario scenario) : MonicaTestApplicationFactory<DataChannelRoleIsolationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddDataChannel().UseSetup<FirstSetup>().UseSetup<SecondSetup>().UseSetup<HealthySetup>();

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(scenario);
            services.AddSingleton<IRoleHealthStateProvider>(scenario);
            services.AddSingleton<IRoleFaultCoordinator, Coordinator>();
        }
    }

    private sealed class Scenario : IRoleHealthStateProvider
    {
        public bool FailDeclaration { get; init; }
        public bool FailMaterialization { get; init; }
        public bool ReportOwnFault { get; init; }
        public Task ReportedDrain { get; set; } = Task.CompletedTask;
        public int Disposals;
        public volatile RoleFaultRecord? Fault;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRoleFaulted(string roleId) => roleId == "selected" && Fault is not null;
        public string? GetRoleId(Type implementationType, Type? registrationOwner = null) =>
            implementationType == typeof(FirstSetup) || implementationType == typeof(SecondSetup) ? "selected" : null;
        public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false) => throw new NotSupportedException();
    }

    private sealed class Coordinator(IServiceProvider services, Scenario scenario) : IRoleFaultCoordinator
    {
        public bool TryIsolateStartupFault(string roleId, RoleFaultRecord fault)
        {
            ReportRuntimeResourceFaultAsync(roleId, true, fault).GetAwaiter().GetResult();
            return true;
        }

        public void ReportRuntimeResourceFault(string roleId, bool isRoleCriticalResource, RoleFaultRecord fault) =>
            scenario.ReportedDrain = ReportRuntimeResourceFaultAsync(roleId, isRoleCriticalResource, fault);

        public Task ReportRuntimeResourceFaultAsync(string roleId, bool isRoleCriticalResource, RoleFaultRecord fault,
            CancellationToken cancellationToken = default)
        {
            scenario.Fault = fault;
            return Task.WhenAll(services.GetServices<IRoleLifecycleParticipant>()
                .Select(participant => participant.OnRoleQuarantinedAsync(roleId, cancellationToken)));
        }
    }

    private sealed class FirstSetup : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels) =>
            channels.Add("first", pipeline => pipeline.SetInnerEndpoint<CountingEndpoint>().SetOuterEndpoint(new DefaultEndpointOptions()));
    }

    private sealed class SecondSetup(Scenario scenario) : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels)
        {
            if (scenario.FailDeclaration) throw new InvalidOperationException("private-detail");
            if (!scenario.FailMaterialization) return;
            channels.Add("partial", pipeline => pipeline.SetInnerEndpoint<CountingEndpoint>().SetOuterEndpoint(new DefaultEndpointOptions()));
            channels.Add("broken", pipeline => pipeline.SetInnerEndpoint<CountingEndpoint>().SetOuterEndpoint<BrokenEndpoint>());
        }
    }

    private sealed class HealthySetup : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels) => channels.Add("healthy", pipeline => pipeline.SetOuterEndpoint(new DefaultEndpointOptions()));
    }

    private sealed class CountingEndpoint(Scenario scenario, IRoleFaultCoordinator coordinator) : DefaultChannelEndpoint
    {
        public override Task DisposeAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref scenario.Disposals);
            return Task.CompletedTask;
        }

        public override Task ReceiveDataAsync(ChannelDataContext data)
        {
            if (scenario.ReportOwnFault)
                coordinator.ReportRuntimeResourceFault("selected", true, RoleFaultRecord.ForResource(RoleFaultStage.RuntimeResource, GetType()));
            scenario.Entered.TrySetResult();
            return scenario.Release.Task.WaitAsync(data.CancellationToken);
        }
    }

    private sealed class BrokenEndpoint : DefaultChannelEndpoint
    {
        public BrokenEndpoint() => throw new InvalidOperationException("private-detail");
    }
}
