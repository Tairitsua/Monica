using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Dapr;
using Dapr.Messaging.PublishSubscribe;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.Core.Execution;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Modularity.Extensions;
using Monica.Core.ObservableInstance.Services;
using Monica.Dapr.Abstractions;
using Monica.Dapr.Services;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;
using P = Dapr.Client.Autogen.Grpc.v1;

namespace Test.Monica.Dapr.Services;

public sealed class DaprEventBusSubscriptionHostedServiceTests
{
    [Fact]
    public void CreateMetadata_PreservesNativeIdentitySourceAndTrace()
    {
        var message = new TopicMessage(
            "native-id", "urn:monica:producer", "test.event", "1.0", "application/json", "orders", "pubsub")
        {
            Extensions = new Dictionary<string, ProtobufValue>
            {
                ["traceparent"] = new() { StringValue = "00-1234-5678-01" },
                ["tracestate"] = new() { StringValue = "vendor=state" }
            }
        };

        var metadata = DaprEventBusSubscriptionHostedService.CreateMetadata(message, typeof(TestEvent), "key");

        Assert.Equal("native-id", metadata.MessageId);
        Assert.Equal("urn:monica:producer", metadata.Source);
        Assert.Equal("orders", metadata.TopicName);
        Assert.Equal("key", metadata.ServiceKey);
        Assert.Equal("00-1234-5678-01", metadata.TraceParent);
        Assert.Equal("vendor=state", metadata.TraceState);
    }

    [Fact]
    public void CreateMetadata_LeavesMissingNativeIdentityAbsent()
    {
        var message = new TopicMessage("", "", "", "1.0", "application/json", "orders", "pubsub");

        var metadata = DaprEventBusSubscriptionHostedService.CreateMetadata(message, typeof(TestEvent), null);

        Assert.Null(metadata.MessageId);
        Assert.Null(metadata.Source);
    }

