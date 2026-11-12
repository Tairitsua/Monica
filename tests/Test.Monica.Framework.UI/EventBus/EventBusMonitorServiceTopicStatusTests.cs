using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Modules;
using MudBlazor;
using Xunit;

namespace Test.Monica.Framework.UI.EventBus;

public sealed class EventBusMonitorServiceTopicStatusTests
{
    [Fact]
    public async Task GetAllSubscriptionsAsync_WhenTopicIsRecovering_ShouldJoinRuntimeStatusIntoViews()
    {
        await using var host = CreateHost();
        var monitor = host.Services.GetRequiredService<EventBusMonitorService>();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var store = host.Services.GetRequiredService<ITopicSubscriptionStatusStore>();

        await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.shipped",
            HandlerFactory = new UnusedHandlerFactory(),
            Scope = EventSubscriptionScope.Distributed
        }, TestContext.Current.CancellationToken);

        store.ReportState(
            null, "orders.shipped", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Recovering,
            "recovery scheduled in 00:00:05", new InvalidOperationException("stream closed"));

        var result = await monitor.GetAllSubscriptionsAsync();

        var subscriptions = result.Data;
        result.IsFailure().Should().BeFalse();
        var vm = subscriptions!.Single(v => v.TopicName == "orders.shipped");
        vm.TopicRuntimeState.Should().Be(TopicSubscriptionRuntimeState.Recovering);
        vm.IsRuntimeStateDisplayed.Should().BeTrue();
        vm.IsUnhealthy.Should().BeTrue();
        vm.DisplayStateColor.Should().Be(Color.Warning);
        vm.TopicStatus!.LastErrorMessage.Should().Be("recovery scheduled in 00:00:05");
        vm.TopicStatus.LastErrorException.Should().Contain("stream closed");
    }

    [Fact]
    public async Task GetAllSubscriptionsAsync_WhenTopicIsHealthy_ShouldKeepRegistrationDisplay()
    {
        await using var host = CreateHost();
        var monitor = host.Services.GetRequiredService<EventBusMonitorService>();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var store = host.Services.GetRequiredService<ITopicSubscriptionStatusStore>();

        await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.shipped",
            HandlerFactory = new UnusedHandlerFactory(),
            Scope = EventSubscriptionScope.Distributed
        }, TestContext.Current.CancellationToken);

        store.ReportState(
            null, "orders.shipped", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Healthy);

        var result = await monitor.GetAllSubscriptionsAsync();

        var subscriptions = result.Data;
        result.IsFailure().Should().BeFalse();
        var vm = subscriptions!.Single(v => v.TopicName == "orders.shipped");
        vm.TopicRuntimeState.Should().Be(TopicSubscriptionRuntimeState.Healthy);
        vm.IsRuntimeStateDisplayed.Should().BeFalse();
        vm.IsUnhealthy.Should().BeFalse();
        vm.DisplayStateColor.Should().Be(Color.Success);
    }

    [Fact]
    public async Task GetAllSubscriptionsAsync_ForLocalSubscriptions_ShouldHaveNoRuntimeStatus()
    {
        await using var host = CreateHost();
        var monitor = host.Services.GetRequiredService<EventBusMonitorService>();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();

        await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.shipped",
            HandlerFactory = new UnusedHandlerFactory(),
            Scope = EventSubscriptionScope.Local
        }, TestContext.Current.CancellationToken);

        var result = await monitor.GetAllSubscriptionsAsync();

        var subscriptions = result.Data;
        result.IsFailure().Should().BeFalse();
        var vm = subscriptions!.Single(v => v.TopicName == "orders.shipped");
        vm.TopicStatus.Should().BeNull();
        vm.TopicRuntimeState.Should().BeNull();
        vm.IsRuntimeStateDisplayed.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatisticsAsync_WhenTopicIsUnhealthy_ShouldCountUnhealthySubscriptionsAndTopics()
    {
        await using var host = CreateHost();
        var monitor = host.Services.GetRequiredService<EventBusMonitorService>();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var store = host.Services.GetRequiredService<ITopicSubscriptionStatusStore>();

        await registry.SubscribeAsync(new EventSubscriptionDescriptor
        {
            EventType = typeof(SampleEvent),
            TopicName = "orders.shipped",
            HandlerFactory = new UnusedHandlerFactory(),
            Scope = EventSubscriptionScope.Distributed
        }, TestContext.Current.CancellationToken);

        store.ReportState(
            null, "orders.shipped", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Failed,
            "supervisor terminated", new InvalidOperationException("boom"));

        var result = await monitor.GetStatisticsAsync();

        var statistics = result.Data;
        result.IsFailure().Should().BeFalse();
        statistics!.ActiveSubscriptions.Should().Be(1);
        statistics.UnhealthySubscriptions.Should().Be(1);
        statistics.UnhealthyTopics.Should().Be(1);
    }

    [Fact]
    public async Task GetAllTopicStatuses_ShouldReturnStoreSnapshots()
    {
        await using var host = CreateHost();
        var monitor = host.Services.GetRequiredService<EventBusMonitorService>();
        var store = host.Services.GetRequiredService<ITopicSubscriptionStatusStore>();

        store.ReportState(
            null, "orders", EventBusProviderKind.Dapr, TopicSubscriptionRuntimeState.Healthy);

        var result = monitor.GetAllTopicStatuses();

        var statuses = result.Data;
        result.IsFailure().Should().BeFalse();
        statuses.Should().ContainSingle()
            .Which.TopicName.Should().Be("orders");
    }

    private static WebApplication CreateHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddEventBus().UseNoOpDistributedEventBus();
            monica.AddModule<EventBusUIConsumerModule, EventBusUIConsumerModuleOption>();
        });

        var app = builder.Build();
        _ = app.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll().Any();
        return app;
    }

    private sealed record SampleEvent(string Value);

    /// <summary>
    /// The monitor never executes handlers; the factory only needs to exist for registration.
    /// </summary>
    private sealed class UnusedHandlerFactory : IEventHandlerFactory
    {
        public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync()
            => throw new NotSupportedException("Not used by monitor queries.");

        public Type? GetHandlerType() => null;
    }
}
