using AwesomeAssertions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Testing.Hosting;
using Test.Monica.EventBus.UIEventBus.State.Support;
using Xunit;

namespace Test.Monica.EventBus.UIEventBus.State;

public sealed class EventBusTestServiceTests
{
    private static readonly EventBusWorkbenchScenarioFactory Factory = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcquireStandaloneListenerLeaseAsync_WhenPriorStopFails_ShouldWaitThenAdoptExactHandleWithoutOldOwnerInterference()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        await using var originalOwner = await service.AcquireStandaloneListenerLeaseAsync(Token);
        var started = await originalOwner.SubscribeAsync("diagnostics.retained");
        started.Status.Should().Be(ResultStatus.Ok);
        started.Message.Should().BeNullOrEmpty();
        started.Data.Should().NotBeNull();
        var original = started.Data!;
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("temporary removal failure");
        var failedStop = await originalOwner.UnsubscribeAsync();
        failedStop.IsFailure().Should().BeTrue();
        failedStop.Message.Should().NotBeNullOrWhiteSpace();
        var acquiring = service.AcquireStandaloneListenerLeaseAsync(Token);
        acquiring.IsCompleted.Should().BeFalse("the next mount cannot adopt a listener while the first still owns control");
        originalOwner.Snapshot!.SubscriptionId.Should().Be(original.Id);

        await originalOwner.DisposeAsync();
        await using var recoveryOwner = await AwaitAsync(acquiring);

        var recovered = recoveryOwner.Snapshot;
        recovered.Should().NotBeNull();
        recovered!.SubscriptionId.Should().Be(original.Id);
        recovered.TopicName.Should().Be("diagnostics.retained");
        var staleStop = await originalOwner.UnsubscribeAsync();
        staleStop.IsFailure().Should().BeTrue();
        staleStop.Message.Should().NotBeNullOrWhiteSpace();
        registry.UnsubscribeAttempts.Should().ContainSingle("retired lease calls cannot touch a later owner's handle")
            .Which.Should().Be(original.Id);
        registry.BeforeUnsubscribe = null;
        var stopped = await recoveryOwner.UnsubscribeAsync();
        stopped.Status.Should().Be(ResultStatus.Ok);
        stopped.Message.Should().BeNullOrEmpty();
        recoveryOwner.Snapshot.Should().BeNull();
        var replacement = await recoveryOwner.SubscribeAsync("diagnostics.replacement");
        replacement.Status.Should().Be(ResultStatus.Ok);
        replacement.Message.Should().BeNullOrEmpty();
        replacement.Data.Should().NotBeNull();
        var replacementId = replacement.Data!.Id;
        replacementId.Should().NotBe(original.Id);

        await originalOwner.DisposeAsync();
        var staleStart = await originalOwner.SubscribeAsync("diagnostics.stale");
        var legacyStop = await service.UnsubscribeFromTestTopicAsync();

        staleStart.IsFailure().Should().BeTrue();
        staleStart.Message.Should().NotBeNullOrWhiteSpace();
        legacyStop.IsFailure().Should().BeTrue();
        legacyStop.Message.Should().NotBeNullOrWhiteSpace();
        registry.GetById(replacementId).Should().NotBeNull();
        registry.UnsubscribeAttempts.Should().Equal(original.Id, original.Id);
        var finalStop = await recoveryOwner.UnsubscribeAsync();
        finalStop.Status.Should().Be(ResultStatus.Ok);
        finalStop.Message.Should().BeNullOrEmpty();
        registry.Inner.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task AcquireStandaloneListenerLeaseAsync_WhenQueuedMountCancels_ShouldLeavePriorOwnershipAndGateIntact()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        await using var originalOwner = await service.AcquireStandaloneListenerLeaseAsync(Token);
        using var waitingLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = service.AcquireStandaloneListenerLeaseAsync(waitingLifetime.Token);
        waiting.IsCompleted.Should().BeFalse();

        waitingLifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(waiting));

        var started = await originalOwner.SubscribeAsync("diagnostics.original");
        started.Status.Should().Be(ResultStatus.Ok);
        started.Message.Should().BeNullOrEmpty();
        started.Data.Should().NotBeNull();
        originalOwner.Snapshot!.SubscriptionId.Should().Be(started.Data!.Id);
        var stopped = await originalOwner.UnsubscribeAsync();
        stopped.Status.Should().Be(ResultStatus.Ok);
        stopped.Message.Should().BeNullOrEmpty();
        await originalOwner.DisposeAsync();
        await using var laterOwner = await service.AcquireStandaloneListenerLeaseAsync(Token);
        laterOwner.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_WhenAnotherMountWaitsForLease_ShouldCancelAcquisitionAndRejectLaterOwners()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        await using var originalOwner = await service.AcquireStandaloneListenerLeaseAsync(Token);
        var waiting = service.AcquireStandaloneListenerLeaseAsync(Token);
        waiting.IsCompleted.Should().BeFalse();

        await service.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(waiting));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AcquireStandaloneListenerLeaseAsync(Token));
        originalOwner.Snapshot.Should().BeNull();
        registry.Inner.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task UnsubscribeFromTestTopicAsync_WhenRemovalFails_ShouldRetainExactHandleAndRetryBeforeReplacingIt()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var started = await service.SubscribeToTestTopicAsync("diagnostics.original");
        started.Status.Should().Be(ResultStatus.Ok);
        started.Message.Should().BeNullOrEmpty();
        started.Data.Should().NotBeNull();
        var subscription = started.Data!;
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("temporary removal failure");

        var failed = await service.UnsubscribeFromTestTopicAsync();
        var blockedReplacement = await service.SubscribeToTestTopicAsync("diagnostics.replacement");

        failed.IsFailure().Should().BeTrue();
        failed.Message.Should().NotBeNullOrWhiteSpace();
        service.IsSubscribed.Should().BeTrue();
        service.CurrentTopicName.Should().Be("diagnostics.original");
        registry.GetById(subscription.Id).Should().BeSameAs(subscription);
        blockedReplacement.IsFailure().Should().BeTrue();
        blockedReplacement.Message.Should().NotBeNullOrWhiteSpace();
        registry.Inner.GetAll().Should().ContainSingle().Which.Id.Should().Be(subscription.Id);
        registry.BeforeUnsubscribe = null;

        var retried = await service.UnsubscribeFromTestTopicAsync();

        retried.Status.Should().Be(ResultStatus.Ok);
        retried.Message.Should().BeNullOrEmpty();
        service.IsSubscribed.Should().BeFalse();
        service.CurrentTopicName.Should().BeNull();
        registry.GetById(subscription.Id).Should().BeNull();
        registry.UnsubscribeAttempts.Should().Equal(subscription.Id, subscription.Id);
    }

    [Fact]
    public async Task SubscribeToTestTopicAsync_WhenStopIsPending_ShouldWaitForExactHandleRemovalBeforeStartingReplacement()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var started = await service.SubscribeToTestTopicAsync("diagnostics.original");
        started.Status.Should().Be(ResultStatus.Ok);
        started.Message.Should().BeNullOrEmpty();
        started.Data.Should().NotBeNull();
        var original = started.Data!;
        var entered = Signal();
        var release = Signal();
        registry.BeforeUnsubscribe = async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Token);
        };
        var stopping = service.UnsubscribeFromTestTopicAsync();
        Task<Result<IEventSubscription>>? replacing = null;
        try
        {
            await AwaitAsync(entered.Task);
            replacing = service.SubscribeToTestTopicAsync("diagnostics.replacement");
            replacing.IsCompleted.Should().BeFalse();
            service.IsSubscribed.Should().BeTrue();
            service.CurrentTopicName.Should().Be("diagnostics.original");
            registry.Inner.GetAll().Should().ContainSingle().Which.Id.Should().Be(original.Id);
            release.TrySetResult(true);

            var stopped = await AwaitAsync(stopping);
            var replacement = await AwaitAsync(replacing);

            stopped.Status.Should().Be(ResultStatus.Ok);
            stopped.Message.Should().BeNullOrEmpty();
            replacement.Status.Should().Be(ResultStatus.Ok);
            replacement.Message.Should().BeNullOrEmpty();
            replacement.Data.Should().NotBeNull();
            replacement.Data!.Id.Should().NotBe(original.Id);
            service.CurrentTopicName.Should().Be("diagnostics.replacement");
            registry.Inner.GetAll().Should().ContainSingle().Which.Id.Should().Be(replacement.Data.Id);
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(stopping);
            if (replacing is not null) await AwaitAsync(replacing);
            registry.BeforeUnsubscribe = null;
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenRemovalFails_ShouldRetireStartsAndRetainHandleForRepeatedCleanup()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var started = await service.SubscribeToTestTopicAsync("diagnostics.original");
        started.Status.Should().Be(ResultStatus.Ok);
        started.Message.Should().BeNullOrEmpty();
        started.Data.Should().NotBeNull();
        var subscription = started.Data!;
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("temporary removal failure");

        await service.DisposeAsync();

        service.IsSubscribed.Should().BeTrue();
        service.CurrentTopicName.Should().Be("diagnostics.original");
        registry.GetById(subscription.Id).Should().NotBeNull();
        registry.BeforeUnsubscribe = null;
        await service.DisposeAsync();
        await service.DisposeAsync();
        service.IsSubscribed.Should().BeFalse();
        service.CurrentTopicName.Should().BeNull();
        registry.Inner.GetAll().Should().BeEmpty();
        registry.UnsubscribeAttempts.Should().Equal(subscription.Id, subscription.Id);

        var lateStart = await service.SubscribeToTestTopicAsync("diagnostics.after-retirement");

        lateStart.IsFailure().Should().BeTrue();
        lateStart.Message.Should().NotBeNullOrWhiteSpace();
        registry.Inner.GetAll().Should().BeEmpty("retired scoped services cannot reacquire a standalone subscription");
    }

    [Fact]
    public async Task StartRuntimeListenerAsync_WhenStartsOverlap_ShouldOwnOneRealHandleWithoutLoserCleanup()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var source = await scope.Resolve<ILocalEventBus>().SubscribeAsync<TestEventMessage>(
            (_, _) => Task.CompletedTask, "diagnostics.row-overlap");
        var entered = Signal();
        var release = Signal();
        var subscribeCount = 0;
        registry.BeforeSubscribe = async (_, _) =>
        {
            Interlocked.Increment(ref subscribeCount);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Token);
        };
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("duplicate cleanup cannot succeed");
        var firstStart = service.StartRuntimeListenerAsync(source.Id);
        Task<Result<RuntimeTestListenerSnapshot>>? secondStart = null;
        try
        {
            await AwaitAsync(entered.Task);
            secondStart = service.StartRuntimeListenerAsync(source.Id);
            secondStart.IsCompleted.Should().BeFalse();
            Volatile.Read(ref subscribeCount).Should().Be(1,
                "only the owner admitted through the service gate may create a registry handle");
            release.TrySetResult(true);
            var first = await AwaitAsync(firstStart);
            var second = await AwaitAsync(secondStart);

            first.Status.Should().Be(ResultStatus.Ok);
            second.Status.Should().Be(ResultStatus.Ok);
            first.Message.Should().BeNullOrEmpty();
            second.Message.Should().BeNullOrEmpty();
            first.Data.Should().NotBeNull();
            second.Data.Should().NotBeNull();
            second.Data!.StartedAt.Should().Be(first.Data!.StartedAt);
            Volatile.Read(ref subscribeCount).Should().Be(1);
            registry.Inner.GetAll().Where(subscription => subscription.Id != source.Id).Should().ContainSingle();
            registry.UnsubscribeAttempts.Should().BeEmpty("serialization avoids creating a losing handle that could leak on cleanup failure");
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(firstStart);
            if (secondStart is not null) await AwaitAsync(secondStart);
            registry.BeforeSubscribe = null;
            registry.BeforeUnsubscribe = null;
        }
    }

    [Fact]
    public async Task StopRuntimeListenerAsync_WhenRemovalIsPending_ShouldSerializeRestartAndRetainTheNewHandle()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var source = await scope.Resolve<ILocalEventBus>().SubscribeAsync<TestEventMessage>(
            (_, _) => Task.CompletedTask, "diagnostics.row-restart");
        var started = await service.StartRuntimeListenerAsync(source.Id);
        started.Status.Should().Be(ResultStatus.Ok);
        var original = registry.Inner.GetAll().Single(subscription => subscription.Id != source.Id);
        var entered = Signal();
        var release = Signal();
        registry.BeforeUnsubscribe = async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Token);
        };
        var stopping = service.StopRuntimeListenerAsync(source.Id);
        Task<Result<RuntimeTestListenerSnapshot>>? restarting = null;
        try
        {
            await AwaitAsync(entered.Task);
            restarting = service.StartRuntimeListenerAsync(source.Id);
            restarting.IsCompleted.Should().BeFalse();
            registry.GetById(original.Id).Should().BeSameAs(original);
            service.GetRuntimeListenerSnapshot(source.Id).IsListening.Should().BeTrue();
            release.TrySetResult(true);

            var stopped = await AwaitAsync(stopping);
            var restarted = await AwaitAsync(restarting);

            stopped.Status.Should().Be(ResultStatus.Ok);
            restarted.Status.Should().Be(ResultStatus.Ok);
            stopped.Message.Should().BeNullOrEmpty();
            restarted.Message.Should().BeNullOrEmpty();
            var replacement = registry.Inner.GetAll().Single(subscription => subscription.Id != source.Id);
            replacement.Id.Should().NotBe(original.Id);
            registry.GetById(original.Id).Should().BeNull();
            service.GetRuntimeListenerSnapshot(source.Id).IsListening.Should().BeTrue();
            registry.UnsubscribeAttempts.Should().Equal(original.Id);
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(stopping);
            if (restarting is not null) await AwaitAsync(restarting);
            registry.BeforeUnsubscribe = null;
        }
    }

    [Fact]
    public async Task StopRuntimeListenerAsync_WhenRemovalFails_ShouldRetainExactHandleAndMessagesUntilRetry()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var bus = scope.Resolve<ILocalEventBus>();
        var source = await bus.SubscribeAsync<TestEventMessage>((_, _) => Task.CompletedTask, "diagnostics.row-retained");
        var started = await service.StartRuntimeListenerAsync(source.Id);
        started.Status.Should().Be(ResultStatus.Ok);
        var listener = registry.Inner.GetAll().Single(subscription => subscription.Id != source.Id);
        await bus.PublishAsync(new TestEventMessage { Message = "retained receipt" }, source.TopicName, Token);
        service.GetRuntimeListenerSnapshot(source.Id).MessageCount.Should().Be(1);
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("temporary row removal failure");
        try
        {
            var failed = await service.StopRuntimeListenerAsync(source.Id);
            var unchanged = await service.StartRuntimeListenerAsync(source.Id);

            failed.IsFailure().Should().BeTrue();
            failed.Message.Should().NotBeNullOrWhiteSpace();
            unchanged.Status.Should().Be(ResultStatus.Ok);
            unchanged.Data.Should().NotBeNull();
            unchanged.Data!.MessageCount.Should().Be(1);
            registry.Inner.GetAll().Where(subscription => subscription.Id != source.Id)
                .Should().ContainSingle().Which.Should().BeSameAs(listener);
            service.GetActiveRuntimeListenerIds().Should().Contain(source.Id.ToString());
        }
        finally { registry.BeforeUnsubscribe = null; }

        var retried = await service.StopRuntimeListenerAsync(source.Id);

        retried.Status.Should().Be(ResultStatus.Ok);
        retried.Message.Should().BeNullOrEmpty();
        registry.GetById(listener.Id).Should().BeNull();
        service.GetRuntimeListenerSnapshot(source.Id).IsListening.Should().BeFalse();
        service.GetActiveRuntimeListenerIds().Should().BeEmpty();
        registry.UnsubscribeAttempts.Should().Equal(listener.Id, listener.Id);
    }

    [Fact]
    public async Task DisposeAsync_WhenRowStartIsPending_ShouldDrainAdoptionCleanItsHandleAndRejectQueuedStarts()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var source = await scope.Resolve<ILocalEventBus>().SubscribeAsync<TestEventMessage>(
            (_, _) => Task.CompletedTask, "diagnostics.row-retirement");
        var entered = Signal();
        var release = Signal();
        var subscribeCount = 0;
        registry.BeforeSubscribe = async (_, _) =>
        {
            Interlocked.Increment(ref subscribeCount);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Token);
        };
        var starting = service.StartRuntimeListenerAsync(source.Id);
        Task? disposing = null;
        Task<Result<RuntimeTestListenerSnapshot>>? queuedStart = null;
        try
        {
            await AwaitAsync(entered.Task);
            queuedStart = service.StartRuntimeListenerAsync(source.Id);
            disposing = service.DisposeAsync().AsTask();
            disposing.IsCompleted.Should().BeFalse("scope cleanup must wait for the admitted registry start");
            queuedStart.IsCompleted.Should().BeFalse();
            release.TrySetResult(true);

            var started = await AwaitAsync(starting);
            var rejected = await AwaitAsync(queuedStart);
            await AwaitAsync(disposing);

            started.Status.Should().Be(ResultStatus.Ok);
            rejected.IsFailure().Should().BeTrue();
            rejected.Message.Should().NotBeNullOrWhiteSpace();
            Volatile.Read(ref subscribeCount).Should().Be(1);
            registry.Inner.GetAll().Should().ContainSingle().Which.Id.Should().Be(source.Id);
            registry.UnsubscribeAttempts.Should().ContainSingle().Which.Should().NotBe(source.Id);
            service.GetActiveRuntimeListenerIds().Should().BeEmpty();
            var lateStart = await service.StartRuntimeListenerAsync(source.Id);
            lateStart.IsFailure().Should().BeTrue();
            lateStart.Message.Should().NotBeNullOrWhiteSpace();
            Volatile.Read(ref subscribeCount).Should().Be(1);
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(starting);
            if (queuedStart is not null) await AwaitAsync(queuedStart);
            if (disposing is not null) await AwaitAsync(disposing);
            registry.BeforeSubscribe = null;
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenRowRemovalFails_ShouldRetainOwnershipForRepeatedCleanupWithoutAllowingStarts()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var service = scope.Resolve<EventBusTestService>();
        var source = await scope.Resolve<ILocalEventBus>().SubscribeAsync<TestEventMessage>(
            (_, _) => Task.CompletedTask, "diagnostics.row-dispose-retry");
        var started = await service.StartRuntimeListenerAsync(source.Id);
        started.Status.Should().Be(ResultStatus.Ok);
        var listener = registry.Inner.GetAll().Single(subscription => subscription.Id != source.Id);
        registry.BeforeUnsubscribe = (_, _) => throw new InvalidOperationException("temporary row cleanup failure");
        try
        {
            await service.DisposeAsync();
            service.GetRuntimeListenerSnapshot(source.Id).IsListening.Should().BeTrue();
            registry.GetById(listener.Id).Should().BeSameAs(listener);
            var lateStart = await service.StartRuntimeListenerAsync(source.Id);
            lateStart.IsFailure().Should().BeTrue();
            lateStart.Message.Should().NotBeNullOrWhiteSpace();
        }
        finally { registry.BeforeUnsubscribe = null; }

        await service.DisposeAsync();
        await service.DisposeAsync();

        service.GetRuntimeListenerSnapshot(source.Id).IsListening.Should().BeFalse();
        registry.Inner.GetAll().Should().ContainSingle().Which.Id.Should().Be(source.Id);
        registry.UnsubscribeAttempts.Should().Equal(listener.Id, listener.Id);
    }

    private static Task<MonicaTestApplication> CreateAsync(ControlledRegistry registry) =>
        Factory.CreateAsync(scenario => scenario.With<IEventSubscriptionRegistry>(registry), Token);

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task AwaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static Task<T> AwaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
}
