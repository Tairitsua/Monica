using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Providers.NoOp;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Modules;

public sealed class OutboxGatewayTests
{
    [Fact]
    public async Task LocalGateway_StagesAllPublishOverloadsWithIndependentSnapshots()
    {
        using var host = BuildHost(out var sink);
        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();
        var bus = firstScope.ServiceProvider.GetRequiredService<ILocalEventBus>();
        var otherBus = secondScope.ServiceProvider.GetRequiredService<ILocalEventBus>();

        bus.Should().NotBeSameAs(otherBus);
        firstScope.ServiceProvider.GetRequiredService<IEventReceiveDispatcher>()
            .Should().BeSameAs(secondScope.ServiceProvider.GetRequiredService<IEventReceiveDispatcher>());

        var first = new DurableEvent { Value = "before" };
        await bus.PublishAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        first.Value = "after";
        await bus.PublishAsync(typeof(DurableEvent), new DurableEvent { Value = "runtime" },
            cancellationToken: TestContext.Current.CancellationToken);
        await bus.BulkPublishAsync([new DurableEvent { Value = "generic bulk" }],
            cancellationToken: TestContext.Current.CancellationToken);
        await bus.BulkPublishAsync(typeof(DurableEvent),
            [new DurableEvent { Value = "runtime bulk" }],
            cancellationToken: TestContext.Current.CancellationToken);

        sink.Messages.Should().HaveCount(4);
        sink.Messages.Select(message => message.Metadata.MessageId).Distinct().Should().HaveCount(4);
        sink.Messages.Should().AllSatisfy(message =>
        {
            message.Scope.Should().Be(EventSubscriptionScope.Local);
            message.Metadata.Source.Should().Be("urn:monica:Test.Monica.EventBus");
            message.Metadata.EventName.Should().Be("test.durable.v1");
            message.Metadata.TopicName.Should().Be("test.durable.v1");
        });
        JsonDocument.Parse(sink.Messages[0].Body).RootElement.GetProperty("value").GetString()
            .Should().Be("before");
    }

    [Fact]
    public async Task OrdinaryBaseTypedEvents_KeepSupportedLocalDispatch()
    {
        using var host = BuildHost(out var sink);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<ILocalEventBus>();
        var received = new List<BaseEvent>();
        await using var subscription = await bus.SubscribeAsync<BaseEvent>((message, _) =>
        {
            received.Add(message);
            return Task.CompletedTask;
        });

        await bus.PublishAsync<BaseEvent>(new OrdinaryDerivedEvent(),
            cancellationToken: TestContext.Current.CancellationToken);
        await bus.BulkPublishAsync<BaseEvent>([new OrdinaryDerivedEvent()],
            cancellationToken: TestContext.Current.CancellationToken);

        received.Should().HaveCount(2);
        sink.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Gateway_RejectsBaseTypedAndMixedBatchesBeforeStaging()
    {
        using var host = BuildHost(out var sink);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<ILocalEventBus>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            bus.PublishAsync<BaseEvent>(new DurableEvent(), cancellationToken: TestContext.Current.CancellationToken));

        sink.Messages.Should().BeEmpty();

        using var batchHost = BuildHost(out var batchSink);
        using var batchScope = batchHost.Services.CreateScope();
        var batchBus = batchScope.ServiceProvider.GetRequiredService<ILocalEventBus>();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            batchBus.BulkPublishAsync(typeof(DurableEvent),
                [new DurableEvent(), new BaseEvent()],
                cancellationToken: TestContext.Current.CancellationToken));

        batchSink.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task NoOpDistributedGateway_RejectsDurablePublication()
    {
        using var host = BuildHost(out var sink);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDistributedEventBus>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.PublishAsync(new DurableEvent(), cancellationToken: TestContext.Current.CancellationToken));
        sink.Messages.Should().BeEmpty();
        host.Services.GetRequiredService<NoOpDistributedEventBus>().Should().NotBeNull();
    }

    [Fact]
    public async Task Gateway_WhenSerializationFailureIsCaught_ShouldAllowAnotherPublication()
    {
        using var host = BuildHost(out var sink);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<ILocalEventBus>();

        await Assert.ThrowsAnyAsync<Exception>(() => bus.PublishAsync(new ThrowingEvent(),
            cancellationToken: TestContext.Current.CancellationToken));

        sink.Messages.Should().BeEmpty();

        await bus.PublishAsync(new DurableEvent(), cancellationToken: TestContext.Current.CancellationToken);
        sink.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task KeyedLocalGateway_CapturesKeyAndDispatcherRejectsMissingSubscriber()
    {
        using var host = BuildHost(out var sink);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredKeyedService<ILocalEventBus>("audit");

        await bus.PublishAsync(new DurableEvent { Value = "keyed" }, "audit-topic",
            TestContext.Current.CancellationToken);

        var message = sink.Messages.Should().ContainSingle().Which;
        message.Metadata.ServiceKey.Should().Be("audit");
        message.Metadata.TopicName.Should().Be("audit-topic");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IEventReceiveDispatcher>()
                .DispatchAsync(message, TestContext.Current.CancellationToken));
    }

    private static IHost BuildHost(out CapturingSink sink)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Test.Monica.EventBus"
        });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddEventBus(options => options.DisableAutoDiscovery = true)
                .UseNoOpDistributedEventBus()
                .AddKeyedLocalEventBus("audit");
        });
        sink = new CapturingSink();
        builder.Services.AddSingleton(sink);
        builder.Services.AddScoped<ITransactionalEventSink>(sp => sp.GetRequiredService<CapturingSink>());
        return builder.Build();
    }

    private class BaseEvent;

    private sealed class OrdinaryDerivedEvent : BaseEvent;

    [Outbox]
    [EventName("test.durable.v1")]
    private sealed class DurableEvent : BaseEvent
    {
        public string? Value { get; set; }
    }

    [Outbox]
    [EventName("test.throwing.v1")]
    private sealed class ThrowingEvent
    {
        public string Value => throw new InvalidOperationException("Serialization failed.");
    }

    private sealed class CapturingSink : ITransactionalEventSink
    {
        public List<EventMessage> Messages { get; } = [];

        public ValueTask StageAsync(Func<IReadOnlyList<EventMessage>> prepare, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.AddRange(prepare());
            return ValueTask.CompletedTask;
        }
    }
}
