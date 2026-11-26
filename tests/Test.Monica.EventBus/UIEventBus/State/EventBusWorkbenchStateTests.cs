using AwesomeAssertions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Testing.Hosting;
using Test.Monica.EventBus.UIEventBus.State.Support;
using Xunit;

namespace Test.Monica.EventBus.UIEventBus.State;

public sealed class EventBusWorkbenchStateTests
{
    private static readonly EventBusWorkbenchScenarioFactory Factory = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddEventBusUI_WhenScopesDiffer_ShouldShareMonitorAndIsolateBorrowedCaptureState()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var firstScope = application.CreateScope(Token);
        await using var secondScope = application.CreateScope(Token);
        var first = firstScope.Resolve<EventBusWorkbenchState>();
        var second = secondScope.Resolve<EventBusWorkbenchState>();

        firstScope.Resolve<EventBusWorkbenchState>().Should().BeSameAs(first);
        second.Should().NotBeSameAs(first);
        firstScope.Resolve<EventBusMonitorService>().Should().BeSameAs(secondScope.Resolve<EventBusMonitorService>());
        await first.RefreshAsync(Token);
        first.RecordChange(first.BeginObservation(), RegistryChange(new SubscriptionViewModel(), DateTime.UtcNow), 1, Token)
            .Should().BeTrue();

        second.Snapshot.Should().BeNull();
        second.ObservedChanges.Should().BeEmpty();
        second.DroppedChangeCount.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenScopesKeysAndRuntimeDiffer_ShouldCaptureExactCountsWithoutInventingHealth()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        var store = scope.Resolve<ITopicSubscriptionStatusStore>();
        _ = await SubscribeAsync(registry, null, EventSubscriptionScope.Local);
        _ = await SubscribeAsync(registry, "default", EventSubscriptionScope.Local);
        _ = await SubscribeAsync(registry, "orders", EventSubscriptionScope.Local);
        var inactive = await SubscribeAsync(registry, "Orders", EventSubscriptionScope.Local);
        await registry.DeactivateAsync(inactive.Id, Token);
        var failed = await SubscribeAsync(registry, "orders", EventSubscriptionScope.Distributed, autoDiscovered: true);
        var unknown = await SubscribeAsync(registry, "missing", EventSubscriptionScope.Distributed);
        _ = await SubscribeAsync(registry, null, EventSubscriptionScope.Local, hidden: true);
        store.ReportState("orders", "orders.approved", EventBusProviderKind.Kafka, TopicSubscriptionRuntimeState.Failed);
        store.ReportState(null, "unreferenced", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Recovering);
        store.ReportError("orders", "orders.approved", "receiver failed");

        await state.RefreshAsync(Token);

