using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Providers.ConfluentKafka;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;
using static Monica.EventBus.Kafka.Providers.ConfluentKafka.KafkaEventBusSubscriptionHostedService;

namespace Test.Monica.EventBus.Kafka.Providers.ConfluentKafka;

public sealed class KafkaEventBatchReceiverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Collect_WhenCountReached_ShouldStopWithoutReadingAnotherRecord()
    {
        var consumer = Substitute.For<IConsumer<string, string>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(Record(1), Record(2));
        var batch = KafkaEventBatchReceiver.Collect(consumer, Record(0), new(maxMessages: 2), () => 1, Token);
        Assert.Equal([0L, 1L], batch.Records.Select(record => record.Offset.Value));
        Assert.Null(batch.Pending);
        consumer.Received(1).Consume(Arg.Any<TimeSpan>());
    }

    [Fact]
    public void Collect_WhenByteBudgetExceeded_ShouldRetainNextRecordAndProcessOversizedRecordAlone()
    {
        var first = Record(0);
        var second = Record(1);
        var consumer = Substitute.For<IConsumer<string, string>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(second);
        var size = KafkaEventBatchReceiver.GetSize(first);
        var batch = KafkaEventBatchReceiver.Collect(consumer, first, new(maxBytes: size + 1), () => 1, Token);
        Assert.Same(first, Assert.Single(batch.Records));
        Assert.Same(second, batch.Pending);
        consumer.ClearReceivedCalls();
        var oversized = KafkaEventBatchReceiver.Collect(consumer, second, new(maxBytes: 1), () => 1, Token);
        Assert.Same(second, Assert.Single(oversized.Records));
        consumer.DidNotReceive().Consume(Arg.Any<TimeSpan>());
    }

    [Fact]
    public void Collect_WhenPartitionsInterleave_ShouldGroupOrderedRecordsWithinOneBoundedWindow()
    {
        var consumer = Substitute.For<IConsumer<string, string>>();
        var other = Record(20, partition: 1);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(other, Record(1), Record(21, partition: 1));
        var batch = KafkaEventBatchReceiver.Collect(consumer, Record(0), new(maxMessages: 4), () => 1, Token);
        var groups = batch.PartitionGroups.ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal([0L, 1L], groups[0].Select(record => record.Offset.Value));
        Assert.Equal([20L, 21L], groups[1].Select(record => record.Offset.Value));
        Assert.Null(batch.Pending);
    }

    [Fact]
    public void Collect_WhenLowTraffic_ShouldWaitOnlyConfiguredWindowAndReturnSingleton()
    {
        var time = new CollectionClock();
        var consumer = Substitute.For<IConsumer<string, string>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(call =>
        {
            time.Advance(call.Arg<TimeSpan>());
            return null!;
        });
        var batch = KafkaEventBatchReceiver.Collect(consumer, Record(0), new(), () => 1, Token, time);
        Assert.Single(batch.Records);
        Assert.Equal(TimeSpan.FromMilliseconds(50).Ticks, time.GetTimestamp());
        consumer.Received(1).Consume(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Collect_WhenAssignmentChanges_ShouldDiscardUnacknowledgedBufferAndRestart()
    {
        var generation = 1;
        var consumer = Substitute.For<IConsumer<string, string>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => { generation++; return Record(1); });
        Assert.Throws<KafkaConsumerRestartRequiredException>(() =>
            KafkaEventBatchReceiver.Collect(consumer, Record(0), new(), () => generation, Token));
    }

    [Fact]
    public async Task DeliverAsync_WhenTransactionStillRunning_ShouldNotCommit()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = new List<long>();
        var delivery = KafkaEventBatchReceiver.DeliverAsync([Record(0), Record(1)], _ => completed.Task,
            (record, _) => commits.Add(record.Offset.Value), Policy(), Token);
        Assert.Empty(commits);
        completed.SetResult();
        await delivery;
        Assert.Equal([1L], commits);
    }

    [Fact]
    public async Task DeliverAsync_WhenPoisonRecordExhausts_ShouldCommitOnlySuccessfulPrefixAndStop()
    {
        var attempts = new List<long[]>();
        var commits = new List<long>();
        var failure = new InvalidOperationException("poison");
        var exception = await Assert.ThrowsAsync<KafkaEventBatchReceiver.KafkaBatchProcessingException>(() =>
            KafkaEventBatchReceiver.DeliverAsync([Record(0), Record(1), Record(2), Record(3)], records =>
            {
                var offsets = records.Select(record => record.Offset.Value).ToArray();
                attempts.Add(offsets);
                return offsets.Contains(2) ? Task.FromException(failure) : Task.CompletedTask;
            }, (record, _) => commits.Add(record.Offset.Value), Policy(2), Token));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal([1L], commits);
        Assert.Equal(6, attempts.Count);
        Assert.Equal(3, attempts.Count(attempt => attempt.SequenceEqual([2L])));
        Assert.DoesNotContain(attempts, attempt => attempt.SequenceEqual([3L]));
    }

    [Fact]
    public async Task DeliverAsync_WhenGroupFailsButSmallerGroupsSucceed_ShouldSplitInOrder()
    {
        var commits = new List<long>();
        var attempts = 0;
        await KafkaEventBatchReceiver.DeliverAsync([Record(0), Record(1), Record(2), Record(3)], records =>
        {
            attempts++;
            return records.Count > 2 ? Task.FromException(new InvalidOperationException("too large")) : Task.CompletedTask;
        }, (record, _) => commits.Add(record.Offset.Value), Policy(), Token);
        Assert.Equal(3, attempts);
        Assert.Equal([1L, 3L], commits);
    }

    [Fact]
    public async Task DeliverAsync_WhenCommitFails_ShouldRestartWithoutSplittingOrAcknowledgingLaterRecords()
    {
        var attempts = 0;
        var commits = 0;
        await Assert.ThrowsAsync<KafkaConsumerRestartRequiredException>(() => KafkaEventBatchReceiver.DeliverAsync(
            [Record(0), Record(1)], _ => { attempts++; return Task.CompletedTask; }, (_, _) =>
            {
                commits++;
                throw new KafkaException(new Error(ErrorCode.Local_AssignmentLost));
            }, Policy(), Token));
        Assert.Equal(1, attempts);
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task DeliverAsync_WhenCancellationIgnoredByHandler_ShouldAwaitItAndLeaveOffsetsUncommitted()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var delivery = KafkaEventBatchReceiver.DeliverAsync([Record(0), Record(1)], _ => completed.Task,
            (_, _) => commits++, Policy(), cancellation.Token);
        cancellation.Cancel();
        Assert.False(delivery.IsCompleted);
        completed.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(0, commits);
    }

    private static EventMessageRetryPolicy Policy(int retries = 1)
        => new(Options.Create(new ModuleEventBusOption { MessageRetryCount = retries }));

    private static ConsumeResult<string, string> Record(long offset, int partition = 0) => new()
    {
        Topic = "batch-test", Partition = partition, Offset = offset,
        Message = new Message<string, string>
        {
            Key = "key", Value = "事件", Headers = new Headers { { "id", [1, 2, 3] } }
        }
    };

    private sealed class CollectionClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
