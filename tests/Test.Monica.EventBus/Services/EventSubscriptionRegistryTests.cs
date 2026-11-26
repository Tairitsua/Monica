using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.ApplicationRoles;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventSubscriptionRegistryTests
{
    [Fact]
    public async Task SubscribeBatchAsync_WhenDescriptorEnumerationFails_ShouldRollBackCreatedSubscriptions()
    {
        var registry = CreateRegistry();
        var disposalOrder = new List<string>();
        var manualFactory = new TrackingHandlerFactory();
        var firstFactory = new TrackingHandlerFactory(name: "first", disposalOrder: disposalOrder);
        var secondFactory = new TrackingHandlerFactory(name: "second", disposalOrder: disposalOrder);
        var manual = await registry.SubscribeAsync(
            CreateDescriptor(manualFactory, "manual"),
            TestContext.Current.CancellationToken);

        Func<Task> subscribe = () => registry.SubscribeBatchAsync(
            CreateFailingBatch(firstFactory, secondFactory));

        var assertion = await subscribe.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("descriptor failure");
        registry.GetAll().Should().ContainSingle(subscription => subscription.Id == manual.Id);
        disposalOrder.Should().Equal("second", "first");
        firstFactory.IsDisposed.Should().BeTrue();
        secondFactory.IsDisposed.Should().BeTrue();
        manualFactory.IsDisposed.Should().BeFalse();

        await registry.UnsubscribeAsync(manual.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnsubscribeBatchAsync_WhenDisposalFails_ShouldRetainEverySubscriptionForRetry()
    {
        var registry = CreateRegistry();
        var firstFactory = new TrackingHandlerFactory(disposalFailures: 1);
        var secondFactory = new TrackingHandlerFactory(disposalFailures: 1);
        var first = await registry.SubscribeAsync(
            CreateDescriptor(firstFactory, "first"),
            TestContext.Current.CancellationToken);
        var second = await registry.SubscribeAsync(
            CreateDescriptor(secondFactory, "second"),
            TestContext.Current.CancellationToken);

        Func<Task> unsubscribe = () => registry.UnsubscribeBatchAsync(
            [first.Id, second.Id],
            TestContext.Current.CancellationToken);

        var assertion = await unsubscribe.Should().ThrowAsync<AggregateException>();
        assertion.Which.InnerExceptions.Should().HaveCount(2);
        registry.GetAll().Should().HaveCount(2);
        firstFactory.IsDisposed.Should().BeFalse();
        secondFactory.IsDisposed.Should().BeFalse();

        await registry.UnsubscribeBatchAsync(
            [first.Id, second.Id],
            TestContext.Current.CancellationToken);

        registry.GetAll().Should().BeEmpty();
        firstFactory.IsDisposed.Should().BeTrue();
        secondFactory.IsDisposed.Should().BeTrue();
        firstFactory.DisposeAttempts.Should().Be(2);
        secondFactory.DisposeAttempts.Should().Be(2);
    }

    [Fact]
    public async Task SubscribeAsync_WhenAlreadyCancelled_ShouldNotMutateRegistry()
    {
        var registry = CreateRegistry();
        var factory = new TrackingHandlerFactory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> subscribe = () => registry.SubscribeAsync(
            CreateDescriptor(factory, "cancelled"),
            cancellation.Token);

        await subscribe.Should().ThrowAsync<OperationCanceledException>();
        registry.GetAll().Should().BeEmpty();
        factory.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task UnsubscribeBatchAsync_WhenAlreadyCancelled_ShouldCleanEverySubscriptionBeforeReportingCancellation()
    {
        var registry = CreateRegistry();
        var firstFactory = new TrackingHandlerFactory();
        var secondFactory = new TrackingHandlerFactory();
        var first = await registry.SubscribeAsync(
            CreateDescriptor(firstFactory, "first"),
            TestContext.Current.CancellationToken);
        var second = await registry.SubscribeAsync(
            CreateDescriptor(secondFactory, "second"),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> unsubscribe = () => registry.UnsubscribeBatchAsync(
            [first.Id, second.Id],
            cancellation.Token);

        await unsubscribe.Should().ThrowAsync<OperationCanceledException>();
        registry.GetAll().Should().BeEmpty();
        firstFactory.IsDisposed.Should().BeTrue();
        secondFactory.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task SubscribeAsync_WhenObserverReentersUnsubscribe_ShouldDispatchCommittedChangesWithoutDeadlock()
    {
        var registry = CreateRegistry();
        var changes = new List<EventSubscriptionChangeType>();
        using var observation = registry.Subscribe(new CallbackObserver(change =>
        {
            changes.Add(change.ChangeType);
            if (change.ChangeType == EventSubscriptionChangeType.Activated)
            {
                registry.UnsubscribeAsync(change.Subscription.Id).GetAwaiter().GetResult();
            }
        }));

        var subscribe = registry.SubscribeAsync(
            CreateDescriptor(new TrackingHandlerFactory(), "reentrant"),
            TestContext.Current.CancellationToken);

        await subscribe.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        registry.GetAll().Should().BeEmpty();
        changes.Should().Equal(
            EventSubscriptionChangeType.Added,
            EventSubscriptionChangeType.Activated,
            EventSubscriptionChangeType.Removed);
    }

    [Fact]
    public async Task UnsubscribeAsync_WhenCallerHasSynchronizationContext_ShouldNotifyOutsideIt()
    {
        var registry = CreateRegistry();
        var token = TestContext.Current.CancellationToken;
        var subscription = await registry.SubscribeAsync(
            CreateDescriptor(new TrackingHandlerFactory(), "context-affinity"), token);
        var observed = new List<(EventSubscriptionChangeType Change, SynchronizationContext? Context)>();
        using var observation = registry.Subscribe(new CallbackObserver(change =>
            observed.Add((change.ChangeType, SynchronizationContext.Current))));

        var callerContext = SynchronizationContext.Current;
        Task removal;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            removal = registry.UnsubscribeAsync(subscription.Id, token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
        }

        await removal.WaitAsync(TimeSpan.FromSeconds(5), token);
        observed.Should().ContainSingle()
            .Which.Should().Be((EventSubscriptionChangeType.Removed, null));
        registry.GetById(subscription.Id).Should().BeNull();
    }

    [Fact]
    public async Task ActivateAsync_WhenUnsubscribeOwnsMutation_ShouldNotReactivateDisposedSubscription()
    {
        var registry = CreateRegistry();
        var factory = new BlockingDisposalHandlerFactory();
        var subscription = await registry.SubscribeAsync(
            CreateDescriptor(factory, "concurrent"),
            TestContext.Current.CancellationToken);
        await registry.DeactivateAsync(subscription.Id, TestContext.Current.CancellationToken);

        var unsubscribe = registry.UnsubscribeAsync(
            subscription.Id,
            TestContext.Current.CancellationToken);
        await factory.DisposalStarted.WaitAsync(TestContext.Current.CancellationToken);
        var activate = registry.ActivateAsync(
            subscription.Id,
            TestContext.Current.CancellationToken);

        factory.ReleaseDisposal();
        await unsubscribe;
        var observeActivation = () => activate;
        await observeActivation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cannot activate subscription in state Disposed");

        registry.GetAll().Should().BeEmpty();
        subscription.State.Should().Be(EventSubscriptionState.Disposed);
    }

    [Fact]
    public async Task Quarantine_WaitsForItsDeactivationWhenAnotherThreadOwnsNotificationDispatch()
    {
        var token = TestContext.Current.CancellationToken;
        var health = new SubscriptionRole();
        var registry = new EventSubscriptionRegistry(NullLogger<EventSubscriptionRegistry>.Instance, health: health);
        var owned = await registry.SubscribeAsync(CreateDescriptor(new TrackingHandlerFactory(), "owned") with
        {
            Scope = EventSubscriptionScope.Distributed
        }, token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var removed = false;
        using var observation = registry.Subscribe(new CallbackObserver(change =>
        {
            if (change.Subscription.TopicName == "other" && change.ChangeType == EventSubscriptionChangeType.Added)
            {
                entered.TrySetResult();
                release.Wait(token);
            }
            if (change.Subscription.Id == owned.Id && change.ChangeType == EventSubscriptionChangeType.Deactivated)
                removed = true;
        }));

        var adding = Task.Run(() => registry.SubscribeAsync(CreateDescriptor(new TrackingHandlerFactory(), "other"), token), token);
        await entered.Task.WaitAsync(token);
        health.Faulted = true;
        var quarantine = registry.OnRoleQuarantinedAsync("worker", token);
        try { Assert.False(quarantine.IsCompleted); }
        finally { release.Set(); }
        await quarantine;
        Assert.True(removed);
        await adding;
    }

    private sealed class SubscriptionRole : IRoleHealthStateProvider
    {
        internal bool Faulted;
        public bool IsRoleFaulted(string roleId) => roleId == "worker" && Faulted;
        public string? GetRoleId(Type implementationType, Type? registrationOwner = null) => "worker";
        public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false) => throw new NotSupportedException();
    }

    private static EventSubscriptionRegistry CreateRegistry()
        => new(NullLogger<EventSubscriptionRegistry>.Instance);

    private static IEnumerable<EventSubscriptionDescriptor> CreateFailingBatch(
        TrackingHandlerFactory firstFactory,
        TrackingHandlerFactory secondFactory)
    {
        yield return CreateDescriptor(firstFactory, "first");
        yield return CreateDescriptor(secondFactory, "second");
        throw new InvalidOperationException("descriptor failure");
    }

    private static EventSubscriptionDescriptor CreateDescriptor(
        IEventHandlerFactory factory,
        string topicName)
    {
        return new EventSubscriptionDescriptor
        {
            EventType = typeof(TestEvent),
            TopicName = topicName,
            HandlerFactory = factory,
            Scope = EventSubscriptionScope.Local
        };
    }

    private sealed record TestEvent;

    private sealed class TrackingHandlerFactory(
        int disposalFailures = 0,
        string? name = null,
        ICollection<string>? disposalOrder = null)
        : IEventHandlerFactory, IAsyncDisposable
    {
        private int _remainingDisposalFailures = disposalFailures;

        public int DisposeAttempts { get; private set; }

        public bool IsDisposed { get; private set; }

        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync()
            => throw new NotSupportedException();

        public Type? GetHandlerType() => null;

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            if (_remainingDisposalFailures > 0)
            {
                _remainingDisposalFailures--;
                return ValueTask.FromException(new InvalidOperationException("dispose failure"));
            }

            IsDisposed = true;
            if (name is not null)
            {
                disposalOrder?.Add(name);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDisposalHandlerFactory : IEventHandlerFactory, IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposalStarted => _disposalStarted.Task;

        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync()
            => throw new NotSupportedException();

        public Type? GetHandlerType() => null;

        public void ReleaseDisposal() => _releaseDisposal.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            _disposalStarted.TrySetResult();
            await _releaseDisposal.Task;
        }
    }

    private sealed class CallbackObserver(Action<EventSubscriptionChange> onNext)
        : IObserver<EventSubscriptionChange>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(EventSubscriptionChange value) => onNext(value);
    }
}
