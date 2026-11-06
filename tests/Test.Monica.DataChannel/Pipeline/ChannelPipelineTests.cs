using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.Models;
using Monica.DataChannel.Providers.Default;
using Monica.DataChannel.Services;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.DataChannel.Support;
using Xunit;

namespace Test.Monica.DataChannel.Pipeline;

public sealed class ChannelPipelineTests
{
    [Fact]
    public async Task Reinitialize_WhenAnEndpointFailsThenRecoversAndFailsAgain_ShouldRetireEachActualAttemptAccurately()
    {
        var scenario = new DataChannelScenario();
        scenario.Primary.Outer.Initialization = _ => throw new InvalidOperationException("Initial endpoint failure.");
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var failed = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        failed.IsInitialized.Should().BeFalse();
        failed.IsInitializing.Should().BeFalse();
        failed.IsNotAvailable.Should().BeTrue();
        failed.TotalExceptionCount.Should().Be(1);

        scenario.Primary.Outer.Initialization = _ => Task.CompletedTask;
        var recovered = await facade.ReInitializeChannelAsync(scenario.Primary.Id, scope.CancellationToken);
        recovered.Status.Should().Be(ResultStatus.Ok);
        recovered.Message.Should().Contain("reinitialized successfully");
        var ready = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        ready.IsInitialized.Should().BeTrue();
        ready.IsInitializing.Should().BeFalse();
        ready.IsNotAvailable.Should().BeFalse();
        ready.TotalExceptionCount.Should().Be(1, "recovering does not clear retained or lifetime exceptions");

        scenario.Primary.Outer.Initialization = _ => throw new InvalidOperationException("Later endpoint failure.");
        var failedRetry = await facade.ReInitializeChannelAsync(scenario.Primary.Id, scope.CancellationToken);
        failedRetry.Status.Should().Be(ResultStatus.BadRequest);
        failedRetry.Message.Should().Contain("Later endpoint failure");
        var after = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        after.IsInitialized.Should().BeFalse();
        after.IsInitializing.Should().BeFalse();
        after.IsNotAvailable.Should().BeTrue();
        after.TotalExceptionCount.Should().Be(2);
        scenario.Primary.Outer.InitializationCalls.Should().Be(3);
    }

