using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Models;
using Monica.DataChannel.Providers.Default;
using Monica.DataChannel.Providers.TCP;
using Monica.DataChannel.Providers.TCP.Utils;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.DataChannel.Modules;

public sealed class DataChannelLifecycleTests
{
    [Fact]
    public async Task StartSetup_WhenManual_ShouldOnlyConstructSelectedSetupAndKeepOtherWorkHeld()
    {
        var probe = new Probe();
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var manager = app.Services.GetRequiredService<IDataChannelManager>();
        Assert.Empty(manager.FetchAll());
        Assert.Empty(probe.Constructed);
        Assert.All(lifecycle.GetSetups(), setup => Assert.Equal(DataChannelSetupState.Held, setup.State));
        var first = Setup<FirstSetup>(lifecycle);

        await lifecycle.StartSetupAsync(first.Id, TestContext.Current.CancellationToken);

        Assert.Equal([nameof(FirstSetup)], probe.Constructed);
        Assert.Equal(1, probe.SetupCalls);
        Assert.Equal(DataChannelSetupState.Running, Setup<FirstSetup>(lifecycle).State);
        Assert.Equal(DataChannelSetupState.Held, Setup<SecondSetup>(lifecycle).State);
        Assert.Equal(ActivationMode.Manual, Setup<FirstSetup>(lifecycle).Mode);
        Assert.Single(manager.FetchAll());
        Assert.True(probe.Open.ContainsKey("first"));
        Assert.Contains(app.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.ImplementationType == typeof(ForbiddenWorker) && resource.Mode == ActivationMode.Manual);

        await lifecycle.StopSetupAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Empty(probe.Open);
        Assert.Equal(DataChannelSetupState.Stopped, Setup<FirstSetup>(lifecycle).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Fetch("first")!.SendDataFromInnerAsync("held", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Fetch("first")!.ReInitialize(TestContext.Current.CancellationToken));
        await lifecycle.RestartSetupAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.SetupCalls);
        Assert.True(manager.Fetch("first")!.Pipe.IsInitialized);
        Assert.Equal(DataChannelSetupState.Held, Setup<SecondSetup>(lifecycle).State);
    }

    [Fact]
    public async Task StartSetup_WhenOneChannelFails_ShouldRollBackTheWholeSetupAndAllowRetry()
    {
        var probe = new Probe { AddFailingChannel = true, Fail = true };
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var setup = Setup<FirstSetup>(lifecycle);
        await Assert.ThrowsAsync<IOException>(() => lifecycle.StartSetupAsync(setup.Id, TestContext.Current.CancellationToken));
        Assert.Empty(probe.Open);
        Assert.Equal(DataChannelSetupState.Failed, Setup<FirstSetup>(lifecycle).State);
        Assert.Contains("test initialization failed", Setup<FirstSetup>(lifecycle).Error);
        probe.Fail = false;
        await lifecycle.StartSetupAsync(setup.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, probe.Open.Count);
        Assert.Equal(1, probe.SetupCalls);
        Assert.Null(Setup<FirstSetup>(lifecycle).Error);
    }