    [Fact]
    [RequiresPreviewFeatures]
    public async Task AddKeyedDaprEventBus_WithTwoKeys_ShouldPublishBothRegisteredInstances()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            var eventBus = monica.AddEventBus()
                .AddKeyedEventBus("orders", useDistributed: true)
                .AddKeyedEventBus("payments", useDistributed: true);
            var dapr = eventBus.UseDaprProvider();
            dapr.AddKeyedDaprEventBus("orders", static _ => { });
            dapr.AddKeyedDaprEventBus("payments", static _ => { });
        });
        builder.Services.AddSingleton(Substitute.For<IDaprSidecarHealthCoordinator>());
        builder.Services.AddSingleton(Substitute.For<global::Dapr.Client.DaprClient>());
        builder.Services.AddSingleton(Substitute.For<IJsonSerializerOptionsProvider>());

        using var host = builder.Build();
        using (var firstScope = host.Services.CreateScope())
        using (var secondScope = host.Services.CreateScope())
        {
            var firstGateway = firstScope.ServiceProvider.GetRequiredKeyedService<IDistributedEventBus>("orders");
            var secondGateway = secondScope.ServiceProvider.GetRequiredKeyedService<IDistributedEventBus>("orders");
            Assert.NotSame(firstGateway, secondGateway);
            Assert.Same(
                firstScope.ServiceProvider.GetRequiredKeyedService<IEventTransport>("orders"),
                secondScope.ServiceProvider.GetRequiredKeyedService<IEventTransport>("orders"));
        }
        var registryLifecycle = host.Services.GetServices<IHostedService>()
            .OfType<IHostedLifecycleService>()
            .Single(service => service.GetType().Name == "HostedServiceRegistryLifecycle");

        await registryLifecycle.StartingAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IMoHostedServiceRegistry>();
        var keyedServices = registry.GetServices<DaprEventBusSubscriptionHostedService>()
            .Where(static service => service.ServiceKey is not null)
            .ToArray();
        Assert.Equal(2, keyedServices.Length);
        Assert.Equal(2, keyedServices.Select(static service => service.InstanceId).Distinct().Count());
        Assert.Equal(["orders", "payments"], keyedServices.Select(static service => service.ServiceKey).Order());
        Assert.All(keyedServices, static service => Assert.Equal(HostedServiceState.NotStarted, service.CurrentState));
    }

    [Fact]
    public async Task RegistryLifecycle_WithTwoKeyedServices_PublishesDistinctInstancesBeforeStartAsync()
    {
        using var dependencyProvider = new ServiceCollection()
            .AddScoped<IExecutionPipeline, PassThroughExecutionPipeline>()
            .BuildServiceProvider();
        var subscriptionRegistry = new EventSubscriptionRegistry(
            NullLogger<EventSubscriptionRegistry>.Instance);
        using var firstClient = new CapturingDaprPublishSubscribeClient(0);
        using var secondClient = new CapturingDaprPublishSubscribeClient(0);
        using var first = CreateUnstartedService(
            firstClient,
            subscriptionRegistry,
            dependencyProvider,
            "orders");
        using var second = CreateUnstartedService(
            secondClient,
            subscriptionRegistry,
            dependencyProvider,
            "payments");
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddHostedService();
        });
        builder.Services.AddSingleton<IHostedService>(first);
        builder.Services.AddSingleton<IHostedService>(second);

        using var host = builder.Build();
        var registryLifecycle = host.Services.GetServices<IHostedService>()
            .OfType<IHostedLifecycleService>()
            .Single(service => service.GetType().Name == "HostedServiceRegistryLifecycle");

        await registryLifecycle.StartingAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IMoHostedServiceRegistry>();
        var services = registry.GetServices<DaprEventBusSubscriptionHostedService>();
        Assert.Equal(2, services.Count);
        Assert.Equal(2, services.Select(service => service.InstanceId).Distinct().Count());
        Assert.All(services, service => Assert.Equal(HostedServiceState.NotStarted, service.CurrentState));
        Assert.Same(
            services.Single(service => service.ServiceKey == "orders"),
            registry.GetServicesByKey("orders").Single());
        Assert.Same(
            services.Single(service => service.ServiceKey == "payments"),
            registry.GetServicesByKey("payments").Single());
        Assert.All(services, service => Assert.Same(service, registry.GetServiceByInstanceId(service.InstanceId)));
    }

    [Fact]
    public async Task Subscription_UsesRetryAsTheDefaultTimeoutResponse()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        Assert.Equal(
            TopicResponseAction.Retry,
            fixture.Client.Options.MessageHandlingPolicy.DefaultResponseAction);
    }

    [Fact]
    public async Task Subscription_ConfiguresBackgroundErrorHandler()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        Assert.NotNull(fixture.Client.Options.ErrorHandler);
    }

    [Fact]
    public async Task Subscription_WhenBackgroundStreamFails_ReconnectsAndDisposesFailedGeneration()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.CompletedTask,
            configureOptions: options => options.DeadLetterTopicSuffix = ".dead-letter");
        var failedOptions = fixture.Client.Options;

        await failedOptions.ErrorHandler!(new DaprException("stream closed"));
        await WaitUntilAsync(
            () => fixture.Client.SubscriptionCount >= 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Client.DisposedSubscriptionCount);
        Assert.NotSame(failedOptions, fixture.Client.Options);
        Assert.Equal("test.progress.dead-letter", fixture.Client.Options.DeadLetterTopic);
    }

    [Fact]
    public async Task Subscription_WhenInitialConnectionFails_RetriesUntilConnected()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.CompletedTask,
            initialSubscriptionFailures: 1);

        await WaitUntilAsync(
            () => fixture.Client.SubscriptionCount >= 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Client.SubscriptionCount);
        Assert.Equal(0, fixture.Client.DisposedSubscriptionCount);
    }

    [Fact]
    public async Task Subscription_WhenFailedGenerationReportsAgain_DoesNotCreateDuplicateReplacement()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);
        var failedOptions = fixture.Client.Options;

        await Task.WhenAll(
            failedOptions.ErrorHandler!(new DaprException("stream closed")),
            failedOptions.ErrorHandler!(new DaprException("acknowledgement loop closed")));
        await WaitUntilAsync(
            () => fixture.Client.SubscriptionCount >= 2,
            TestContext.Current.CancellationToken);

        await failedOptions.ErrorHandler!(new DaprException("late failure from stale generation"));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Client.SubscriptionCount);
        Assert.Equal(1, fixture.Client.DisposedSubscriptionCount);
    }

    [Fact]
    public async Task Subscription_WhenRapidBackgroundFailures_IncreasesRecoveryDelay()
    {
        // This scenario drives both failures explicitly; keep the stability reset outside the test's execution window.
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.CompletedTask,
            configureOptions: options => options.SubscriptionRecoveryStabilityPeriod = TimeSpan.FromDays(1));

        await fixture.Client.Options.ErrorHandler!(new DaprException("stream closed"));
        await WaitUntilAsync(
            () => fixture.Client.SubscriptionCount >= 2,
            TestContext.Current.CancellationToken);

        await fixture.Client.Options.ErrorHandler!(new DaprException("replacement stream closed"));
        await WaitUntilAsync(
            () => fixture.Client.SubscriptionCount >= 3 && fixture.Logger.RecoveryDelays.Count >= 2,
            TestContext.Current.CancellationToken);

        var delays = fixture.Logger.RecoveryDelays.ToArray();
        Assert.True(delays[1] > delays[0]);
    }

    [Fact]
    public async Task Subscription_WithDefaultConfiguration_ShouldNotConfigureDeadLetterTopic()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        Assert.Null(fixture.Client.Options.DeadLetterTopic);
    }

    [Fact]
    public async Task Subscription_WithConfiguredSuffix_ShouldCreateDistinctDeadLetterTopics()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.CompletedTask,
            configureOptions: options => options.DeadLetterTopicSuffix = ".dead-letter");

        await fixture.AddSubscriptionAsync("test.audit");
        await WaitUntilAsync(
            () => fixture.Client.HasSubscriptionForTopic("test.audit"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "test.progress.dead-letter",
            fixture.Client.GetOptions("test.progress").DeadLetterTopic);
        Assert.Equal(
            "test.audit.dead-letter",
            fixture.Client.GetOptions("test.audit").DeadLetterTopic);
    }

    [Fact]
    public async Task Subscription_WhenSourceIsDeadLetterTopic_ShouldNotConfigureAnotherDeadLetterTopic()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.CompletedTask,
            configureOptions: options => options.DeadLetterTopicSuffix = ".dead-letter");

        await fixture.AddSubscriptionAsync("test.progress.dead-letter");
        await WaitUntilAsync(
            () => fixture.Client.HasSubscriptionForTopic("test.progress.dead-letter"),
            TestContext.Current.CancellationToken);

        Assert.Null(fixture.Client.GetOptions("test.progress.dead-letter").DeadLetterTopic);
    }

    [Fact]
    public async Task ServiceDispose_DisposesActiveGenerationExactlyOnce()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        fixture.DisposeService();
        fixture.DisposeService();

        Assert.Equal(1, fixture.Client.DisposedSubscriptionCount);
    }

    [Fact]
    public async Task ServiceStop_DisposesActiveGenerationAndRejectsLaterSubscriptions()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        await fixture.StopServiceAsync(TestContext.Current.CancellationToken);
        await fixture.AddSubscriptionAsync("test.after-stop");

        Assert.Equal(1, fixture.Client.SubscriptionCount);
        Assert.Equal(1, fixture.Client.DisposedSubscriptionCount);
    }

    [Fact]
    public void RecoveryPolicy_RejectsInvalidSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DaprSubscriptionRecoveryPolicy.Create(
            new ModuleDaprEventBusOption { SubscriptionRecoveryInitialDelay = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaprSubscriptionRecoveryPolicy.Create(
            new ModuleDaprEventBusOption
            {
                SubscriptionRecoveryInitialDelay = TimeSpan.FromSeconds(2),
                SubscriptionRecoveryMaxDelay = TimeSpan.FromSeconds(1)
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaprSubscriptionRecoveryPolicy.Create(
            new ModuleDaprEventBusOption { SubscriptionRecoveryBackoffMultiplier = double.NaN }));
    }

    [Fact]
    public async Task MessageHandler_WhenDispatchSucceeds_ReturnsSuccess()
    {
        var received = new List<TestEvent>();
        using var fixture = await CreateFixtureAsync((message, _) =>
        {
            received.Add(message);
            return Task.CompletedTask;
        });

        var action = await fixture.HandleAsync(
            "{\"value\":\"ready\"}",
            TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Success, action);
        Assert.Collection(received, message => Assert.Equal("ready", message.Value));
    }

    [Fact]
    public async Task MessageHandler_WhenJsonIsMalformed_ReturnsDrop()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        var action = await fixture.HandleAsync(
            "{not-json",
            TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Drop, action);
    }

    [Fact]
    public async Task MessageHandler_WhenJsonDeserializesToNull_ReturnsDrop()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        var action = await fixture.HandleAsync(
            "null",
            TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Drop, action);
    }

    [Theory]
    [InlineData(null, "test.progress", null)]
    [InlineData(".dead-letter", "test.progress", "test.progress.dead-letter")]
    [InlineData(".dead-letter", "test.progress.dead-letter", null)]
    public async Task MessageHandler_WhenBusinessRejected_ShouldDropAndAllowNextDelivery(
        string? suffix, string topicName, string? expectedDeadLetterTopic)
    {
        var failure = new BusinessException("Required matching time is missing.");
        var handledValues = new List<string>();
        using var fixture = await CreateFixtureAsync(
            (message, _) =>
            {
                handledValues.Add(message.Value);
                return message.Value == "rejected" ? Task.FromException(failure) : Task.CompletedTask;
            },
            configureOptions: options => options.DeadLetterTopicSuffix = suffix,
            topicName: topicName);

        var rejected = await fixture.HandleAsync("{\"value\":\"rejected\"}", TestContext.Current.CancellationToken);
        var accepted = await fixture.HandleAsync("{\"value\":\"accepted\"}", TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Drop, rejected);
        Assert.Equal(TopicResponseAction.Success, accepted);
        Assert.Equal(expectedDeadLetterTopic, fixture.Client.Options.DeadLetterTopic);
        Assert.Equal(["rejected", "accepted"], handledValues);
        var status = fixture.TopicStatusStore.Get(null, topicName)!;
        Assert.Equal(1, status.MessageErrorCount);
        Assert.Equal(1, status.ProcessedMessages);
        Assert.Contains(failure.Message, status.LastErrorException);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error
            && ReferenceEquals(entry.Exception, failure)
            && entry.Message.Contains("returning Drop", StringComparison.Ordinal)
            && entry.Message.Contains("message-1", StringComparison.Ordinal)
            && entry.Message.Contains(topicName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("null")]
    public async Task MessageHandler_WhenMalformedWithDeadLetterTopic_ShouldDropWithoutInvokingHandler(string json)
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => throw new InvalidOperationException("Malformed messages must not invoke a handler."),
            configureOptions: options => options.DeadLetterTopicSuffix = ".dead-letter");

        var action = await fixture.HandleAsync(json, TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Drop, action);
        Assert.Equal("test.progress.dead-letter", fixture.Client.Options.DeadLetterTopic);
    }

    [Fact]
    public async Task MessageHandler_WhenBusinessFailureIsWrappedInUnexpectedFailure_ShouldRetry()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.FromException(
            new InvalidOperationException("Operation failed.", new BusinessException("Rejected."))));

        var action = await fixture.HandleAsync("{\"value\":\"retry\"}", TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Retry, action);
    }

    [Fact]
    public async Task MessageHandler_WhenEventHandlerFails_ReturnsRetry()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.FromException(new InvalidOperationException("SignalR unavailable.")));

        var action = await fixture.HandleAsync(
            "{\"value\":\"retry\"}",
            TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Retry, action);
    }

    [Fact]
    public async Task MessageHandler_WhenEventHandlerIsCancelled_ReturnsRetry()
    {
        using var fixture = await CreateFixtureAsync(
            (_, _) => Task.FromCanceled(new CancellationToken(canceled: true)));

        var action = await fixture.HandleAsync(
            "{\"value\":\"retry\"}",
            TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Retry, action);
    }

    [Fact]
    public async Task MessageHandler_PropagatesDeadlineCancellationToTheEventHandler()
    {
        var receivedToken = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await CreateFixtureAsync((_, cancellationToken) =>
        {
            receivedToken.TrySetResult(cancellationToken);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();

        var handlingTask = fixture.HandleAsync("{\"value\":\"retry\"}", cancellation.Token);
        var handlerToken = await receivedToken.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var action = await handlingTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(cancellation.Token, handlerToken);
        Assert.Equal(TopicResponseAction.Retry, action);
        await fixture.HandlerDisposed.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MessageHandler_WhenNonCooperativeHandlerExceedsDeadline_ReturnsRetryWithoutDisposingItsScope()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await CreateFixtureAsync((_, _) =>
        {
            handlerStarted.TrySetResult();
            return releaseHandler.Task;
        });
        using var cancellation = new CancellationTokenSource();

        var handlingTask = fixture.HandleAsync("{\"value\":\"retry\"}", cancellation.Token);
        await handlerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var action = await handlingTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Retry, action);
        Assert.False(releaseHandler.Task.IsCompleted);
        Assert.False(fixture.HandlerDisposed.IsCompleted);

        releaseHandler.TrySetResult();
        await fixture.HandlerDisposed.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MessageHandler_ObservesFailureFromHandlerThatFaultsAfterDeadline()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await CreateFixtureAsync((_, _) =>
        {
            handlerStarted.TrySetResult();
            return handlerCompletion.Task;
        });
        using var cancellation = new CancellationTokenSource();

        var handlingTask = fixture.HandleAsync("{\"value\":\"retry\"}", cancellation.Token);
        await handlerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var action = await handlingTask.WaitAsync(TestContext.Current.CancellationToken);

        var lateException = new InvalidOperationException("Late SignalR failure.");
        handlerCompletion.SetException(lateException);
        var observedException = await fixture.Logger.LateFailureObserved
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Retry, action);
        Assert.Same(lateException, observedException);
        await fixture.HandlerDisposed.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TopicStatus_WhenStabilityPeriodPasses_ReportsHealthy()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        await WaitUntilAsync(
            () => fixture.TopicStatusStore.Get(null, "test.progress")?.State
                == TopicSubscriptionRuntimeState.Healthy,
            TestContext.Current.CancellationToken);

        var status = fixture.TopicStatusStore.Get(null, "test.progress")!;
        Assert.Equal(TopicSubscriptionRuntimeState.Healthy, status.State);
        Assert.Equal(EventBusProviderKind.Dapr, status.Provider);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task TopicStatus_WhenBackgroundStreamFails_ReportsRecoveringWithError()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);
        // Recovering is transient (it flips back to Subscribing once the backoff elapses),
        // so observe the change stream instead of polling the current state.
        var observed = new ConcurrentQueue<TopicSubscriptionStatus>();
        fixture.TopicStatusStore.StatusChanged += status => observed.Enqueue(status);

        await fixture.Client.Options.ErrorHandler!(new DaprException("stream closed"));
        await WaitUntilAsync(
            () => observed.Any(status => status.State == TopicSubscriptionRuntimeState.Recovering),
            TestContext.Current.CancellationToken);

        var recovering = observed.First(status => status.State == TopicSubscriptionRuntimeState.Recovering);
        Assert.Equal(1, recovering.ConsecutiveFailures);
        Assert.Equal(1, recovering.RecoveryCount);
        Assert.NotNull(recovering.LastErrorMessage);
        Assert.Contains("stream closed", recovering.LastErrorException);
    }

    [Fact]
    public async Task TopicStatus_WhenMessageHandled_RecordsProcessedCount()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        var first = await fixture.HandleAsync(
            "{\"value\":\"one\"}", TestContext.Current.CancellationToken);
        var second = await fixture.HandleAsync(
            "{\"value\":\"two\"}", TestContext.Current.CancellationToken);

        Assert.Equal(TopicResponseAction.Success, first);
        Assert.Equal(TopicResponseAction.Success, second);
        var status = fixture.TopicStatusStore.Get(null, "test.progress")!;
        Assert.Equal(2, status.ProcessedMessages);
        Assert.NotNull(status.LastMessageReceivedAt);
    }

    [Fact]
    public async Task TopicStatus_WhenMessageIsMalformed_ReportsErrorWithoutFailingTopic()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);

        await fixture.HandleAsync("{not-json", TestContext.Current.CancellationToken);

        var status = fixture.TopicStatusStore.Get(null, "test.progress")!;
        Assert.Equal(1, status.MessageErrorCount);
        Assert.NotNull(status.LastErrorMessage);
        Assert.NotEqual(TopicSubscriptionRuntimeState.Failed, status.State);
        Assert.NotEqual(TopicSubscriptionRuntimeState.Stopped, status.State);
    }

    [Fact]
    public async Task TopicStatus_WhenLastSubscriptionForTopicIsRemoved_ReportsStopped()
    {
        using var fixture = await CreateFixtureAsync((_, _) => Task.CompletedTask);
        var extra = await fixture.AddSubscriptionAsync("test.audit");
        await WaitUntilAsync(
            () => fixture.TopicStatusStore.Get(null, "test.audit") is not null,
            TestContext.Current.CancellationToken);

        await fixture.RemoveSubscriptionAsync(extra);
        await WaitUntilAsync(
            () => fixture.TopicStatusStore.Get(null, "test.audit")?.State
                == TopicSubscriptionRuntimeState.Stopped,
            TestContext.Current.CancellationToken);
    }

    private static async Task<TestFixture> CreateFixtureAsync(
        Func<TestEvent, CancellationToken, Task> handler,
        int initialSubscriptionFailures = 0,
        Action<ModuleDaprEventBusOption>? configureOptions = null,
        string topicName = "test.progress")
    {
        var registry = new EventSubscriptionRegistry(NullLogger<EventSubscriptionRegistry>.Instance);
        var serviceProvider = new ServiceCollection()
            .AddScoped<IExecutionPipeline, PassThroughExecutionPipeline>()
            .BuildServiceProvider();
        var jsonOptions = new JsonSerializerOptionsProvider(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), DateTimeWireFormat.Iso8601WallClock);
        var dispatcher = new EventReceiveDispatcher(registry, new EventHandlerInvoker(),
            jsonOptions, NullLogger<EventReceiveDispatcher>.Instance);
        var client = new CapturingDaprPublishSubscribeClient(initialSubscriptionFailures);
        var logger = new TestLogger<DaprEventBusSubscriptionHostedService>();
        var healthCoordinator = Substitute.For<IDaprSidecarHealthCoordinator>();
        healthCoordinator.IsHealthy.Returns(true);
        var handlerDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventBusOptions = new ModuleDaprEventBusOption
        {
            SubscriptionRecoveryInitialDelay = TimeSpan.FromMilliseconds(1),
            SubscriptionRecoveryMaxDelay = TimeSpan.FromMilliseconds(5),
            SubscriptionRecoveryStabilityPeriod = TimeSpan.FromMilliseconds(100)
        };
        configureOptions?.Invoke(eventBusOptions);
        var topicStatusStore = new TopicSubscriptionStatusStore();
        var service = new DaprEventBusSubscriptionHostedService(
            client,
            registry,
            topicStatusStore,
            Substitute.For<IHostApplicationLifetime>(),
            dispatcher,
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            healthCoordinator,
            Options.Create(eventBusOptions),
            Options.Create(new ModuleHostedServiceOption
            {
                DefaultHeartbeatInterval = TimeSpan.Zero
            }),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DaprTopicSubscription>.Instance,
            logger);
        var subscription = await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(TestEvent),
            TopicName = topicName,
            HandlerFactory = new DelegateHandlerFactory(
                handler,
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                () => handlerDisposed.TrySetResult()),
            Scope = EventSubscriptionScope.Distributed
        });

        service.OnNext(new EventSubscriptionChange(
            EventSubscriptionChangeType.Added,
            subscription,
            DateTime.UtcNow));

        await WaitUntilAsync(
            () => client.HasSubscription,
            TestContext.Current.CancellationToken);
        return new TestFixture(serviceProvider, service, registry, client, logger, handlerDisposed.Task, topicStatusStore, topicName);
    }

    private static DaprEventBusSubscriptionHostedService CreateUnstartedService(
        DaprPublishSubscribeClient client,
        EventSubscriptionRegistry subscriptionRegistry,
        IServiceProvider dependencyProvider,
        string serviceKey)
    {
        var jsonOptions = new JsonSerializerOptionsProvider(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), DateTimeWireFormat.Iso8601WallClock);
        var dispatcher = new EventReceiveDispatcher(subscriptionRegistry, new EventHandlerInvoker(),
            jsonOptions, NullLogger<EventReceiveDispatcher>.Instance);
        var healthCoordinator = Substitute.For<IDaprSidecarHealthCoordinator>();
        healthCoordinator.IsHealthy.Returns(true);

        return new DaprEventBusSubscriptionHostedService(
            client,
            subscriptionRegistry,
            new TopicSubscriptionStatusStore(),
            Substitute.For<IHostApplicationLifetime>(),
            dispatcher,
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            healthCoordinator,
            Options.Create(new ModuleDaprEventBusOption()),
            Options.Create(new ModuleHostedServiceOption { DefaultHeartbeatInterval = TimeSpan.Zero }),
            dependencyProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DaprTopicSubscription>.Instance,
            NullLogger<DaprEventBusSubscriptionHostedService>.Instance,
            serviceKey);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private sealed class TestFixture(
        ServiceProvider serviceProvider,
        DaprEventBusSubscriptionHostedService service,
        EventSubscriptionRegistry registry,
        CapturingDaprPublishSubscribeClient client,
        TestLogger<DaprEventBusSubscriptionHostedService> logger,
        Task handlerDisposed,
        TopicSubscriptionStatusStore topicStatusStore,
        string topicName) : IDisposable
    {
        public TopicSubscriptionStatusStore TopicStatusStore { get; } = topicStatusStore;

        public CapturingDaprPublishSubscribeClient Client { get; } = client;

        public TestLogger<DaprEventBusSubscriptionHostedService> Logger { get; } = logger;

        public Task HandlerDisposed { get; } = handlerDisposed;

        public Task<TopicResponseAction> HandleAsync(
            string json,
            CancellationToken cancellationToken = default)
        {
            var message = new TopicMessage(
                "message-1",
                "test",
                nameof(TestEvent),
                "1.0",
                "application/json",
                topicName,
                "pubsub")
            {
                Data = Encoding.UTF8.GetBytes(json)
            };
            return Client.Handler(message, cancellationToken);
        }

        public void DisposeService()
        {
            service.Dispose();
        }

        public Task StopServiceAsync(CancellationToken cancellationToken)
        {
            return service.StopAsync(cancellationToken);
        }

        public async Task<IEventSubscription> AddSubscriptionAsync(string topicName)
        {
            var subscription = await registry.SubscribeAsync(new EventSubscriptionDescriptor
            {
                EventType = typeof(TestEvent),
                TopicName = topicName,
                HandlerFactory = new DelegateHandlerFactory(
                    (_, _) => Task.CompletedTask,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    () => { }),
                Scope = EventSubscriptionScope.Distributed
            });

            service.OnNext(new EventSubscriptionChange(
                EventSubscriptionChangeType.Added,
                subscription,
                DateTime.UtcNow));
            return subscription;
        }

        public Task RemoveSubscriptionAsync(IEventSubscription subscription)
        {
            service.OnNext(new EventSubscriptionChange(
                EventSubscriptionChangeType.Removed,
                subscription,
                DateTime.UtcNow));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            service.Dispose();
            Client.Dispose();
            serviceProvider.Dispose();
        }
    }

    private sealed class CapturingDaprPublishSubscribeClient(int initialSubscriptionFailures)
        : DaprPublishSubscribeClient(Substitute.For<P.Dapr.DaprClient>(), new HttpClient())
    {
        private int _remainingSubscriptionFailures = initialSubscriptionFailures;
        private readonly ConcurrentDictionary<string, DaprSubscriptionOptions> _optionsByTopic =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TopicMessageHandler> _handlersByTopic =
            new(StringComparer.Ordinal);
        private DaprSubscriptionOptions? _options;
        private TopicMessageHandler? _handler;
        private int _subscriptionCount;
        private int _disposedSubscriptionCount;

        public DaprSubscriptionOptions Options =>
            Volatile.Read(ref _options) ?? throw new InvalidOperationException("No subscription has been attempted.");

        public TopicMessageHandler Handler =>
            Volatile.Read(ref _handler) ?? throw new InvalidOperationException("No subscription has been attempted.");

        public bool HasSubscription => Volatile.Read(ref _handler) is not null;

        public bool HasSubscriptionForTopic(string topicName)
        {
            return _handlersByTopic.ContainsKey(topicName);
        }

        public DaprSubscriptionOptions GetOptions(string topicName)
        {
            return _optionsByTopic.TryGetValue(topicName, out var options)
                ? options
                : throw new InvalidOperationException($"No subscription has been attempted for topic '{topicName}'.");
        }

        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public int DisposedSubscriptionCount => Volatile.Read(ref _disposedSubscriptionCount);

        public override Task<IAsyncDisposable> SubscribeAsync(
            string pubSubName,
            string topicName,
            DaprSubscriptionOptions options,
            TopicMessageHandler messageHandler,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _options, options);
            Volatile.Write(ref _handler, messageHandler);
            _optionsByTopic[topicName] = options;
            _handlersByTopic[topicName] = messageHandler;
            Interlocked.Increment(ref _subscriptionCount);

            if (_remainingSubscriptionFailures > 0)
            {
                _remainingSubscriptionFailures--;
                return Task.FromException<IAsyncDisposable>(
                    new DaprException("The Dapr subscription stream is not ready."));
            }

            return Task.FromResult<IAsyncDisposable>(new TrackingAsyncDisposable(
                () => Interlocked.Increment(ref _disposedSubscriptionCount)));
        }
    }

    private sealed class TestDistributedEventBus(
        IServiceScopeFactory serviceScopeFactory,
        IEventHandlerInvoker eventHandlerInvoker,
        IEventSubscriptionRegistry subscriptionManager)
        : DistributedEventBusBase(
            serviceScopeFactory,
            eventHandlerInvoker,
            subscriptionManager,
            NullLoggerFactory.Instance)
    {
        public override Task PublishAsync(
            Type eventType,
            object eventData,
            string? topicName = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public override Task BulkPublishAsync(
            Type eventType,
            IEnumerable<object> eventDataList,
            string? topicName = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateHandlerFactory(
        Func<TestEvent, CancellationToken, Task> handler,
        IServiceScopeFactory serviceScopeFactory,
        Action onDispose) : IEventHandlerFactory
    {
        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync()
        {
            var scope = serviceScopeFactory.CreateAsyncScope();
            return ValueTask.FromResult<IEventHandlerExecutionScope>(
                new EventHandlerExecutionScope(
                    new DelegateHandler(handler),
                    scope.ServiceProvider,
                    async () =>
                    {
                        await scope.DisposeAsync();
                        onDispose();
                    }));
        }

        public Type GetHandlerType()
        {
            return typeof(DelegateHandler);
        }
    }

    private sealed class DelegateHandler(Func<TestEvent, CancellationToken, Task> handler)
        : IDistributedEventHandler<TestEvent>
    {
        public Task HandleEventAsync(TestEvent eventData, CancellationToken cancellationToken)
        {
            return handler(eventData, cancellationToken);
        }
    }

    private sealed record TestEvent(string Value);

    private sealed class PassThroughExecutionPipeline : IExecutionPipeline
    {
        public Task<TResult> ExecuteAsync<TInput, TResult>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            ExecutionDelegate<TResult> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null)
        {
            return terminal();
        }

        public Task ExecuteAsync<TInput>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            Func<Task> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null)
        {
            return terminal();
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource<Exception> _lateFailureObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Exception> LateFailureObserved => _lateFailureObserved.Task;

        public ConcurrentQueue<TimeSpan> RecoveryDelays { get; } = new();

        public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((logLevel, exception, message));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                var delay = properties.FirstOrDefault(property => property.Key == "Delay").Value;
                if (delay is TimeSpan recoveryDelay)
                {
                    RecoveryDelays.Enqueue(recoveryDelay);
                }
            }

            if (exception is not null
                && message.Contains(
                    "faulted after the callback returned Retry",
                    StringComparison.Ordinal))
            {
                _lateFailureObserved.TrySetResult(exception);
            }
        }
    }

    private sealed class TrackingAsyncDisposable(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
