using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.ApplicationRoles;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventReceiveDispatcherTests
{
    [Fact]
    public async Task Discovery_WhenOwnersAreDeclared_ShouldPreferInheritedOverrideAndKeepLocalOwnerEmpty()
    {
        await using var application = await new OwnershipFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var subscriptions = application.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll().ToArray();

        subscriptions.Single(subscription => subscription.HandlerType == typeof(PolicyOwnedHandler))
            .WorkloadOwner.Should().Be("policy-owner");
        subscriptions.Single(subscription => subscription.HandlerType == typeof(ExplicitOwnedHandler))
            .WorkloadOwner.Should().Be("explicit-owner");
        subscriptions.Single(subscription => subscription.HandlerType == typeof(UnownedHandler))
            .WorkloadOwner.Should().BeNull();
        subscriptions.Single(subscription => subscription.HandlerType == typeof(LocalOwnedHandler))
            .WorkloadOwner.Should().BeNull();
    }

    [Fact]
    public async Task DispatchAsync_WhenCombinedHostOwnsSeveralConsumers_ShouldDispatchOnlyToIntendedOwner()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var dispatcher = application.Services.GetRequiredService<IEventReceiveDispatcher>();
        var recorder = application.Services.GetRequiredService<DeliveryRecorder>();
        var message = CreateMessage();

        await dispatcher.DispatchAsync(message, "policy-owner", token);
        recorder.Owners.Should().Equal("policy-owner");
        await dispatcher.DispatchAsync(message, "explicit-owner", token);
        recorder.Owners.Should().Equal("policy-owner", "explicit-owner");
        await dispatcher.DispatchAsync(message, workloadOwner: null, token);
        recorder.Owners.Should().Equal("policy-owner", "explicit-owner", "unowned");
        recorder.Metadata.Should().AllSatisfy(metadata =>
        {
            metadata.MessageId.Should().Be("original-message");
            metadata.Source.Should().Be("original-source");
            metadata.EventName.Should().Be("receiver.owned");
        });
    }

    [Fact]
    public async Task DispatchAsync_WhenSharedAndInstanceReceiversUseTheSameOwner_ShouldSeparateDeliveries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var registry = app.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var local = await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(OwnershipNotice),
            TopicName = "receiver.owned",
            WorkloadOwner = "policy-owner",
            ReceiveOnEachInstance = true,
            Scope = EventSubscriptionScope.Distributed,
            HandlerFactory = new IocEventHandlerFactory(
                app.Services.GetRequiredService<IServiceScopeFactory>(), typeof(ExplicitOwnedHandler))
        }, token);
        var dispatcher = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        var recorder = app.Services.GetRequiredService<DeliveryRecorder>();
        Assert.NotNull(local.ReceiverInstanceId);
        await dispatcher.DispatchAsync(CreateMessage(), "policy-owner", token);
        recorder.Owners.Should().Equal("policy-owner");
        await dispatcher.DispatchAsync(CreateMessage(), "policy-owner", token, local.ReceiverInstanceId);
        recorder.Owners.Should().Equal("policy-owner", "explicit-owner");
    }

    [Fact]
    public async Task LocalPublish_WhenExternalOwnersExist_ShouldRetainLocalDispatch()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        await using var scope = application.CreateScope(token);

        await scope.Resolve<ILocalEventBus>().PublishAsync(new OwnershipNotice("local"), cancellationToken: token);

        application.Services.GetRequiredService<DeliveryRecorder>().Owners.Should().Equal("local");
    }

    [Fact]
    public async Task SubscribeAsync_WhenTypedSubscriptionIsExplicit_ShouldInferOwnerAndPreserveDescriptorOverride()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var registry = application.Services.GetRequiredService<IEventSubscriptionRegistry>();
        await registry.UnsubscribeWhereAsync(subscription => subscription.Scope == EventSubscriptionScope.Distributed, token);
        await using var scope = application.CreateScope(token);
        var bus = scope.Resolve<IDistributedEventBus>();
        var policySubscription = await bus.SubscribeAsync<OwnershipNotice, PolicyOwnedHandler>();
        var annotatedSubscription = await bus.SubscribeAsync<OwnershipNotice, ExplicitOwnedHandler>();
        policySubscription.WorkloadOwner.Should().Be("policy-owner");
        annotatedSubscription.WorkloadOwner.Should().Be("explicit-owner");

        var overriddenSubscription = await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(OwnershipNotice),
            TopicName = "receiver.owned",
            WorkloadOwner = "descriptor-owner",
            Scope = EventSubscriptionScope.Distributed,
            HandlerFactory = new IocEventHandlerFactory(
                application.Services.GetRequiredService<IServiceScopeFactory>(), typeof(PolicyOwnedHandler))
        }, token);
        overriddenSubscription.WorkloadOwner.Should().Be("descriptor-owner");

        await application.Services.GetRequiredService<IEventReceiveDispatcher>()
            .DispatchAsync(CreateMessage(), "explicit-owner", token);
        application.Services.GetRequiredService<DeliveryRecorder>().Owners.Should().Equal("explicit-owner");
        await registry.UnsubscribeWhereAsync(subscription => subscription.Scope == EventSubscriptionScope.Distributed, token);
    }

    [Fact]
    public async Task DispatchAsync_WhenIntendedOwnerWasRemoved_ShouldFailWithoutInvokingAnotherOwner()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var registry = application.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var dispatcher = application.Services.GetRequiredService<IEventReceiveDispatcher>();
        var subscription = registry.GetAll().Single(item => item.WorkloadOwner == "policy-owner");
        await registry.UnsubscribeAsync(subscription.Id, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CreateMessage(), "policy-owner", token));

        application.Services.GetRequiredService<DeliveryRecorder>().Owners.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenNoDistributedReceiversRemain_ShouldFailDelivery()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var registry = application.Services.GetRequiredService<IEventSubscriptionRegistry>();
        await registry.UnsubscribeWhereAsync(subscription => subscription.Scope == EventSubscriptionScope.Distributed, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.Services
            .GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(CreateMessage(), token));
    }

    [Fact]
    public async Task DispatchAsync_WhenCancellationOccursInHandler_ShouldFailAfterHandlerReturns()
    {
        await using var application = await new OwnershipFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        application.Services.GetRequiredService<DeliveryRecorder>().AfterDelivery = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.Services
            .GetRequiredService<IEventReceiveDispatcher>()
            .DispatchAsync(CreateMessage(), "policy-owner", cancellation.Token));
    }

    [Fact]
    public async Task SubscriptionLifecycle_WhenOwnersShareTopic_ShouldRetainWorstHealthUntilFailedOwnerIsRemoved()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new OwnershipFactory().CreateAsync(cancellationToken: token);
        var registry = application.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var statusStore = application.Services.GetRequiredService<ITopicSubscriptionStatusStore>();
        var probe = application.Services.GetRequiredService<OwnerSubscriptionProbe>();
        using var observation = registry.Subscribe(probe);
        foreach (var subscription in registry.GetAll().ToArray())
        {
            probe.OnNext(new EventSubscriptionChange(EventSubscriptionChangeType.Added, subscription, DateTime.UtcNow));
        }

        probe.CreatedOwners.Should().BeEquivalentTo(new string?[] { "policy-owner", "explicit-owner", null });
        probe.ChangeState("policy-owner", TopicSubscriptionRuntimeState.Failed);
        probe.ChangeState("explicit-owner", TopicSubscriptionRuntimeState.Healthy);
        statusStore.Get(null, "receiver.owned")!.State.Should().Be(TopicSubscriptionRuntimeState.Failed);

        var failedSubscription = registry.GetAll().Single(subscription => subscription.WorkloadOwner == "policy-owner");
        await registry.UnsubscribeAsync(failedSubscription.Id, token);
        statusStore.Get(null, "receiver.owned")!.State.Should().Be(TopicSubscriptionRuntimeState.Healthy);
        probe.ChangeState("policy-owner", TopicSubscriptionRuntimeState.Failed);
        statusStore.Get(null, "receiver.owned")!.State.Should().Be(TopicSubscriptionRuntimeState.Healthy);

        await registry.UnsubscribeWhereAsync(subscription => subscription.Scope == EventSubscriptionScope.Distributed, token);
        statusStore.Get(null, "receiver.owned")!.State.Should().Be(TopicSubscriptionRuntimeState.Stopped);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.DeliverAsync(CreateMessage(), "policy-owner", token));
    }

    private static EventMessage CreateMessage()
        => new(new EventDeliveryMetadata("original-message", "original-source", "receiver.owned", "receiver.owned",
                null, "original-parent", null),
            EventSubscriptionScope.Distributed, Encoding.UTF8.GetBytes("{\"value\":\"payload\"}"));

    [Fact]
    public async Task Quarantine_ClosesNewReceivesAndDrainsBeforeRemovingTheExternalSubscription()
    {
        var token = TestContext.Current.CancellationToken;
        var health = new ReceiverHealth();
        await using var app = await new OwnershipFactory(health).CreateAsync(cancellationToken: token);
        var registry = app.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var probe = app.Services.GetRequiredService<OwnerSubscriptionProbe>();
        var recorder = app.Services.GetRequiredService<DeliveryRecorder>();
        var dispatcher = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        using var observation = registry.Subscribe(probe);
        foreach (var subscription in registry.GetAll())
            probe.OnNext(new EventSubscriptionChange(EventSubscriptionChangeType.Added, subscription, DateTime.UtcNow));
        var subscriptionId = registry.GetAll().Single(s => s.HandlerType == typeof(PolicyOwnedHandler)).Id;
        recorder.ReleasePolicyHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = dispatcher.DispatchAsync(CreateMessage(), "policy-owner", token);
        await recorder.PolicyHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        try
        {
            health.Faulted = true;
            var drain = ((IRoleLifecycleParticipant)registry).OnRoleQuarantinedAsync("receiver.integration", token);
            Assert.False(drain.IsCompleted);
            Assert.Empty(probe.RemovedOwners);
            await Assert.ThrowsAsync<RoleUnavailableException>(() => dispatcher.DispatchAsync(CreateMessage(), "policy-owner", token));
            await Assert.ThrowsAsync<RoleUnavailableException>(() => dispatcher.DispatchAsync(CreateMessage(), token));
            Assert.Equal(["policy-owner"], recorder.Owners);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ActivateAsync(subscriptionId, token));
            await dispatcher.DispatchAsync(CreateMessage(), "explicit-owner", token);
            recorder.ReleasePolicyHandler.TrySetResult();
            await admitted.WaitAsync(TimeSpan.FromSeconds(5), token);
            await drain.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(["policy-owner"], probe.RemovedOwners);
            Assert.Equal(EventSubscriptionState.Disabled, registry.GetById(subscriptionId)!.State);
        }
        finally { recorder.ReleasePolicyHandler.TrySetResult(); }
    }

    private sealed class ReceiverHealth : IRoleHealthStateProvider
    {
        internal bool Faulted { get; set; }
        public bool IsRoleFaulted(string roleId) => Faulted && roleId == "receiver.integration";
        public string? GetRoleId(Type implementationType, Type? registrationOwner = null)
            => implementationType == typeof(PolicyOwnedHandler) ? "receiver.integration" : null;
        public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false) => throw new NotSupportedException();
    }

    private sealed class OwnershipFactory(ReceiverHealth? health = null) : MonicaTestApplicationFactory<OwnershipNotice>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddEventBus().UseNoOpDistributedEventBus();
            builder.AddHostedService();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            var productionGateway = services.Single(descriptor => descriptor.ServiceType == typeof(IDistributedEventBus));
            base.ConfigureServices(services);
            services.RemoveAll<IDistributedEventBus>();
            services.Add(productionGateway);
            services.AddSingleton<ITypeActivationPolicy, OwnershipPolicy>();
            if (health is not null) services.AddSingleton<IRoleHealthStateProvider>(health);
            services.AddSingleton<DeliveryRecorder>();
            services.AddSingleton<OwnerSubscriptionProbe>();
            services.AddScoped<PolicyOwnedHandler>();
            services.AddScoped<ExplicitOwnedHandler>();
            services.AddScoped<UnownedHandler>();
            services.AddScoped<LocalOwnedHandler>();
        }
    }

    private sealed class OwnershipPolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) => ActivationMode.Automatic;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null)
            => implementationType == typeof(UnownedHandler) ? null : "policy-owner";
    }

    public sealed class DeliveryRecorder
    {
        public List<string> Owners { get; } = [];
        public List<EventDeliveryMetadata> Metadata { get; } = [];
        public Action? AfterDelivery { get; set; }
        public TaskCompletionSource? ReleasePolicyHandler { get; set; }
        public TaskCompletionSource PolicyHandlerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(string owner, IEventDeliveryContext delivery)
        {
            Owners.Add(owner);
            if (delivery.Current is { } metadata) Metadata.Add(metadata);
            AfterDelivery?.Invoke();
        }
    }

    [EventName("receiver.owned")]
    public sealed record OwnershipNotice(string Value);

    public sealed class PolicyOwnedHandler(DeliveryRecorder recorder, IEventDeliveryContext delivery)
        : IDistributedEventHandler<OwnershipNotice>
    {
        public Task HandleEventAsync(OwnershipNotice eventData, CancellationToken cancellationToken)
        {
            recorder.Record("policy-owner", delivery);
            recorder.PolicyHandlerStarted.TrySetResult();
            return recorder.ReleasePolicyHandler?.Task ?? Task.CompletedTask;
        }
    }

    [EventSubscriptionOwner("explicit-owner")]
    public abstract class ExplicitOwnerBase : IDistributedEventHandler<OwnershipNotice>
    {
        public abstract Task HandleEventAsync(OwnershipNotice eventData, CancellationToken cancellationToken);
    }

    public sealed class ExplicitOwnedHandler(DeliveryRecorder recorder, IEventDeliveryContext delivery) : ExplicitOwnerBase
    {
        public override Task HandleEventAsync(OwnershipNotice eventData, CancellationToken cancellationToken)
        {
            recorder.Record("explicit-owner", delivery);
            return Task.CompletedTask;
        }
    }

    public sealed class UnownedHandler(DeliveryRecorder recorder, IEventDeliveryContext delivery)
        : IDistributedEventHandler<OwnershipNotice>
    {
        public Task HandleEventAsync(OwnershipNotice eventData, CancellationToken cancellationToken)
        {
            recorder.Record("unowned", delivery);
            return Task.CompletedTask;
        }
    }

    public sealed class LocalOwnedHandler(DeliveryRecorder recorder, IEventDeliveryContext delivery)
        : ILocalEventHandler<OwnershipNotice>
    {
        public Task HandleEventAsync(OwnershipNotice eventData, CancellationToken cancellationToken)
        {
            recorder.Record("local", delivery);
            return Task.CompletedTask;
        }
    }

    private sealed class OwnerSubscriptionProbe(
        IEventSubscriptionRegistry registry,
        IEventReceiveDispatcher dispatcher,
        ITopicSubscriptionStatusStore statusStore,
        IObservableInstanceRegistry observableRegistry,
        IOptions<ModuleHostedServiceOption> hostedOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<OwnerSubscriptionProbe> logger)
        : EventBusSubscriptionHostedServiceBase(registry, dispatcher, statusStore,
            observableRegistry, hostedOptions, scopeFactory, logger, serviceKey: null)
    {
        public List<string?> CreatedOwners { get; } = [];
        public List<string?> RemovedOwners { get; } = [];
        protected override bool GroupSubscriptionsByWorkloadOwner => true;

        public void ChangeState(string? owner, TopicSubscriptionRuntimeState state)
            => ReportSubscriptionState(new SubscriptionGroup("receiver.owned", owner), state, "Owner probe");

        public Task DeliverAsync(EventMessage message, string? owner, CancellationToken token)
            => HandleExternalMessageAsync(message, token, owner);

        protected override Task CreateExternalSubscriptionForTopicAsync(SubscriptionGroup group, Type eventType, CancellationToken cancellationToken)
        {
            CreatedOwners.Add(group.WorkloadOwner);
            ReportSubscriptionState(group, TopicSubscriptionRuntimeState.Healthy, "Probe subscribed");
            return Task.CompletedTask;
        }

        protected override Task RemoveExternalSubscriptionForTopicAsync(SubscriptionGroup group, CancellationToken cancellationToken)
        {
            RemovedOwners.Add(group.WorkloadOwner);
            return Task.CompletedTask;
        }
    }
}
