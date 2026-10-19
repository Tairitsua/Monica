using System.Text;
using AwesomeAssertions;
using Confluent.Kafka;
using Monica.EventBus.Kafka.Providers.ConfluentKafka;
using Monica.EventBus.Models;
using Xunit;

namespace Test.Monica.EventBus.Kafka.Providers.ConfluentKafka;

public class KafkaEventBusSubscriptionHostedServiceTests
{
    [Fact]
    public async Task CommitAfterHandlingAsync_WaitsForHandlerBeforeAcknowledging()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = false;
        var rewound = false;

        var pending = KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => handled.Task,
            () => committed = true,
            () => rewound = true,
            TestContext.Current.CancellationToken);

        committed.Should().BeFalse();
        handled.SetResult();
        await pending;
        committed.Should().BeTrue();
        rewound.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenHandlerFails_RewindsWithoutAcknowledging()
    {
        var committed = false;
        var rewound = false;
        var failure = new InvalidOperationException("handler failed");

        Func<Task> operation = () => KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => Task.FromException(failure),
            () => committed = true,
            () => rewound = true,
            TestContext.Current.CancellationToken);

        (await Assert.ThrowsAsync<InvalidOperationException>(operation)).Should().BeSameAs(failure);
        committed.Should().BeFalse();
        rewound.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenCancelledAfterHandling_ShouldNotCommitOrSeek()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var committed = false;
        var rewound = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => cancellation.CancelAsync(),
                () => committed = true,
                () => rewound = true,
                cancellation.Token));

        committed.Should().BeFalse();
        rewound.Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCode.Local_State)]
    [InlineData(ErrorCode.Local_AssignmentLost)]
    [InlineData(ErrorCode.IllegalGeneration)]
    [InlineData(ErrorCode.UnknownMemberId)]
    [InlineData(ErrorCode.RebalanceInProgress)]
    public async Task CommitAfterHandlingAsync_WhenOwnershipIsLost_ShouldRestartWithoutSeeking(ErrorCode code)
    {
        var rewound = false;
        var failure = new KafkaException(new Error(code));

        var restart = await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.CompletedTask,
                () => throw failure,
                () => rewound = true,
                TestContext.Current.CancellationToken));

        restart.InnerException.Should().BeSameAs(failure);
        rewound.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenCommitFailsTransiently_ShouldRewindOriginalRecord()
    {
        var rewound = false;
        var failure = new KafkaException(new Error(ErrorCode.RequestTimedOut));

        var thrown = await Assert.ThrowsAsync<KafkaException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.CompletedTask,
                () => throw failure,
                () => rewound = true,
                TestContext.Current.CancellationToken));

        thrown.Should().BeSameAs(failure);
        rewound.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenRewindFails_ShouldRestartBeforeAnotherAcknowledgment()
    {
        var committed = false;
        var failure = new InvalidOperationException("business operation failed");
        var seekFailure = new KafkaException(new Error(ErrorCode.Local_State));

        var restart = await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.FromException(failure),
                () => committed = true,
                () => throw seekFailure,
                TestContext.Current.CancellationToken));

        committed.Should().BeFalse();
        restart.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().Equal(failure, seekFailure);
    }

    [Fact]
    public void CreateReceivedMessage_WhenNativeHeadersExist_ShouldPreserveDeliveryIdentityAndPayload()
    {
        var record = CreateRecord();
        record.Message.Headers.Add("traceparent", Encoding.UTF8.GetBytes("original-parent"));
        record.Message.Headers.Add("tracestate", Encoding.UTF8.GetBytes("original-state"));

        var message = KafkaEventBusSubscriptionHostedService.CreateReceivedMessage(record, "audit");

        message.Metadata.MessageId.Should().Be("stable-id");
        message.Metadata.Source.Should().Be("stable-source");
        message.Metadata.EventName.Should().Be("orders.created");
        message.Metadata.TopicName.Should().Be("orders");
        message.Metadata.ServiceKey.Should().Be("audit");
        message.Metadata.TraceParent.Should().Be("original-parent");
        message.Metadata.TraceState.Should().Be("original-state");
        message.Metadata.TransportKey.Should().Be("order-17");
        Encoding.UTF8.GetString(message.Body.Span).Should().Be(record.Message.Value);
    }

    [Theory]
    [InlineData("monica-message-id")]
    [InlineData("monica-source")]
    [InlineData("monica-event-name")]
    public void CreateReceivedMessage_WhenRequiredHeaderIsMissing_ShouldRejectBeforeDispatch(string missingHeader)
    {
        var record = CreateRecord();
        record.Message.Headers.Remove(missingHeader);

        Action read = () => KafkaEventBusSubscriptionHostedService.CreateReceivedMessage(record, null);

        read.Should().Throw<EventMessageDeserializationException>().WithMessage($"*'{missingHeader}'*");
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenOldCloudEventRemains_ShouldRewindWithoutAcknowledging()
    {
        var record = CreateRecord();
        record.Message.Headers = new Headers();
        record.Message.Value = "{\"specversion\":\"1.0\",\"id\":\"old\",\"source\":\"old-source\",\"data\":{}}";
        var committed = false;
        var rewound = false;

        await Assert.ThrowsAsync<EventMessageDeserializationException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () =>
                {
                    KafkaEventBusSubscriptionHostedService.CreateReceivedMessage(record, null);
                    return Task.CompletedTask;
                },
                () => committed = true,
                () => rewound = true,
                TestContext.Current.CancellationToken));

        committed.Should().BeFalse();
        rewound.Should().BeTrue();
    }

    [Fact]
    public async Task TopicConsumerRegistry_WhenSameTopicHasDifferentOwners_ShouldRetainIndependentConsumers()
    {
        var registry = new KafkaEventBusSubscriptionHostedService.TopicConsumerRegistry();
        var first = CreateConsumer(_ => Task.CompletedTask, "orders");
        var second = CreateConsumer(_ => Task.CompletedTask, "notifications");

        registry.TryAddAndStart(first).Should().BeTrue();
        registry.TryAddAndStart(second).Should().BeTrue();
        registry.Remove("orders", "orders").Should().BeSameAs(first);
        var remaining = registry.Drain();
        remaining.Should().ContainSingle().Which.Should().BeSameAs(second);

        await first.StopAsync();
        await second.StopAsync();
    }

    [Fact]
    public async Task TopicConsumerTryStart_WhenStopWasRequestedFirst_ShouldNotRunConsumer()
    {
        var executionCount = 0;
        var consumer = CreateConsumer(_ =>
        {
            Interlocked.Increment(ref executionCount);
            return Task.CompletedTask;
        });

        await consumer.StopAsync().WaitAsync(TestContext.Current.CancellationToken);
        var started = consumer.TryStart();

        started.Should().BeFalse();
        Volatile.Read(ref executionCount).Should().Be(0);
    }

    [Fact]
    public async Task TopicConsumerStopAsync_WhenConsumerIsRunning_ShouldCancelAwaitAndShareShutdown()
    {
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var consumer = CreateConsumer(async activeConsumer =>
        {
            using var registration = activeConsumer.CancellationToken.Register(
                () => cancellationObserved.TrySetResult());
            Interlocked.Increment(ref executionCount);
            consumerStarted.TrySetResult();
            await releaseConsumer.Task;
        });

        consumer.TryStart().Should().BeTrue();
        await consumerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopTask = consumer.StopAsync();
        var repeatedStopTask = consumer.StopAsync();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stoppedBeforeConsumerCompleted = stopTask.IsCompleted;

        releaseConsumer.TrySetResult();
        await stopTask.WaitAsync(TestContext.Current.CancellationToken);

        repeatedStopTask.Should().BeSameAs(stopTask);
        stoppedBeforeConsumerCompleted.Should().BeFalse();
        Volatile.Read(ref executionCount).Should().Be(1);
    }

    [Fact]
    public async Task TopicConsumerRegistryStopAccepting_WhenShutdownRacesWithCreation_ShouldOwnEveryConsumer()
    {
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new KafkaEventBusSubscriptionHostedService.TopicConsumerRegistry();
        var activeConsumer = CreateConsumer(async consumer =>
        {
            consumerStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, consumer.CancellationToken);
        });

        registry.TryAddAndStart(activeConsumer).Should().BeTrue();
        await consumerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        registry.StopAccepting();
        var drainedConsumers = registry.Drain();
        var lateConsumer = CreateConsumer(_ => Task.CompletedTask);
        registry.TryAddAndStart(lateConsumer).Should().BeFalse();

        await lateConsumer.StopAsync().WaitAsync(TestContext.Current.CancellationToken);
        foreach (var consumer in drainedConsumers)
        {
            await consumer.StopAsync().WaitAsync(TestContext.Current.CancellationToken);
        }

        drainedConsumers.Should().ContainSingle().Which.Should().BeSameAs(activeConsumer);
        registry.Drain().Should().BeEmpty();
    }

    private static KafkaEventBusSubscriptionHostedService.TopicConsumer CreateConsumer(
        Func<KafkaEventBusSubscriptionHostedService.TopicConsumer, Task> consumeAsync,
        string? workloadOwner = null)
    {
        return new KafkaEventBusSubscriptionHostedService.TopicConsumer(
            "orders",
            CancellationToken.None,
            consumeAsync,
            workloadOwner);
    }

    private static ConsumeResult<string, string> CreateRecord()
        => new()
        {
            Topic = "orders",
            Partition = 0,
            Offset = 17,
            Message = new Message<string, string>
            {
                Key = "order-17",
                Value = "{\"orderId\":17}",
                Headers = new Headers
                {
                    { "monica-message-id", Encoding.UTF8.GetBytes("stable-id") },
                    { "monica-source", Encoding.UTF8.GetBytes("stable-source") },
                    { "monica-event-name", Encoding.UTF8.GetBytes("orders.created") }
                }
            }
        };
}