    [Fact]
    public async Task Reinitialize_WhenAnotherAttemptIsPending_ShouldRejectConcurrentCallAndPropagateAdmittedCancellation()
    {
        var scenario = new DataChannelScenario();
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Primary.Outer.Initialization = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = facade.ReInitializeChannelAsync(scenario.Primary.Id, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var initializing = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
            initializing.IsInitializing.Should().BeTrue();
            initializing.IsInitialized.Should().BeFalse();
            var rejected = await facade.ReInitializeChannelAsync(scenario.Primary.Id, scope.CancellationToken);
            rejected.Status.Should().Be(ResultStatus.BadRequest);
            rejected.Message.Should().Contain("already initializing");
            pending.IsCompleted.Should().BeFalse();
            scenario.Primary.Outer.InitializationCalls.Should().Be(2, "a rejected attempt never calls an endpoint");

            cancellation.Cancel();
            var canceled = () => pending;
            await canceled.Should().ThrowAsync<OperationCanceledException>();
            var retired = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
            retired.IsInitializing.Should().BeFalse();
            retired.IsInitialized.Should().BeFalse();
            retired.IsNotAvailable.Should().BeTrue();
            scenario.Primary.Outer.Initialization = _ => Task.CompletedTask;
            (await facade.ReInitializeChannelAsync(scenario.Primary.Id, scope.CancellationToken)).Status.Should().Be(ResultStatus.Ok);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Reinitialize_WhenATransientEndpointFailsStartup_ShouldRetryItsActualInitializationAndExposeDeclaredIdentity()
    {
        var scenario = new DataChannelScenario();
        scenario.Channels.Clear();
        var channel = new ScenarioChannel { TransientOuter = true, TransientDebugger = true };
        channel.Outer.Initialization = _ => throw new InvalidOperationException("Transient endpoint failed.");
        scenario.Channels.Add(channel);
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var capture = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        capture.OuterEndpoint.IsTransient.Should().BeTrue();
        capture.OuterEndpoint.TypeName.Should().Contain(nameof(TransientScenarioOuterEndpoint));
        capture.OuterEndpoint.AssemblyQualifiedName.Should().Be(typeof(TransientScenarioOuterEndpoint).AssemblyQualifiedName);
        capture.OuterEndpoint.Metadata.CaptureError.Should().BeNull();
        capture.OuterEndpoint.Metadata.Json.Should().Contain(channel.Id);
        capture.OuterEndpoint.Metadata.Json.Should().Contain("Outer");
        var debugger = capture.Middlewares.Single();
        debugger.IsTransient.Should().BeTrue();
        debugger.CanDebugMessages.Should().BeFalse();
        debugger.Type.Should().Be(global::Monica.DataChannel.UIDataChannel.Models.PipelineComponentKind.InfoDisplayMiddleware);
        var transientAction = await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channel.Id, debugger.ComponentKey);
        transientAction.Status.Should().Be(ResultStatus.BadRequest);
        transientAction.Message.Should().Contain("unavailable");
        transientAction.Data.Should().BeNull();

        channel.Outer.Initialization = _ => Task.CompletedTask;
        var retry = await facade.ReInitializeChannelAsync(channel.Id, scope.CancellationToken);
        retry.Status.Should().Be(ResultStatus.Ok);
        retry.Message.Should().Contain("reinitialized successfully");
        channel.Outer.InitializationCalls.Should().Be(2, "a failed transient initialization must not latch success");
        var after = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        after.IsInitialized.Should().BeTrue();
        after.IsNotAvailable.Should().BeFalse();
        after.TotalExceptionCount.Should().Be(1);
    }

    [Fact]
    public async Task Send_WhenTransformAndConfigurationStagesCoexist_ShouldKeepActualTransformOrderInBothDirections()
    {
        var scenario = new DataChannelScenario();
        scenario.Primary.Middlewares.AddRange([new AppendMiddleware("|first"), new ConfigurationMiddleware(), new AppendMiddleware("|second")]);
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var channel = scope.Resolve<IDataChannelManager>().Fetch(scenario.Primary.Id)!;
        await channel.SendDataFromInnerAsync("inner", TestContext.Current.CancellationToken);
        await channel.SendDataFromOuterAsync("outer", TestContext.Current.CancellationToken);
        scenario.Primary.Outer.Receipts.Should().Equal("inner|first|second");
        scenario.Primary.Inner.Receipts.Should().Equal("outer|first|second");

        var components = ResultAssertions.Success(await scope.Resolve<DataChannelFacade>().GetChannelsStatusAsync(scope.CancellationToken)).Single().Middlewares;
        components.Select(component => component.Stage).Should().Equal(PipelineComponentStage.EndpointConfiguration,
            PipelineComponentStage.Transform, PipelineComponentStage.Transform);
        components.Select(component => component.Ordinal).Should().Equal(0, 0, 1);
        components[0].TypeName.Should().Contain(nameof(ConfigurationMiddleware));
    }

    [Fact]
    public async Task Shutdown_WhenInitializationIsAdmitted_ShouldWaitThenRetireThePipelinePermanently()
    {
        var behavior = new LifecycleBehavior();
        await using var application = await new LifecycleApplicationFactory(behavior)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var channel = application.Services.GetRequiredService<IDataChannelManager>().Fetch("lifecycle")!;
        var initializer = application.Services.GetServices<IHostedService>().OfType<DataChannelInitializerService>().Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behavior.InitializeOuter = async token =>
        {
            behavior.Events.Enqueue("initialization-entered");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            behavior.Events.Enqueue("initialization-completed");
        };

        var initialization = channel.ReInitialize(TestContext.Current.CancellationToken);
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            shutdown = initializer.StopAsync(TestContext.Current.CancellationToken);
            shutdown.IsCompleted.Should().BeFalse();
            channel.Pipe.IsInitializing.Should().BeTrue();
            behavior.Events.Should().Equal("initialization-entered");

            release.TrySetResult();
            await initialization.WaitAsync(TestContext.Current.CancellationToken);
            await shutdown.WaitAsync(TestContext.Current.CancellationToken);

            behavior.Events.Should().Equal("initialization-entered", "initialization-completed", "dispose-outer", "dispose-inner");
            channel.Pipe.IsInitializing.Should().BeFalse();
            channel.Pipe.IsInitialized.Should().BeFalse();
            channel.Pipe.IsNotAvailable.Should().BeTrue();
            var rejected = () => channel.ReInitialize(TestContext.Current.CancellationToken);
            await rejected.Should().ThrowAsync<ObjectDisposedException>();
            behavior.Events.Should().HaveCount(4, "terminal host disposal cannot reopen endpoints");
        }
        finally
        {
            release.TrySetResult();
            await initialization;
            if (shutdown is not null) await shutdown;
        }
    }

