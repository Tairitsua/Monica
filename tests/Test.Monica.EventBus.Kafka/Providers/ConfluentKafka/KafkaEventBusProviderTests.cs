using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Providers.ConfluentKafka;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.EventBus.Kafka.Providers.ConfluentKafka;

public sealed class KafkaEventBusProviderTests
{
    [Fact]
    public async Task SendAsync_WhenNativeAcknowledgmentIsPending_ShouldKeepPublicationPending()
    {
        var producer = Substitute.For<IProducer<string, string>>();
        var acknowledgment = new TaskCompletionSource<DeliveryResult<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(acknowledgment.Task);
        using var provider = CreateProvider(_ => producer);

        var delivery = provider.SendAsync(PreparedMessage(), TestContext.Current.CancellationToken);

        Assert.False(delivery.IsCompleted);
        acknowledgment.SetResult(new DeliveryResult<string, string>());
        await delivery;
    }

    [Fact]
    public async Task SendAsync_WhenRetryingAfterAmbiguousFailure_ShouldRetainPreparedBodyKeyAndIdentity()
    {
        var producer = Substitute.For<IProducer<string, string>>();
        var attempts = new List<(string Topic, Message<string, string> Message)>();
        var failure = new ProduceException<string, string>(
            new Error(ErrorCode.Local_MsgTimedOut, "Acknowledgment was lost."),
            new DeliveryResult<string, string>());
        producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attempts.Add((call.ArgAt<string>(0), call.ArgAt<Message<string, string>>(1)));
                return attempts.Count == 1
                    ? Task.FromException<DeliveryResult<string, string>>(failure)
                    : Task.FromResult(new DeliveryResult<string, string>());
            });
        using var provider = CreateProvider(_ => producer);
        var prepared = PreparedMessage();

        var observed = await Assert.ThrowsAsync<ProduceException<string, string>>(() =>
            provider.SendAsync(prepared, TestContext.Current.CancellationToken));
        await provider.SendAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Same(failure, observed);
        Assert.Equal(2, attempts.Count);
        foreach (var attempt in attempts)
        {
            Assert.Equal(prepared.Metadata.TopicName, attempt.Topic);
            Assert.Equal(prepared.Metadata.TransportKey, attempt.Message.Key);
            Assert.Equal(Encoding.UTF8.GetString(prepared.Body.Span), attempt.Message.Value);
            Assert.Equal(prepared.Metadata.MessageId, Header(attempt.Message, "monica-message-id"));
            Assert.Equal(prepared.Metadata.Source, Header(attempt.Message, "monica-source"));
            Assert.Equal(prepared.Metadata.EventName, Header(attempt.Message, "monica-event-name"));
            Assert.Equal(prepared.Metadata.TraceParent, Header(attempt.Message, "traceparent"));
            Assert.Equal(prepared.Metadata.TraceState, Header(attempt.Message, "tracestate"));
        }
    }

    [Fact]
    public async Task SendAsync_WhenCanceledBeforeQueuing_ShouldAvoidCreatingProducer()
    {
        var created = 0;
        using var provider = CreateProvider(_ =>
        {
            created++;
            return Substitute.For<IProducer<string, string>>();
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.SendAsync(PreparedMessage(), cancellation.Token));

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task SendAsync_WhenNativeWaitIsCanceled_ShouldPropagateCancellation()
    {
        var producer = Substitute.For<IProducer<string, string>>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgment = new TaskCompletionSource<DeliveryResult<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                started.TrySetResult();
                return acknowledgment.Task.WaitAsync(call.ArgAt<CancellationToken>(2));
            });
        using var provider = CreateProvider(_ => producer);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var delivery = provider.SendAsync(PreparedMessage(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
    }

    [Theory]
    [InlineData(null, "urn:monica:tests")]
    [InlineData("message-id", null)]
    public async Task SendAsync_WhenDurableIdentityIsMissing_ShouldRejectBeforeCreatingProducer(string? messageId, string? source)
    {
        var created = 0;
        using var provider = CreateProvider(_ =>
        {
            created++;
            return Substitute.For<IProducer<string, string>>();
        });
        var prepared = PreparedMessage();
        prepared = prepared with { Metadata = prepared.Metadata with { MessageId = messageId, Source = source } };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(prepared, TestContext.Current.CancellationToken));

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Dispose_WhenProducerWasUsed_ShouldFlushOnceAndRejectFurtherSends()
    {
        var producer = Substitute.For<IProducer<string, string>>();
        producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeliveryResult<string, string>()));
        var options = new ModuleEventBusKafkaOption { ProducerFlushTimeout = TimeSpan.FromSeconds(7) };
        using var provider = CreateProvider(_ => producer, options);
        await provider.SendAsync(PreparedMessage(), TestContext.Current.CancellationToken);

        provider.Dispose();
        provider.Dispose();

        producer.Received(1).Flush(TimeSpan.FromSeconds(7));
        producer.Received(1).Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            provider.SendAsync(PreparedMessage(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dispose_WhenProducerWasNeverUsed_ShouldAvoidCreatingNativeHandle()
    {
        var created = 0;
        using var provider = CreateProvider(_ =>
        {
            created++;
            return Substitute.For<IProducer<string, string>>();
        });

        provider.Dispose();

        Assert.Equal(0, created);
    }

    private static KafkaEventBusProvider CreateProvider(Func<ProducerConfig, IProducer<string, string>> factory,
        ModuleEventBusKafkaOption? options = null)
    {
        var cluster = Substitute.For<IKafkaClusterConfigProvider>();
        cluster.GetDirectEventBusCluster().Returns(new KafkaClusterConfig
        {
            ClusterId = "tests",
            BootstrapServers = "broker.example:9092"
        });
        return new KafkaEventBusProvider(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IEventHandlerInvoker>(),
            Substitute.For<IEventSubscriptionRegistry>(),
            cluster,
            Substitute.For<IEventMessageFactory>(),
            Options.Create(options ?? new ModuleEventBusKafkaOption()),
            NullLoggerFactory.Instance,
            factory);
    }

    private static EventMessage PreparedMessage() => new(
        new EventDeliveryMetadata("1c2c2a1dd6854cedae3bf321d362c73b", "urn:monica:tests", "flight.changed.v1",
            "flight.changed", null, "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "tenant=tests")
        {
            TransportKey = "Fips.FlightChanged"
        },
        EventSubscriptionScope.Distributed,
        Encoding.UTF8.GetBytes("{\"value\":\"航班\",\"version\":1}"));

    private static string? Header(Message<string, string> message, string key)
    {
        var header = message.Headers.LastOrDefault(value => value.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
