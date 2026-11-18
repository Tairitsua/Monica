using System.Text;
using AwesomeAssertions;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Monica.Kafka;
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
            _ => throw new Xunit.Sdk.XunitException("A successful delivery must not be rejected."),
            CreatePolicy(), TestContext.Current.CancellationToken);

        committed.Should().BeFalse();
        handled.SetResult();
        (await pending).Should().BeTrue();
        committed.Should().BeTrue();
        rewound.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task CommitAfterHandlingAsync_WhenHandlerFails_ShouldExhaustBudgetThenCommit(int retries, int expectedAttempts)
    {
        var attempts = 0;
        var committed = false;
        var failure = new InvalidOperationException("Handler failed.");
        Exception? rejected = null;

        var processed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () =>
            {
                attempts++;
                return Task.FromException(failure);
            },
            () => committed = true,
            () => Assert.Fail("Exhausted processing must not rewind."),
            exception => { rejected = exception; return Task.CompletedTask; },
            CreatePolicy(retries), TestContext.Current.CancellationToken);

        Assert.False(processed);
        Assert.True(committed);
        Assert.Same(failure, rejected);
        Assert.Equal(expectedAttempts, attempts);
    }

    public static TheoryData<Exception> ProcessingFailures => new()
    {
        new BusinessException("Required matching time is missing."),
        new EventMessageDeserializationException("Invalid event body."),
        new InvalidOperationException("Unavailable."),
        new TimeoutException("Timed out."),
        new OperationCanceledException("Handler canceled independently.")
    };

    [Theory]
    [MemberData(nameof(ProcessingFailures))]
    public async Task CommitAfterHandlingAsync_WhenRetriesAreExhausted_ShouldLogCommitAndContinue(Exception failure)
    {
        var operations = new List<string>();
        Exception? rejected = null;

        var processed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => Task.FromException(failure),
            () => operations.Add("commit"),
            () => operations.Add("rewind"),
            exception =>
            {
                rejected = exception;
                operations.Add("reject");
                return Task.CompletedTask;
            },
            CreatePolicy(), TestContext.Current.CancellationToken);
        var nextProcessed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => Task.CompletedTask,
            () => operations.Add("next-commit"),
            () => operations.Add("next-rewind"),
            _ => throw new Xunit.Sdk.XunitException("The next delivery must not be rejected."),
            CreatePolicy(), TestContext.Current.CancellationToken);

        processed.Should().BeFalse();
        nextProcessed.Should().BeTrue();
        rejected.Should().BeSameAs(failure);
        operations.Should().Equal("reject", "commit", "next-commit");
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenRetrySucceeds_ShouldAcknowledgeWithoutRejection()
    {
        var attempts = 0;
        var committed = false;
        var processed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => ++attempts == 1 ? Task.FromException(new TimeoutException()) : Task.CompletedTask,
            () => committed = true,
            () => Assert.Fail("Successful retry must not rewind."),
            _ => throw new Xunit.Sdk.XunitException("Successful retry must not reject."),
            CreatePolicy(), TestContext.Current.CancellationToken);

        Assert.True(processed);
        Assert.True(committed);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAfterHandlingAsync_WhenCommitFails_ShouldRewindEvenAfterRejection(bool rejectHandler)
    {
        var rewound = false;
        var rejectionCount = 0;
        // Even a terminal-looking exception from commit is a failed acknowledgement, not a handler rejection.
        var commitFailure = new BusinessException("Commit failed.");

        var thrown = await Assert.ThrowsAsync<BusinessException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => rejectHandler ? Task.FromException(new BusinessException("Rejected.")) : Task.CompletedTask,
                () => throw commitFailure,
                () => rewound = true,
                _ => { rejectionCount++; return Task.CompletedTask; },
                CreatePolicy(), TestContext.Current.CancellationToken));

        thrown.Should().BeSameAs(commitFailure);
        rewound.Should().BeTrue();
        rejectionCount.Should().Be(rejectHandler ? 1 : 0);
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenRewindFails_ShouldRequireConsumerToStop()
    {
        var handlerFailure = new InvalidOperationException("Commit failed.");
        var seekFailure = new InvalidOperationException("Seek failed.");

        var thrown = await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.CompletedTask,
                () => throw handlerFailure,
                () => throw seekFailure,
                _ => throw new Xunit.Sdk.XunitException("An unexpected failure must remain retryable."),
                CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.IsType<AggregateException>(thrown.InnerException).InnerExceptions
            .Should().Equal(handlerFailure, seekFailure);
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
                _ => throw new Xunit.Sdk.XunitException("Cancellation must not reject delivery."),
                CreatePolicy(), cancellation.Token));

        committed.Should().BeFalse();
        rewound.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenCancelledDuringRejection_ShouldNotCommitOrSeek()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.FromException(new BusinessException("Rejected.")),
                () => Assert.Fail("A canceled delivery must not be acknowledged."),
                () => Assert.Fail("A stopped consumer must not seek."),
                _ => cancellation.CancelAsync(),
                CreatePolicy(), cancellation.Token));
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenOwnershipIsLostAfterRejection_ShouldRestartWithoutSeeking()
    {
        var failure = new KafkaException(new Error(ErrorCode.Local_AssignmentLost));
        var rejected = false;

        var restart = await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.FromException(new BusinessException("Rejected.")),
                () => throw failure,
                () => Assert.Fail("A transferred partition must not be sought."),
                _ => { rejected = true; return Task.CompletedTask; },
                CreatePolicy(), TestContext.Current.CancellationToken));

        rejected.Should().BeTrue();
        restart.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenNativeBodyIsNull_ShouldRejectAndAcknowledge()
    {
        var record = CreateRecord();
        record.Message.Value = null!;
        var committed = false;
        Exception? rejection = null;

        var processed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () =>
            {
                KafkaEventBusSubscriptionHostedService.CreateReceivedMessage(record, null);
                return Task.CompletedTask;
            },
            () => committed = true,
            () => Assert.Fail("A permanent body rejection must not rewind."),
            failure => { rejection = failure; return Task.CompletedTask; },
            CreatePolicy(), TestContext.Current.CancellationToken);

        processed.Should().BeFalse();
        committed.Should().BeTrue();
        rejection.Should().BeOfType<EventMessageDeserializationException>();
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
                _ => throw new Xunit.Sdk.XunitException("This failure must remain retryable."),
                CreatePolicy(), TestContext.Current.CancellationToken));

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
                _ => throw new Xunit.Sdk.XunitException("This failure must remain retryable."),
                CreatePolicy(), TestContext.Current.CancellationToken));

        thrown.Should().BeSameAs(failure);
        rewound.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenRewindFails_ShouldRestartBeforeAnotherAcknowledgment()
    {
        var committed = false;
        var failure = new InvalidOperationException("Offset commit failed.");
        var seekFailure = new KafkaException(new Error(ErrorCode.Local_State));

        var restart = await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.CompletedTask,
                () => throw failure,
                () => throw seekFailure,
                _ => throw new Xunit.Sdk.XunitException("This failure must remain retryable."),
                CreatePolicy(), TestContext.Current.CancellationToken));

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
        message.Body.ToArray().Should().Equal(record.Message.Value);
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

        read.Should().Throw<KafkaEventBusSubscriptionHostedService.KafkaNativeEnvelopeException>().WithMessage($"*'{missingHeader}'*");
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenOldCloudEventRemains_ShouldUseTheSameFiniteBudget()
    {
        var record = CreateRecord();
        record.Message.Headers = new Headers();
        record.Message.Value = "{}"u8.ToArray();
        var attempts = 0;
        var committed = false;
        Exception? rejection = null;

        var processed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () =>
            {
                attempts++;
                KafkaEventBusSubscriptionHostedService.CreateReceivedMessage(record, null);
                return Task.CompletedTask;
            },
            () => committed = true,
            () => Assert.Fail("Exhausted processing must not rewind."),
            exception => { rejection = exception; return Task.CompletedTask; },
            CreatePolicy(), TestContext.Current.CancellationToken);

        Assert.False(processed);
        Assert.True(committed);
        Assert.Equal(2, attempts);
        Assert.IsType<KafkaEventBusSubscriptionHostedService.KafkaNativeEnvelopeException>(rejection);
    }

    [Fact]
    public void ResolveFailurePolicy_SelectsExactGroupAndPreservesSingleAndBatchDefaults()
    {
        var options = new ModuleEventBusKafkaOption
        {
            SubscriptionFailurePolicies = [new("changes", KafkaFailureAction.DeadLetter, "audit", "secondary", "changes-dlq")]
        };
        options.ValidateSubscriptionFailurePolicies();
        Assert.Equal("changes-dlq", options.ResolveFailurePolicy("changes", "audit", "secondary", true).DeadLetterTopic);
        Assert.Equal(KafkaFailureAction.Block, options.ResolveFailurePolicy("changes", "audit", "another", true).Action);
        Assert.Equal(KafkaFailureAction.LogAndSkip, options.ResolveFailurePolicy("changes", null, "secondary", false).Action);
        options.SubscriptionFailurePolicies.Add(new("changes", KafkaFailureAction.LogAndSkip, "audit", "secondary"));
        Assert.Throws<ArgumentException>(options.ValidateSubscriptionFailurePolicies);
        options.SubscriptionFailurePolicies = [new("changes", KafkaFailureAction.DeadLetter, DeadLetterTopic: "changes")];
        Assert.Throws<ArgumentException>(options.ValidateSubscriptionFailurePolicies);
    }

    [Fact]
    public async Task CommitAfterHandlingAsync_WhenHandoffFails_ShouldRetainOffsetUntilConsumerRestarts()
    {
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var delivery = KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => Task.FromException(new InvalidDataException("Bad record.")), () => commits++,
            () => Assert.Fail("The failed consumer is replaced."), _ => confirmation.Task, CreatePolicy(0),
            TestContext.Current.CancellationToken);
        Assert.False(delivery.IsCompleted);
        Assert.Equal(0, commits);
        confirmation.SetException(new KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException(
            "Failed to forward", new IOException()));
        await Assert.ThrowsAsync<KafkaEventBusSubscriptionHostedService.KafkaConsumerRestartRequiredException>(() => delivery);
        Assert.Equal(0, commits);
    }

    private static EventMessageRetryPolicy CreatePolicy(int retries = 1)
        => new(Options.Create(new ModuleEventBusOption { MessageRetryCount = retries }));

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

    private static ConsumeResult<byte[], byte[]> CreateRecord()
        => new()
        {
            Topic = "orders",
            Partition = 0,
            Offset = 17,
            Message = new Message<byte[], byte[]>
            {
                Key = "order-17"u8.ToArray(),
                Value = "{\"orderId\":17}"u8.ToArray(),
                Headers = new Headers
                {
                    { "monica-message-id", Encoding.UTF8.GetBytes("stable-id") },
                    { "monica-source", Encoding.UTF8.GetBytes("stable-source") },
                    { "monica-event-name", Encoding.UTF8.GetBytes("orders.created") }
                }
            }
        };
}
