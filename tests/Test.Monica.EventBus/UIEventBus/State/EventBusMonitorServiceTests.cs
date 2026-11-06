using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Xunit;

namespace Test.Monica.EventBus.UIEventBus.State;

/// <summary>Exercises monitor contracts against the real in-memory registry and topic-status store, without rendering UI.</summary>
public sealed class EventBusMonitorServiceTests
{
    [Fact]
    public async Task OpenChangeFeed_WhenTwoConsumersObserve_ShouldBroadcastRegistryAndRuntimeChangesToBoth()
    {
        await using var fixture = new MonitorFixture();
        using var first = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        using var second = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        var subscription = await fixture.SubscribeAsync("orders", EventSubscriptionScope.Distributed);
        fixture.Store.ReportState("orders", "orders.approved", EventBusProviderKind.Kafka, TopicSubscriptionRuntimeState.Recovering);

        foreach (var feed in new[] { first, second })
        {
            var changes = Drain(feed);
            changes.Should().HaveCount(3);
            changes.OfType<EventBusMonitorChange.SubscriptionChanged>()
                .Select(change => change.Change.ChangeType)
                .Should().Equal(EventSubscriptionChangeType.Added, EventSubscriptionChangeType.Activated);
            changes.OfType<EventBusMonitorChange.SubscriptionChanged>()
                .Should().OnlyContain(change => change.Change.Subscription.SubscriptionId == subscription.Id.ToString());
            changes.OfType<EventBusMonitorChange.TopicStatusChanged>().Should().ContainSingle()
                .Which.Status.State.Should().Be(TopicSubscriptionRuntimeState.Recovering);
            feed.DroppedChangeCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Dispose_WhenOneConsumerRetires_ShouldCompleteOnlyItsOwnReader()
    {
        await using var fixture = new MonitorFixture();
        var retired = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        using var remaining = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        retired.Dispose();
        retired.Dispose();
        fixture.Store.ReportState(null, "orders", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Healthy);

        retired.Reader.TryRead(out _).Should().BeFalse();
        retired.Reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
        remaining.Reader.TryRead(out var change).Should().BeTrue();
        change.Should().BeOfType<EventBusMonitorChange.TopicStatusChanged>();
        remaining.Reader.Completion.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task OpenChangeFeed_WhenLifetimeIsCancelled_ShouldRetireOnlyThatConsumer()
    {
        await using var fixture = new MonitorFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var cancelled = fixture.Monitor.OpenChangeFeed(cancellation.Token);
        using var remaining = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        fixture.Store.ReportError(null, "orders", "delivery failure");

        cancelled.Reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
        cancelled.Reader.TryRead(out _).Should().BeFalse();
        Drain(remaining).Should().ContainSingle();
    }

    [Fact]
    public async Task SubscribeToChanges_WhenNoLifetimeCanOwnReader_ShouldRejectOwnerlessAdapter()
    {
        await using var fixture = new MonitorFixture();

        Action act = () => { _ = fixture.Monitor.SubscribeToChanges(CancellationToken.None); };

        act.Should().Throw<ArgumentException>().WithParameterName("cancellationToken");
    }

    [Fact]
    public async Task SubscribeToChanges_WhenLifetimeIsCancelled_ShouldReleaseAdaptedReader()
    {
        await using var fixture = new MonitorFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reader = fixture.Monitor.SubscribeToChanges(cancellation.Token);

        await cancellation.CancelAsync();
        fixture.Store.ReportError(null, "orders", "after retirement");

        reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
        reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task OpenChangeFeed_WhenOpenedAfterEarlierChanges_ShouldStartWithoutHistoricalReplay()
    {
        await using var fixture = new MonitorFixture();
        using var earlier = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        fixture.Store.ReportError(null, "orders", "before second consumer");

        using var later = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        Drain(earlier).Should().ContainSingle();
        Drain(later).Should().BeEmpty();
        later.DroppedChangeCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenChangeFeed_WhenOneReaderFallsBehind_ShouldBoundItsBufferAndReportOnlyItsOwnLoss()
    {
        await using var fixture = new MonitorFixture();
        using var delayed = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        using var prompt = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        for (var index = 0; index < 300; index++)
        {
            fixture.Store.ReportState(null, $"orders-{index:D3}", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Healthy);
            prompt.Reader.TryRead(out var change).Should().BeTrue();
            change.Should().BeOfType<EventBusMonitorChange.TopicStatusChanged>()
                .Which.Status.TopicName.Should().Be($"orders-{index:D3}");
        }

        var retained = Drain(delayed).OfType<EventBusMonitorChange.TopicStatusChanged>().ToList();
        retained.Should().HaveCount(256);
        retained.First().Status.TopicName.Should().Be("orders-044");
        retained.Last().Status.TopicName.Should().Be("orders-299");
        delayed.DroppedChangeCount.Should().Be(44);
        prompt.DroppedChangeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenHostRetires_ShouldCloseAllFeedsAndRejectNewConsumersIdempotently()
    {
        await using var fixture = new MonitorFixture();
        using var first = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        using var second = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);

        await fixture.Monitor.DisposeAsync();
        await fixture.Monitor.DisposeAsync();
        fixture.Store.ReportError(null, "orders", "after host retirement");

        first.Reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
        second.Reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
        Action act = () => { _ = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken); };
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetStatisticsAsync_WhenRuntimeListenerIsRegistered_ShouldExcludeItFromViewsCountsAndRegistryChanges()
    {
        await using var fixture = new MonitorFixture();
        using var feed = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        var visible = await fixture.SubscribeAsync(null, EventSubscriptionScope.Local);
        Drain(feed).Should().HaveCount(2);
        _ = await fixture.Registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.approved",
            Scope = EventSubscriptionScope.Local,
            HandlerFactory = new UnusedHandlerFactory(),
            Metadata = new Dictionary<string, object> { ["Monica.EventBusUI.TestListener"] = true }
        }, TestContext.Current.CancellationToken);

        var subscriptions = await fixture.Monitor.GetAllSubscriptionsAsync();
        var statistics = await fixture.Monitor.GetStatisticsAsync();

        subscriptions.Status.Should().Be(ResultStatus.Ok);
        subscriptions.Message.Should().BeNullOrEmpty();
        subscriptions.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(visible.Id.ToString());
        statistics.Status.Should().Be(ResultStatus.Ok);
        statistics.Data!.TotalSubscriptions.Should().Be(1);
        statistics.Data.ActiveSubscriptions.Should().Be(1);
        Drain(feed).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllTopicStatuses_WhenMessagesAreProcessed_ShouldRefreshCountersWithoutInventingChangeNotifications()
    {
        await using var fixture = new MonitorFixture();
        using var feed = fixture.Monitor.OpenChangeFeed(TestContext.Current.CancellationToken);
        fixture.Store.ReportState(null, "orders", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Healthy);
        Drain(feed).Should().ContainSingle();

        fixture.Store.ReportMessageProcessed(null, "orders");
        var statuses = fixture.Monitor.GetAllTopicStatuses();

        statuses.Status.Should().Be(ResultStatus.Ok);
        statuses.Data.Should().ContainSingle().Which.ProcessedMessages.Should().Be(1);
        Drain(feed).Should().BeEmpty();
    }

    [Fact]
    public async Task FilterSubscriptionsAsync_WhenFiltersCombine_ShouldKeepExactKeyScopeStateAndDiscoveryConjunctive()
    {
        await using var fixture = new MonitorFixture();
        var matching = await fixture.SubscribeAsync("orders", EventSubscriptionScope.Distributed, autoDiscovered: true);
        _ = await fixture.SubscribeAsync("Orders", EventSubscriptionScope.Distributed, autoDiscovered: true);
        _ = await fixture.SubscribeAsync("orders", EventSubscriptionScope.Local, autoDiscovered: true);
        _ = await fixture.SubscribeAsync("orders", EventSubscriptionScope.Distributed);
        fixture.Store.ReportState("orders", "orders.approved", EventBusProviderKind.Kafka, TopicSubscriptionRuntimeState.Failed);
        var filter = new SubscriptionFilter
        {
            State = EventSubscriptionState.Active,
            TopicRuntimeState = TopicSubscriptionRuntimeState.Failed,
            Scope = EventSubscriptionScope.Distributed,
            ServiceKey = "orders",
            IsAutoDiscovered = true,
            SelectedProvider = new EventBusProviderInfo { ServiceKey = "orders", IsDistributed = true }
        };

        var result = await fixture.Monitor.FilterSubscriptionsAsync(filter);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(matching.Id.ToString());
        result.Data![0].State.Should().Be(EventSubscriptionState.Active);
        result.Data[0].TopicRuntimeState.Should().Be(TopicSubscriptionRuntimeState.Failed);

        filter.ServiceKey = "another-key";
        var contradictory = await fixture.Monitor.FilterSubscriptionsAsync(filter);
        contradictory.Status.Should().Be(ResultStatus.Ok);
        contradictory.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task FilterSubscriptionsAsync_WhenDefaultAndNamedDefaultCoexist_ShouldKeepThemSeparate()
    {
        await using var fixture = new MonitorFixture();
        var defaultSubscription = await fixture.SubscribeAsync(null, EventSubscriptionScope.Local);
        var namedSubscription = await fixture.SubscribeAsync("default", EventSubscriptionScope.Local);

        var defaultResult = await fixture.Monitor.FilterSubscriptionsAsync(new SubscriptionFilter
        {
            SelectedProvider = new EventBusProviderInfo { ServiceKey = null, IsDistributed = false }
        });
        var namedResult = await fixture.Monitor.FilterSubscriptionsAsync(new SubscriptionFilter
        {
            SelectedProvider = new EventBusProviderInfo { ServiceKey = "default", IsDistributed = false }
        });

        defaultResult.Status.Should().Be(ResultStatus.Ok);
        defaultResult.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(defaultSubscription.Id.ToString());
        namedResult.Status.Should().Be(ResultStatus.Ok);
        namedResult.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(namedSubscription.Id.ToString());
    }

    [Fact]
    public async Task FilterSubscriptionsAsync_WhenRuntimeStatusIsUnreported_ShouldMatchNoConcreteRuntimeState()
    {
        await using var fixture = new MonitorFixture();
        _ = await fixture.SubscribeAsync("orders", EventSubscriptionScope.Distributed);
        _ = await fixture.SubscribeAsync(null, EventSubscriptionScope.Local);

        var result = await fixture.Monitor.FilterSubscriptionsAsync(new SubscriptionFilter
        {
            TopicRuntimeState = TopicSubscriptionRuntimeState.Healthy
        });

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData("test.monica.eventbus.uieventbus.state.eventbusmonitorservicetests.sampleevent")]
    [InlineData("samplehandler")]
    [InlineData("ORDERS.APPROVED")]
    public async Task FilterSubscriptionsAsync_WhenSearchingFullTypeHandlerOrTopic_ShouldUseCaseInsensitiveMappedNames(string search)
    {
        await using var fixture = new MonitorFixture();
        var subscription = await fixture.SubscribeAsync(null, EventSubscriptionScope.Local);

        var result = await fixture.Monitor.FilterSubscriptionsAsync(new SubscriptionFilter { SearchText = search });

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(subscription.Id.ToString());
    }

    [Fact]
    public async Task FilterSubscriptionsAsync_WhenSearchingHyphenatedGuid_ShouldMatchItsCanonicalSubscriptionId()
    {
        await using var fixture = new MonitorFixture();
        var subscription = await fixture.SubscribeAsync(null, EventSubscriptionScope.Local);

        var result = await fixture.Monitor.FilterSubscriptionsAsync(new SubscriptionFilter
        {
            SearchText = subscription.Id.Value.ToString("D")
        });

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().ContainSingle().Which.SubscriptionId.Should().Be(subscription.Id.ToString());
    }

    [Fact]
    public void ApplyFilter_WhenSearchingActionMetadata_ShouldRetainSnapshotOrderingAndRequireEveryOtherFilter()
    {
        var older = new SubscriptionViewModel
        {
            SubscriptionId = "11111111111111111111111111111111",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ActionDeclaringType = "Orders.ApprovalActions",
            ActionMethodName = "HandleApproval",
            State = EventSubscriptionState.Inactive,
            Scope = EventSubscriptionScope.Local
        };
        var newer = new SubscriptionViewModel
        {
            SubscriptionId = "22222222222222222222222222222222",
            CreatedAt = older.CreatedAt.AddHours(1),
            ActionMethodSignature = "ApprovalActions.HandleApproval(SampleEvent, CancellationToken)",
            State = EventSubscriptionState.Active,
            Scope = EventSubscriptionScope.Local
        };
        var filter = new SubscriptionFilter { SearchText = "  handleapproval  " };

        EventBusMonitorService.ApplyFilter([older, newer], filter).Should().Equal(newer, older);
        filter.State = EventSubscriptionState.Inactive;
        EventBusMonitorService.ApplyFilter([older, newer], filter).Should().Equal(older);
    }

    private static List<EventBusMonitorChange> Drain(EventBusMonitorChangeFeed feed)
    {
        var result = new List<EventBusMonitorChange>();
        while (feed.Reader.TryRead(out var change))
        {
            result.Add(change);
        }

        return result;
    }

    private sealed class MonitorFixture : IAsyncDisposable
    {
        public EventSubscriptionRegistry Registry { get; } = new(NullLogger<EventSubscriptionRegistry>.Instance);
        public TopicSubscriptionStatusStore Store { get; } = new();
        public EventBusMonitorService Monitor { get; }

        public MonitorFixture()
        {
            Monitor = new EventBusMonitorService(Registry, Store, new EchoLocalizer(), NullLogger<EventBusMonitorService>.Instance);
        }

        public Task<IEventSubscription> SubscribeAsync(string? key, EventSubscriptionScope scope, bool autoDiscovered = false) =>
            Registry.SubscribeAsync(new EventSubscriptionDescriptor
            {
                EventType = typeof(SampleEvent),
                TopicName = "orders.approved",
                HandlerFactory = new UnusedHandlerFactory(),
                Scope = scope,
                ServiceKey = key,
                IsAutoDiscovered = autoDiscovered
            }, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync();
            await Registry.UnsubscribeBatchAsync(Registry.GetAll().Select(subscription => subscription.Id).ToList());
        }
    }

    private sealed record SampleEvent(string Value);

    /// <summary>Provides handler-name metadata without adding a discoverable business handler to this test assembly.</summary>
    private sealed class SampleHandler
    {
    }

    private sealed class UnusedHandlerFactory : IEventHandlerFactory
    {
        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync() =>
            throw new NotSupportedException("Monitor queries never execute the registered handler.");

        public Type? GetHandlerType() => typeof(SampleHandler);
    }

    private sealed class EchoLocalizer : IStringLocalizer<EventBusResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