        var capture = state.Snapshot;
        capture.Should().NotBeNull();
        capture!.Subscriptions.Should().HaveCount(6);
        capture.Statistics.TotalSubscriptions.Should().Be(6);
        capture.Statistics.ActiveSubscriptions.Should().Be(5);
        capture.Statistics.InactiveSubscriptions.Should().Be(1);
        capture.Statistics.LocalSubscriptions.Should().Be(4);
        capture.Statistics.DistributedSubscriptions.Should().Be(2);
        capture.Statistics.UnhealthySubscriptions.Should().Be(1);
        capture.Statistics.UnhealthyTopics.Should().Be(2, "topic health retains store scope, including an unreferenced topic");
        capture.Statistics.AutoDiscoveredCount.Should().Be(1);
        capture.Statistics.ManualSubscriptionCount.Should().Be(5);
        capture.Subscriptions.Single(row => row.SubscriptionId == unknown.Id.ToString()).TopicStatus.Should().BeNull();
        var failedRow = capture.Subscriptions.Single(row => row.SubscriptionId == failed.Id.ToString());
        failedRow.TopicStatus.Should().BeSameAs(capture.TopicStatuses.Single(topic => topic.ServiceKey == "orders"));
        failedRow.TopicRuntimeState.Should().Be(TopicSubscriptionRuntimeState.Failed);
        // Standard host seams replace the default buses and transport with RecordingEventBus; keyed local buses remain real.
        capture.Providers.Should().HaveCount(5);
        capture.Providers.Where(provider => !provider.IsDistributed).Should().HaveCount(4);
        var defaultLocal = capture.Providers.Single(provider => provider.ServiceKey is null && !provider.IsDistributed);
        var defaultDistributed = capture.Providers.Single(provider => provider.ServiceKey is null && provider.IsDistributed);
        defaultLocal.SubscriptionCount.Should().Be(1);
        defaultLocal.ActiveSubscriptionCount.Should().Be(1);
        defaultDistributed.SubscriptionCount.Should().Be(0, "a distributed provider must not count a local handler sharing its null key");
        defaultDistributed.ActiveSubscriptionCount.Should().Be(0);
        defaultDistributed.UnhealthyTopicCount.Should().Be(1, "its topic evidence includes the unreferenced default-key topic");
        defaultDistributed.ProviderType.Should().Be(EventBusProviderKind.Unknown, "the recording seam has no registered provider-module metadata");
        defaultDistributed.Capabilities.Should().Be(EventBusProviderCapabilities.None);
        defaultDistributed.UniqueId.Should().NotBe(defaultLocal.UniqueId);
        capture.Providers.Single(provider => provider.ServiceKey == "default" && !provider.IsDistributed).SubscriptionCount.Should().Be(1);
        capture.Providers.Single(provider => provider.ServiceKey == "orders" && !provider.IsDistributed).SubscriptionCount.Should().Be(1);
        capture.Providers.Single(provider => provider.ServiceKey == "Orders" && !provider.IsDistributed).ActiveSubscriptionCount.Should().Be(0);
        capture.Providers.Where(provider => !provider.IsDistributed).Should().OnlyContain(provider => provider.UnhealthyTopicCount == 0);
        state.ApplyFilter(new SubscriptionFilter
        {
            ServiceKey = "orders",
            SelectedProvider = capture.Providers.Single(provider => provider.ServiceKey == "orders" && !provider.IsDistributed)
        }).Should().ContainSingle().Which.Scope.Should().Be(EventSubscriptionScope.Local);
        state.RefreshError.Should().BeNull();
        state.HasAttemptedCapture.Should().BeTrue();
        state.IsRefreshing.Should().BeFalse();
        state.CapturedAt.Should().Be(capture.CapturedAt);
    }

    [Fact]
    public async Task RefreshAsync_WhenRegistryChangesBetweenServiceReads_ShouldDeriveProviderCountsFromCapturedHandlers()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        var first = await SubscribeAsync(registry, null, EventSubscriptionScope.Local);
        registry.SetReadHook(call =>
        {
            if (call != 2) return;
            registry.DeactivateAsync(first.Id, Token).GetAwaiter().GetResult();
            SubscribeAsync(registry, null, EventSubscriptionScope.Local).GetAwaiter().GetResult();
        });

        await state.RefreshAsync(Token);

        var capture = state.Snapshot!;
        capture.Subscriptions.Should().ContainSingle().Which.State.Should().Be(EventSubscriptionState.Active);
        capture.Statistics.TotalSubscriptions.Should().Be(1);
        capture.Statistics.ActiveSubscriptions.Should().Be(1);
        var provider = capture.Providers.Single(row => row.ServiceKey is null && !row.IsDistributed);
        provider.SubscriptionCount.Should().Be(1, "a second service read must not replace the capture's handler scope");
        provider.ActiveSubscriptionCount.Should().Be(1);
        var distributed = capture.Providers.Single(row => row.ServiceKey is null && row.IsDistributed);
        distributed.SubscriptionCount.Should().Be(0, "neither captured nor later local handlers belong to the default distributed scope");
        distributed.ActiveSubscriptionCount.Should().Be(0);
        registry.Inner.GetAll().Should().HaveCount(2).And.ContainSingle(row => row.State == EventSubscriptionState.Inactive);
    }

    [Fact]
    public async Task RefreshAsync_WhenReadFails_ShouldRetainLastCaptureAndClearErrorOnlyAfterSuccess()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        registry.SetReadHook(_ => throw new InvalidOperationException("registry unavailable"));

        await state.RefreshAsync(Token);

        state.HasAttemptedCapture.Should().BeTrue();
        state.Snapshot.Should().BeNull();
        state.Statistics.Should().BeNull();
        state.RefreshError.Should().NotBeNullOrWhiteSpace();
        registry.SetReadHook(null);
        _ = await SubscribeAsync(registry, null, EventSubscriptionScope.Local);
        await state.RefreshAsync(Token);
        var successful = state.Snapshot;
        registry.SetReadHook(_ => throw new InvalidOperationException("registry unavailable again"));

        await state.RefreshAsync(Token);

        state.Snapshot.Should().BeSameAs(successful);
        state.CapturedAt.Should().Be(successful!.CapturedAt);
        state.RefreshError.Should().NotBeNullOrWhiteSpace();
        state.IsRefreshing.Should().BeFalse();
        registry.SetReadHook(null);
        await state.RefreshAsync(Token);
        state.Snapshot.Should().NotBeSameAs(successful);
        state.RefreshError.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_WhenCancellationPrecedesReadCompletion_ShouldRejectLateSuccessOrCaughtFailure(bool failRead)
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        _ = await SubscribeAsync(registry, null, EventSubscriptionScope.Local);
        await state.RefreshAsync(Token);
        var capture = state.Snapshot;
        registry.SetReadHook(_ => throw new InvalidOperationException("previous error"));
        await state.RefreshAsync(Token);
        var previousError = state.RefreshError;
        var entered = Signal();
        var release = Signal();
        registry.SetReadHook(_ =>
        {
            entered.TrySetResult(true);
            release.Task.Wait(Token);
            if (failRead) throw new InvalidOperationException("late error");
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresh = Task.Run(() => state.RefreshAsync(lifetime.Token), Token);
        try
        {
            await AwaitAsync(entered.Task);
            state.IsRefreshing.Should().BeTrue();
            lifetime.Cancel();
            release.TrySetResult(true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(refresh));

            state.Snapshot.Should().BeSameAs(capture);
            state.CapturedAt.Should().Be(capture!.CapturedAt);
            state.RefreshError.Should().Be(previousError, "cancelled work cannot overwrite either prior data or error evidence");
            state.IsRefreshing.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            try { await AwaitAsync(refresh); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenAnotherRefreshIsReading_ShouldSerializeAndCancelQueuedAdmission()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        var entered = Signal();
        var release = Signal();
        registry.SetReadHook(call =>
        {
            if (call != 1) return;
            entered.TrySetResult(true);
            release.Task.Wait(Token);
        });
        var first = Task.Run(() => state.RefreshAsync(Token), Token);
        using var queuedLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task? queued = null;
        try
        {
            await AwaitAsync(entered.Task);
            state.IsInitialLoading.Should().BeTrue();
            queued = state.RefreshAsync(queuedLifetime.Token);
            queued.IsCompleted.Should().BeFalse();
            registry.ReadCount.Should().Be(1);
            queuedLifetime.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(queued));

            registry.ReadCount.Should().Be(1, "a cancelled queued refresh must never enter the registry");
            state.IsRefreshing.Should().BeTrue("the original refresh still owns its gate");
            release.TrySetResult(true);
            await AwaitAsync(first);
            state.Snapshot.Should().NotBeNull();
            state.IsInitialLoading.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(first);
            if (queued is not null)
            {
                try { await AwaitAsync(queued); }
                catch (OperationCanceledException) when (queuedLifetime.IsCancellationRequested) { }
            }
        }
    }

    [Fact]
    public async Task RecordChange_WhenWindowTruncatesOrFeedLosesNotifications_ShouldDistinguishObservedHistoryFromLoss()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        _ = await SubscribeAsync(registry, "orders", EventSubscriptionScope.Distributed);
        await state.RefreshAsync(Token);
        var capture = state.Snapshot!;
        var notificationRow = EventBusWorkbenchState.CloneSubscription(capture.Subscriptions.Single());
        notificationRow.Metadata["source"] = "original";
        var generation = state.BeginObservation();
        var firstTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 105; index++)
        {
            state.RecordChange(generation, RegistryChange(notificationRow, firstTime.AddSeconds(index)), 4, Token)
                .Should().BeTrue();
        }

        notificationRow.Metadata["source"] = "changed by producer";
        notificationRow.State = EventSubscriptionState.Disposed;

        state.ObservedChangeCount.Should().Be(105);
        state.ObservedChanges.Should().HaveCount(100);
        state.ObservedChanges[0].Timestamp.Should().Be(firstTime.AddSeconds(104));
        state.ObservedChanges[^1].Timestamp.Should().Be(firstTime.AddSeconds(5));
        state.ObservedChanges.Should().OnlyContain(change => change.Subscription.Metadata["source"] == "original");
        state.ObservedChanges.Should().OnlyContain(change => change.Subscription.State == EventSubscriptionState.Active);
        state.DroppedChangeCount.Should().Be(4, "repeated cumulative feed reports do not double-count loss or window truncation");
        state.Snapshot.Should().BeSameAs(capture);
        var topic = TopicStatus("orders", "orders.approved", []);
        state.RecordChange(generation, new EventBusMonitorChange.TopicStatusChanged(topic), 7, Token).Should().BeTrue();
        state.ObservedChangeCount.Should().Be(105, "topic invalidations are not registry lifecycle history");
        state.DroppedChangeCount.Should().Be(7);
        var nextGeneration = state.BeginObservation();
        state.RecordChange(nextGeneration, new EventBusMonitorChange.TopicStatusChanged(topic), 2, Token).Should().BeTrue();
        state.DroppedChangeCount.Should().Be(9, "each new feed reports its own cumulative counter");
        state.ObservedChanges.Should().HaveCount(100, "retained circuit history survives a mounted view changing");
    }

    [Fact]
    public async Task RecordChange_WhenOwnerRetiresOrCancels_ShouldRejectLateUpdatesAndPreserveNewOwner()
    {
        await using var registry = new ControlledRegistry();
        await using var application = await CreateAsync(registry);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<EventBusWorkbenchState>();
        var oldGeneration = state.BeginObservation();
        var activeGeneration = state.BeginObservation();
        var change = RegistryChange(new SubscriptionViewModel(), DateTime.UtcNow);
        state.EndObservation(oldGeneration);

        state.RecordChange(oldGeneration, change, 20, Token).Should().BeFalse();
        using var retiredLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        retiredLifetime.Cancel();
        state.RecordChange(activeGeneration, change, 20, retiredLifetime.Token).Should().BeFalse();
        state.RecordChange(activeGeneration, change, 2, Token).Should().BeTrue();
        state.EndObservation(activeGeneration);
        state.RecordChange(activeGeneration, change, 30, Token).Should().BeFalse();

        state.ObservedChanges.Should().ContainSingle();
        state.ObservedChangeCount.Should().Be(1);
        state.DroppedChangeCount.Should().Be(2);
    }

    [Fact]
    public void CloneSubscription_WhenProducerAndInspectorMutate_ShouldDetachMetadataAndTopicErrorCollections()
    {
        var errors = new List<TopicSubscriptionError> { new(DateTime.UtcNow, "original error", null) };
        var source = new SubscriptionViewModel
        {
            SubscriptionId = "identity",
            ServiceKey = "Orders",
            WorkloadOwner = "order.handler",
            ReceiverInstanceId = Guid.NewGuid(),
            IsTransportReady = true,
            EventType = "Events.OrderApproved",
            HandlerType = "Handlers.ApprovalHandler",
            ActionMethodSignature = "handler-signature",
            State = EventSubscriptionState.Active,
            Scope = EventSubscriptionScope.Distributed,
            TopicStatus = TopicStatus("Orders", "orders.approved", errors),
            Metadata = new Dictionary<string, string> { ["origin"] = "registry" }
        };

        var first = EventBusWorkbenchState.CloneSubscription(source);
        var inspector = EventBusWorkbenchState.CloneSubscription(first);
        errors.Clear();
        source.IsTransportReady = false;
        source.Metadata["origin"] = "producer changed";
        inspector.Metadata["origin"] = "inspector changed";
        inspector.State = EventSubscriptionState.Disposed;

        first.Metadata["origin"].Should().Be("registry");
        first.State.Should().Be(EventSubscriptionState.Active);
        first.TopicStatus!.RecentErrors.Should().ContainSingle().Which.Message.Should().Be("original error");
        inspector.TopicStatus!.RecentErrors.Should().ContainSingle();
        first.TopicStatus.Should().NotBeSameAs(source.TopicStatus);
        inspector.TopicStatus.Should().NotBeSameAs(first.TopicStatus);
        inspector.ServiceKey.Should().Be("Orders");
        inspector.WorkloadOwner.Should().Be(source.WorkloadOwner);
        inspector.ReceiverInstanceId.Should().Be(source.ReceiverInstanceId);
        inspector.IsTransportReady.Should().BeTrue();
        inspector.SubscriptionId.Should().Be(source.SubscriptionId);
        inspector.ActionMethodSignature.Should().Be(source.ActionMethodSignature);
    }

    private static Task<MonicaTestApplication> CreateAsync(ControlledRegistry registry) =>
        Factory.CreateAsync(scenario => scenario.With<IEventSubscriptionRegistry>(registry), Token);

    private static Task<IEventSubscription> SubscribeAsync(
        IEventSubscriptionRegistry registry, string? key, EventSubscriptionScope scope,
        bool autoDiscovered = false, bool hidden = false) => registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.approved",
            HandlerFactory = new MetadataOnlyHandlerFactory(),
            Scope = scope,
            ServiceKey = key,
            IsAutoDiscovered = autoDiscovered,
            Metadata = hidden ? new Dictionary<string, object> { ["Monica.EventBusUI.TestListener"] = true } : null
        }, Token);

    private static EventBusMonitorChange RegistryChange(SubscriptionViewModel row, DateTime timestamp) =>
        new EventBusMonitorChange.SubscriptionChanged(new SubscriptionChangeViewModel
        {
            Subscription = row,
            Timestamp = timestamp,
            ChangeType = EventSubscriptionChangeType.Activated
        });

    private static TopicSubscriptionStatus TopicStatus(
        string? key, string topic, IReadOnlyList<TopicSubscriptionError> errors) => new()
        {
            ServiceKey = key,
            TopicName = topic,
            Provider = EventBusProviderKind.Kafka,
            State = TopicSubscriptionRuntimeState.Failed,
            StateChangedAt = DateTime.UtcNow,
            ConsecutiveFailures = 1,
            RecoveryCount = 1,
            MessageErrorCount = 0,
            ProcessedMessages = 0,
            RecentErrors = errors
        };

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task AwaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);

    private sealed record SampleEvent(string Value);

    private sealed class MetadataOnlyHandlerFactory : IEventHandlerFactory
    {
        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync() =>
            throw new NotSupportedException("Capture scenarios never execute handlers.");

        // No handler interface: this metadata marker must not participate in other tests' automatic discovery.
        public Type? GetHandlerType() => typeof(SampleEvent);
    }
}