    [Fact]
    public async Task StartSetup_WhenCancelled_ShouldDrainCleanupAndRejectConcurrentCommands()
    {
        var probe = new Probe { BlockInitialization = true };
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var setup = Setup<FirstSetup>(lifecycle);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = lifecycle.StartSetupAsync(setup.Id, cancellation.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DataChannelSetupState.Starting, Setup<FirstSetup>(lifecycle).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartSetupAsync(setup.Id, TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Empty(probe.Open);
            Assert.All(probe.CleanupTokens, token => Assert.False(token.CanBeCanceled));
            probe.BlockInitialization = false;
            await lifecycle.StartSetupAsync(setup.Id, TestContext.Current.CancellationToken);
            Assert.Equal(DataChannelSetupState.Running, Setup<FirstSetup>(lifecycle).State);
        }
        finally { cancellation.Cancel(); try { await start; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task StartSetup_WhenOwnerDisabled_ShouldRejectBeforeConstruction()
    {
        var probe = new Probe();
        await using var app = await new Factory(probe, disabled: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var setup = Setup<FirstSetup>(lifecycle);
        Assert.Equal(DataChannelSetupState.Disabled, setup.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartSetupAsync(setup.Id, TestContext.Current.CancellationToken));
        Assert.Empty(probe.Constructed);
    }

    [Fact]
    public async Task StartSetup_WhenChannelIdConflicts_ShouldLeaveRunningSetupIntact()
    {
        var probe = new Probe { DuplicateSecond = true };
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        await lifecycle.StartSetupAsync(Setup<FirstSetup>(lifecycle).Id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartSetupAsync(Setup<SecondSetup>(lifecycle).Id, TestContext.Current.CancellationToken));
        Assert.Equal(["first"], probe.Open.Keys);
        Assert.Equal(DataChannelSetupState.Running, Setup<FirstSetup>(lifecycle).State);
        Assert.Equal(DataChannelSetupState.Failed, Setup<SecondSetup>(lifecycle).State);
    }

    [Fact]
    public async Task StartSetup_WhenHttpMappingIsRequired_ShouldRejectBeforeInitializingEndpoints()
    {
        var probe = new Probe { HttpEndpoint = true };
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartSetupAsync(Setup<FirstSetup>(lifecycle).Id, TestContext.Current.CancellationToken));
        Assert.Contains("startup-time HTTP", error.Message);
        Assert.Empty(probe.Open);
        Assert.Empty(app.Services.GetRequiredService<IDataChannelManager>().FetchAll());
    }

    [Fact]
    public async Task SetupEndpoints_WhenManual_ShouldExposeHeldEntriesAndExecuteRealCommands()
    {
        await using var app = await new Factory(new Probe()).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        using var client = ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();
        var id = Uri.EscapeDataString(Setup<FirstSetup>(lifecycle).Id);
        using var listed = await client.GetAsync("/channel-setups", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains(nameof(FirstSetup), await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var started = await client.PostAsync($"/channel-setups/{id}/start", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(DataChannelSetupState.Running, Setup<FirstSetup>(lifecycle).State);
        using var stopped = await client.PostAsync($"/channel-setups/{id}/stop", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal(DataChannelSetupState.Stopped, Setup<FirstSetup>(lifecycle).State);
    }

    [Fact]
    public async Task TcpSetup_WhenExplicitlyStoppedAndRestarted_ShouldReleaseAndReopenListener()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var probe = new Probe { TcpEndpoint = true };
        await using var app = await new Factory(probe).CreateAsync(cancellationToken: deadline.Token);
        var lifecycle = app.Services.GetRequiredService<IDataChannelLifecycle>();
        var manager = app.Services.GetRequiredService<IDataChannelManager>();
        var id = Setup<FirstSetup>(lifecycle).Id;
        await lifecycle.StartSetupAsync(id, deadline.Token);
        var endpoint = Assert.IsType<TcpServerEndpoint>(manager.Fetch("first")!.Pipe.OuterEndpoint);
        var address = Assert.IsType<IPEndPoint>(endpoint.LocalEndpoint);
        using (var peer = new TcpClient()) await peer.ConnectAsync(address.Address, address.Port, deadline.Token);
        await lifecycle.StopSetupAsync(id, deadline.Token);
        Assert.Null(endpoint.LocalEndpoint);
        using (var rebound = new TcpListener(address)) { rebound.Start(); }
        await lifecycle.RestartSetupAsync(id, deadline.Token);
        var reopened = Assert.IsType<IPEndPoint>(endpoint.LocalEndpoint);
        using (var peer = new TcpClient()) await peer.ConnectAsync(reopened.Address, reopened.Port, deadline.Token);
        Assert.Equal(1, probe.SetupCalls);
    }

    private static DataChannelSetupSnapshot Setup<T>(IDataChannelLifecycle lifecycle) =>
        lifecycle.GetSetups().Single(setup => setup.ImplementationType == typeof(T).FullName);

    public sealed class Probe
    {
        public ConcurrentQueue<string> Constructed { get; } = new();
        public ConcurrentDictionary<string, bool> Open { get; } = new();
        public ConcurrentQueue<CancellationToken> CleanupTokens { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SetupCalls;
        public bool AddFailingChannel;
        public bool Fail;
        public bool BlockInitialization;
        public bool DuplicateSecond;
        public bool HttpEndpoint;
        public bool TcpEndpoint;
    }

    public sealed class FirstSetup : IDataChannelSetup
    {
        private readonly Probe _probe;
        public FirstSetup(Probe probe) { _probe = probe; probe.Constructed.Enqueue(nameof(FirstSetup)); }
        public void Setup(IDataChannelRegistrar channels)
        {
            _probe.SetupCalls++;
            channels.Add("first", pipeline =>
            {
                if (_probe.TcpEndpoint) pipeline.SetOuterEndpoint(new TcpServerOptions
                {
                    IsServer = true,
                    ServerAddress = new("first", new ConnectedExtend { Address = Tuple.Create("127.0.0.1", 0) })
                });
                else if (_probe.HttpEndpoint) pipeline.SetOuterEndpoint<HttpEndpoint>();
                else pipeline.SetOuterEndpoint<ProbeEndpoint>();
            });
            if (_probe.AddFailingChannel) channels.Add("failure", pipeline => pipeline.SetOuterEndpoint<ProbeEndpoint>());
        }
    }

    public sealed class SecondSetup : IDataChannelSetup
    {
        private readonly Probe _probe;
        public SecondSetup(Probe probe) { _probe = probe; probe.Constructed.Enqueue(nameof(SecondSetup)); }
        public void Setup(IDataChannelRegistrar channels) =>
            channels.Add(_probe.DuplicateSecond ? "first" : "second", pipeline => pipeline.SetOuterEndpoint<ProbeEndpoint>());
    }

    public class ProbeEndpoint(Probe probe) : DefaultChannelEndpoint
    {
        public override async Task InitAsync(CancellationToken cancellationToken = default)
        {
            probe.Open[Pipe.Id] = true;
            probe.Entered.TrySetResult();
            if (probe.BlockInitialization) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Pipe.Id == "failure" && probe.Fail) throw new IOException("test initialization failed");
        }
        public override Task DisposeAsync(CancellationToken cancellationToken = default)
        {
            probe.CleanupTokens.Enqueue(cancellationToken);
            if (Pipe is not null) probe.Open.TryRemove(Pipe.Id, out _);
            return Task.CompletedTask;
        }
    }

    public sealed class HttpEndpoint(Probe probe) : ProbeEndpoint(probe), IApplicationBuilderConfigurable
    {
        public void ConfigApplicationBuilder(IApplicationBuilder app) => throw new InvalidOperationException("HTTP mapping must not execute.");
        public void ConfigEndpoints(IApplicationBuilder app) => throw new InvalidOperationException("HTTP mapping must not execute.");
    }

    public sealed class ForbiddenWorker : IHostedService
    {
        public ForbiddenWorker() => throw new InvalidOperationException("Unrelated worker must remain held.");
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Factory(Probe probe, bool disabled = false) : MonicaTestApplicationFactory<DataChannelLifecycleTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            base.ConfigureHost(builder);
            builder.Configuration["Application:AutonomousWork"] = "false";
            builder.Services.AddHostedService<ForbiddenWorker>();
        }
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => { options.AutoAddMonicaHttpListener = false; options.EnableMinimalApiByDefault = true; });
            if (disabled) builder.AddModule<DisabledAuthority, DisabledOptions>();
            builder.AddDataChannel(options => options.InitThreadCount = 1).UseSetup<FirstSetup>().UseSetup<SecondSetup>();
        }
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(probe);
        }
    }

    public sealed class DisabledAuthority : MonicaModule<DisabledOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) => Application.SetTypeActivationPolicy(new DisabledPolicy());
        public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) { }
    }
    public sealed class DisabledOptions : ModuleOptions<DisabledAuthority>;
    private sealed class DisabledPolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            kind == TypeActivationKind.DataChannel ? ActivationMode.Disabled : kind == TypeActivationKind.HostedService ? ActivationMode.Manual : ActivationMode.Automatic;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => "remote-owner";
    }
}
