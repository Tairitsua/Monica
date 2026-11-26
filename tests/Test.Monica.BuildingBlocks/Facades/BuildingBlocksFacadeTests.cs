using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Events;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Monica.ApplicationModel.Annotations;
using Monica.BuildingBlocks.Facades;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.BuildingBlocks.Facades;

public sealed class BuildingBlocksFacadeTests
{
    [Fact]
    public async Task PublishDomainEvent_WhenDisplayTitleDiffers_ShouldUseDiscoveredExactOperationKey()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<BuildingBlocksFacade>();
        var descriptors = await facade.GetDomainEventsAsync();
        descriptors.Status.Should().Be(ResultStatus.Ok);
        var descriptor = descriptors.Data!.Single(item => item.Info.Key == typeof(EventFriendlyDispatch).FullName);
        descriptor.Info.Title.Should().Be("Friendly dispatch label");
        descriptor.EventKey.Should().Be(typeof(EventFriendlyDispatch).FullName);

        var result = await facade.PublishDomainEventAsync(descriptor.EventKey, JsonNode.Parse("{\"Value\":\"exact\"}")!);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().Contain(descriptor.EventKey);
        var delivery = application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries.Single();
        delivery.Type.Should().Be(typeof(EventFriendlyDispatch));
        delivery.Value.Should().BeOfType<EventFriendlyDispatch>().Which.Value.Should().Be("exact");
    }

    [Fact]
    public async Task PublishDomainEvent_WhenShortNamesCollide_ShouldDispatchEachFullIdentityExactly()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<BuildingBlocksFacade>();

        foreach (var eventType in new[] { typeof(FirstContext.EventCollision), typeof(SecondContext.EventCollision) })
        {
            var result = await facade.PublishDomainEventAsync(eventType.FullName!, JsonNode.Parse("{}")!);
            result.Status.Should().Be(ResultStatus.Ok);
        }

        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries
            .Select(item => item.Type).Should().Equal(typeof(FirstContext.EventCollision), typeof(SecondContext.EventCollision));
    }

    [Fact]
    public async Task PublishDomainEvent_WhenUsingLegacyShortKey_ShouldRetainPublication()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<BuildingBlocksFacade>()
            .PublishDomainEventAsync(nameof(EventFriendlyDispatch), JsonNode.Parse("{}")!);

        result.Status.Should().Be(ResultStatus.Ok);
        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries
            .Should().ContainSingle(item => item.Type == typeof(EventFriendlyDispatch));
    }

    [Fact]
    public async Task PublishDomainEvent_WhenShortKeyIsAmbiguous_ShouldRejectWithoutDispatch()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<BuildingBlocksFacade>()
            .PublishDomainEventAsync(nameof(FirstContext.EventCollision), JsonNode.Parse("{}")!);

        result.IsSuccess().Should().BeFalse();
        result.Message.Should().Contain("short name is ambiguous").And.Contain("full CLR event key");
        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDomainEvents_WhenRecordRequiresConstructorArguments_ShouldRetainDescriptorWithoutSample()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<BuildingBlocksFacade>().GetDomainEventsAsync();

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data!.Single(item => item.EventKey == typeof(EventParameterized).FullName).Structure.Should().BeNull();
        result.Data!.Single(item => item.EventKey == typeof(EventFriendlyDispatch).FullName).Structure.Should().NotBeNull();
    }

    public sealed record EventParameterized(string Value) : IDomainEvent;

    [BuildingBlockMetadata("Friendly dispatch label")]
    public sealed class EventFriendlyDispatch : IDomainEvent
    {
        public string Value { get; set; } = string.Empty;
    }

    public static class FirstContext
    {
        public sealed class EventCollision : IDomainEvent;
    }

    public static class SecondContext
    {
        public sealed class EventCollision : IDomainEvent;
    }

    private sealed class FacadeScenarioFactory
        : MonicaTestApplicationFactory<EventFriendlyDispatch>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            builder.AddEventBus(options => options.DisableAutoDiscovery = true)
                .UseDistributedEventBus<RecordingDistributedProvider>();
            builder.AddBuildingBlocks();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            // This scenario retains its explicitly selected provider behind the production gateway.
            // Default test seams would replace it with the shared recording provider.
        }
    }

    public sealed class RecordingDistributedProvider(
        IServiceScopeFactory scopeFactory,
        IEventHandlerInvoker invoker,
        IEventSubscriptionRegistry subscriptions,
        ILoggerFactory loggerFactory)
        : DistributedEventBusBase(scopeFactory, invoker, subscriptions, loggerFactory), IEventTransport
    {
        public ConcurrentQueue<(Type Type, object Value)> Deliveries { get; } = new();

        public override Task PublishAsync(Type eventType, object eventData, string? topicName = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deliveries.Enqueue((eventType, eventData));
            return Task.CompletedTask;
        }

        public override async Task BulkPublishAsync(Type eventType, IEnumerable<object> eventDataList,
            string? topicName = null, CancellationToken cancellationToken = default)
        {
            foreach (var eventData in eventDataList)
            {
                await PublishAsync(eventType, eventData, topicName, cancellationToken);
            }
        }

        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("This facade scenario does not publish durable transport messages.");
        }
    }
}
