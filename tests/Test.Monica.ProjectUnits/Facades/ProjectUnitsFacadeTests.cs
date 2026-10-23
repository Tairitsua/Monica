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
using Monica.ProjectUnits.Annotations;
using Monica.ProjectUnits.Facades;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ProjectUnits.Facades;

public sealed class ProjectUnitsFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRequestFilterState_WhenPathsAreEmpty_ShouldDistinguishServiceAvailability(bool enabled)
    {
        await using var application = await new FacadeScenarioFactory(enabled)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<ProjectUnitsFacade>().GetRequestFilterStateAsync();

        result.Status.Should().Be(ResStatus.Ok);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        result.Data!.IsAvailable.Should().Be(enabled);
        result.Data.DisabledPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRequestFilterState_WhenPathsChange_ShouldRetainDetachedExactSnapshotAndHostIsolation()
    {
        await using var first = await new FacadeScenarioFactory(true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var second = await new FacadeScenarioFactory(true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var firstScope = first.CreateScope(TestContext.Current.CancellationToken);
        await using var secondScope = second.CreateScope(TestContext.Current.CancellationToken);
        var facade = firstScope.Resolve<ProjectUnitsFacade>();

        var disabled = await facade.ManageRequestFilterAsync(["/probe", "/Probe", "/probe"], true);
        disabled.Status.Should().Be(ResStatus.Ok);
        disabled.Data.Should().BeEquivalentTo("/probe", "/Probe");
        var captured = await facade.GetRequestFilterStateAsync();
        captured.Status.Should().Be(ResStatus.Ok);
        captured.Data!.DisabledPaths.Should().BeEquivalentTo("/probe", "/Probe");

        var enabled = await facade.ManageRequestFilterAsync(["/probe"], false);
        enabled.Status.Should().Be(ResStatus.Ok);
        enabled.Data.Should().Equal("/Probe");
        captured.Data.DisabledPaths.Should().BeEquivalentTo("/probe", "/Probe");
        var other = await secondScope.Resolve<ProjectUnitsFacade>().GetRequestFilterStateAsync();
        other.Status.Should().Be(ResStatus.Ok);
        other.Data!.IsAvailable.Should().BeTrue();
        other.Data.DisabledPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishDomainEvent_WhenDisplayTitleDiffers_ShouldUseDiscoveredExactOperationKey()
    {
        await using var application = await new FacadeScenarioFactory(false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ProjectUnitsFacade>();
        var descriptors = await facade.GetDomainEventsAsync();
        descriptors.Status.Should().Be(ResStatus.Ok);
        var descriptor = descriptors.Data!.Single(item => item.Info.Key == typeof(EventFriendlyDispatch).FullName);
        descriptor.Info.Title.Should().Be("Friendly dispatch label");
        descriptor.EventKey.Should().Be(typeof(EventFriendlyDispatch).FullName);

        var result = await facade.PublishDomainEventAsync(descriptor.EventKey, JsonNode.Parse("{\"Value\":\"exact\"}")!);

        result.Status.Should().Be(ResStatus.Ok);
        result.Message.Should().Contain(descriptor.EventKey);
        var delivery = application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries.Single();
        delivery.Type.Should().Be(typeof(EventFriendlyDispatch));
        delivery.Value.Should().BeOfType<EventFriendlyDispatch>().Which.Value.Should().Be("exact");
    }

    [Fact]
    public async Task PublishDomainEvent_WhenShortNamesCollide_ShouldDispatchEachFullIdentityExactly()
    {
        await using var application = await new FacadeScenarioFactory(false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ProjectUnitsFacade>();

        foreach (var eventType in new[] { typeof(FirstContext.EventCollision), typeof(SecondContext.EventCollision) })
        {
            var result = await facade.PublishDomainEventAsync(eventType.FullName!, JsonNode.Parse("{}")!);
            result.Status.Should().Be(ResStatus.Ok);
        }

        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries
            .Select(item => item.Type).Should().Equal(typeof(FirstContext.EventCollision), typeof(SecondContext.EventCollision));
    }

    [Fact]
    public async Task PublishDomainEvent_WhenUsingLegacyShortKey_ShouldRetainPublication()
    {
        await using var application = await new FacadeScenarioFactory(false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<ProjectUnitsFacade>()
            .PublishDomainEventAsync(nameof(EventFriendlyDispatch), JsonNode.Parse("{}")!);

        result.Status.Should().Be(ResStatus.Ok);
        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries
            .Should().ContainSingle(item => item.Type == typeof(EventFriendlyDispatch));
    }

    [Fact]
    public async Task PublishDomainEvent_WhenShortKeyIsAmbiguous_ShouldRejectWithoutDispatch()
    {
        await using var application = await new FacadeScenarioFactory(false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<ProjectUnitsFacade>()
            .PublishDomainEventAsync(nameof(FirstContext.EventCollision), JsonNode.Parse("{}")!);

        result.IsOk().Should().BeFalse();
        result.Message.Should().Contain("short name is ambiguous").And.Contain("full CLR event key");
        application.Services.GetRequiredService<RecordingDistributedProvider>().Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDomainEvents_WhenRecordRequiresConstructorArguments_ShouldRetainDescriptorWithoutSample()
    {
        await using var application = await new FacadeScenarioFactory(false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<ProjectUnitsFacade>().GetDomainEventsAsync();

        result.Status.Should().Be(ResStatus.Ok);
        result.Data!.Single(item => item.EventKey == typeof(EventParameterized).FullName).Structure.Should().BeNull();
        result.Data!.Single(item => item.EventKey == typeof(EventFriendlyDispatch).FullName).Structure.Should().NotBeNull();
    }

    public sealed record EventParameterized(string Value) : IDomainEvent;

    [ProjectUnitMetadata("Friendly dispatch label")]
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

    private sealed class FacadeScenarioFactory(bool enableFilter)
        : MonicaTestApplicationFactory<EventFriendlyDispatch>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            builder.AddEventBus(options => options.DisableAutoDiscovery = true)
                .UseDistributedEventBus<RecordingDistributedProvider>();
            builder.AddProjectUnits(options => options.EnableRequestFilter = enableFilter);
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