    [Fact]
    public async Task Reinitialize_WhenCancelled_ShouldReleaseEndpointsWithoutCancelledCleanupAndAllowRetry()
    {
        var behavior = new LifecycleBehavior();
        await using var application = await new LifecycleApplicationFactory(behavior)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var channel = application.Services.GetRequiredService<IDataChannelManager>().Fetch("lifecycle")!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behavior.InitializeOuter = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialization = channel.ReInitialize(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var cancelled = () => initialization;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            behavior.Events.Should().Equal("dispose-outer", "dispose-inner");
            behavior.CleanupTokens.Should().OnlyContain(token => !token.CanBeCanceled);
            channel.Pipe.IsInitializing.Should().BeFalse();
            channel.Pipe.IsInitialized.Should().BeFalse();
            channel.Pipe.IsNotAvailable.Should().BeTrue();

            behavior.InitializeOuter = _ => Task.CompletedTask;
            await channel.ReInitialize(TestContext.Current.CancellationToken);
            channel.Pipe.IsInitialized.Should().BeTrue();
            channel.Pipe.IsNotAvailable.Should().BeFalse();
        }
        finally
        {
            cancellation.Cancel();
            try { await initialization; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Shutdown_WhenAnEndpointFails_ShouldReleaseBothEndpointsAndPreserveFailureOnRepeatedShutdown()
    {
        var behavior = new LifecycleBehavior();
        await using var application = await new LifecycleApplicationFactory(behavior)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var channel = application.Services.GetRequiredService<IDataChannelManager>().Fetch("lifecycle")!;
        var initializer = application.Services.GetServices<IHostedService>().OfType<DataChannelInitializerService>().Single();
        var endpointFailure = new InvalidOperationException("The outer endpoint failed to stop.");
        behavior.DisposeOuter = _ => throw endpointFailure;

        await initializer.StopAsync(TestContext.Current.CancellationToken);
        await initializer.StopAsync(TestContext.Current.CancellationToken);

        behavior.Events.Should().Equal("dispose-outer", "dispose-inner");
        channel.Pipe.IsInitialized.Should().BeFalse();
        channel.Pipe.IsNotAvailable.Should().BeTrue();
        var errors = initializer.RuntimeInfo.StateHistory.Where(entry => entry.Exception is not null).ToArray();
        errors.Should().HaveCount(2, "both host shutdown calls must observe the cleanup failure");
        var firstFailure = errors[0].Exception.Should().BeOfType<AggregateException>().Which;
        firstFailure.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(endpointFailure);
        errors[1].Exception.Should().BeSameAs(firstFailure);
        var rejected = () => channel.ReInitialize(TestContext.Current.CancellationToken);
        await rejected.Should().ThrowAsync<ObjectDisposedException>();
    }

    public sealed class LifecycleBehavior
    {
        public Func<CancellationToken, Task> InitializeOuter { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> DisposeOuter { get; set; } = _ => Task.CompletedTask;
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<CancellationToken> CleanupTokens { get; } = new();

        public Task DisposeAsync(string side, CancellationToken cancellationToken)
        {
            Events.Enqueue($"dispose-{side}");
            CleanupTokens.Enqueue(cancellationToken);
            return side == "outer" ? DisposeOuter(cancellationToken) : Task.CompletedTask;
        }
    }

    public sealed class LifecycleInnerEndpoint(LifecycleBehavior behavior) : DefaultChannelEndpoint
    {
        public override Task DisposeAsync(CancellationToken cancellationToken = default) => behavior.DisposeAsync("inner", cancellationToken);
    }

    public sealed class LifecycleOuterEndpoint(LifecycleBehavior behavior) : DefaultChannelEndpoint
    {
        public override Task InitAsync(CancellationToken cancellationToken = default) => behavior.InitializeOuter(cancellationToken);
        public override Task DisposeAsync(CancellationToken cancellationToken = default) => behavior.DisposeAsync("outer", cancellationToken);
    }

    public sealed class LifecycleSetup : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar registrar) => registrar.Add("lifecycle", pipeline => pipeline
            .SetOuterEndpoint<LifecycleOuterEndpoint>().SetInnerEndpoint<LifecycleInnerEndpoint>());
    }

    private sealed class LifecycleApplicationFactory(LifecycleBehavior behavior)
        : MonicaTestApplicationFactory<LifecycleOuterEndpoint>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) => builder.AddDataChannel().UseSetup<LifecycleSetup>();

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(behavior);
        }
    }
}
