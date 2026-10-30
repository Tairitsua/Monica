using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.EventBus.Services.Support;
using Monica.Modules;
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
            _ => Assert.Fail("A successful delivery must not be rejected."),
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
            exception => rejected = exception,
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
            },
            CreatePolicy(), TestContext.Current.CancellationToken);
        var nextProcessed = await KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
            () => Task.CompletedTask,
            () => operations.Add("next-commit"),
            () => operations.Add("next-rewind"),
            _ => Assert.Fail("The next delivery must not be rejected."),
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
            _ => Assert.Fail("Successful retry must not reject."),
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
                _ => rejectionCount++,
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

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            KafkaEventBusSubscriptionHostedService.CommitAfterHandlingAsync(
                () => Task.CompletedTask,
                () => throw handlerFailure,
                () => throw seekFailure,
                _ => Assert.Fail("An unexpected failure must remain retryable."),
                CreatePolicy(), TestContext.Current.CancellationToken));

        thrown.GetType().Name.Should().Be("KafkaRewindFailedException");
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
                _ => Assert.Fail("Cancellation must not reject delivery."),
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
                _ => cancellation.Cancel(),
                CreatePolicy(), cancellation.Token));
    }

    private static EventMessageRetryPolicy CreatePolicy(int retries = 1)
        => new(Options.Create(new ModuleEventBusOption { MessageRetryCount = retries }));

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
        Func<KafkaEventBusSubscriptionHostedService.TopicConsumer, Task> consumeAsync)
    {
        return new KafkaEventBusSubscriptionHostedService.TopicConsumer(
            "orders",
            typeof(object),
            CancellationToken.None,
            consumeAsync);
    }
}
