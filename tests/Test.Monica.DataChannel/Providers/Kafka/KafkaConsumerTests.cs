using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Kafka;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.DataChannel.Providers.Kafka;

public sealed class KafkaConsumerTests
{
    [Fact]
    public async Task Input_WhenPartitionWorkWaits_ShouldKeepPollingAndCompleteOtherPartitionsWithoutOvertaking()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        var release = Signal();
        scenario.Receive = context => Metadata(context) is { Partition: 0, Offset: 0 }
            ? release.Task.WaitAsync(context.CancellationToken) : Task.CompletedTask;
        await using var application = await StartAsync(scenario, deadline.Token);
        var channel = Channel(application);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        consumer.Assign(0, 1);
        consumer.Enqueue(KafkaScenario.Record(0, 0), KafkaScenario.Record(0, 1),
            KafkaScenario.Record(1, 0), KafkaScenario.Record(1, 1));

        await scenario.Signal.UntilAsync(() => Positions(consumer, 1).Length == 2, deadline.Token);
        var polling = consumer.Polls;
        await scenario.Signal.UntilAsync(() => consumer.Polls >= polling + 3, deadline.Token);
        Assert.Equal([1L, 2L], Positions(consumer, 1));
        Assert.Empty(Positions(consumer, 0));
        Assert.Single(scenario.Deliveries, context => Metadata(context).Partition == 0);
        Assert.True(consumer.IsPaused(0));
        Assert.InRange(KafkaScenario.Snapshot(channel).ActiveWorkers, 1, scenario.Options.MaxConcurrentPartitions);

        release.TrySetResult();
        await scenario.Signal.UntilAsync(() => Positions(consumer, 0).Length == 2, deadline.Token);
        Assert.Equal([1L, 2L], Positions(consumer, 0));
        Assert.Equal([0L, 1L], scenario.Deliveries.Where(context => Metadata(context).Partition == 0)
            .Select(context => Metadata(context).Offset).ToArray());
        AssertSingleConsumerOwner(consumer);
    }

    [Fact]
    public async Task Input_WhenWorkersReachCapacity_ShouldBoundRetainedRecordsAndResumeAfterCompletion()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        scenario.Options.MaxConcurrentPartitions = 2;
        scenario.Options.MaxBufferedMessages = 2;
        var release = Signal();
        scenario.Receive = context => release.Task.WaitAsync(context.CancellationToken);
        await using var application = await StartAsync(scenario, deadline.Token);
        var channel = Channel(application);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        consumer.Assign(0, 1, 2, 3);
        consumer.Enqueue(Enumerable.Range(0, 4).Select(partition => KafkaScenario.Record(partition, 0)).ToArray());

        await scenario.Signal.UntilAsync(() => KafkaScenario.Snapshot(channel) is
            { BufferedMessages: 2, ActiveWorkers: 2, PausedPartitions: 4 }, deadline.Token);
        var polling = consumer.Polls;
        await scenario.Signal.UntilAsync(() => consumer.Polls >= polling + 3, deadline.Token);
        Assert.Equal(2, scenario.Deliveries.Count);
        Assert.Equal(2, consumer.PendingRecords);
        Assert.Empty(consumer.Commits);

        release.TrySetResult();
        await scenario.Signal.UntilAsync(() => consumer.Commits.Count == 4, deadline.Token);
        Assert.Equal([0, 1, 2, 3], consumer.Commits.Select(position => position.Partition.Value).Order().ToArray());
        Assert.All(consumer.Commits, position => Assert.Equal(1, position.Offset.Value));
        Assert.Contains(consumer.Calls, call => call.Method == "Resume");
        AssertSingleConsumerOwner(consumer);
    }

    [Fact]
    public async Task Input_WhenHeadAlwaysFails_ShouldRetryFinitelyBlockItsPartitionAndContinueHealthyWork()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        scenario.Options.ProcessingMaxAttempts = 3;
        scenario.Receive = context => Metadata(context).Partition == 0
            ? Task.FromException(new InvalidDataException("Invalid business record.")) : Task.CompletedTask;
        await using var application = await StartAsync(scenario, deadline.Token);
        var channel = Channel(application);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        consumer.Assign(0, 1);
        consumer.Enqueue(KafkaScenario.Record(0, 7), KafkaScenario.Record(0, 8), KafkaScenario.Record(1, 20));

        await scenario.Signal.UntilAsync(() => KafkaScenario.Snapshot(channel).BlockedPartitions.Count == 1
            && consumer.Commits.Count == 1, deadline.Token);
        var polling = consumer.Polls;
        await scenario.Signal.UntilAsync(() => consumer.Polls >= polling + 3, deadline.Token);
        var failedDeliveries = scenario.Deliveries.Where(context => Metadata(context).Partition == 0).ToArray();
        Assert.Equal(3, failedDeliveries.Length);
        Assert.All(failedDeliveries, context => Assert.Equal(7, Metadata(context).Offset));
        Assert.Empty(Positions(consumer, 0));
        Assert.Equal([21L], Positions(consumer, 1));
        Assert.True(consumer.IsPaused(0));
        var blocked = Assert.Single(KafkaScenario.Snapshot(channel).BlockedPartitions);
        Assert.Equal((KafkaScenario.TOPIC, 0, 7L), (blocked.Topic, blocked.Partition, blocked.Offset));
        Assert.Equal("Blocked", KafkaScenario.Snapshot(channel).State);
        Assert.True(channel.Pipe.IsNotAvailable);
    }

    [Fact]
    public async Task Input_WhenCommitTemporarilyFails_ShouldRetryOnlyAcknowledgmentWithTheSameNextOffset()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        await using var application = await StartAsync(scenario, deadline.Token);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        consumer.CommitFailuresRemaining = 2;
        consumer.Assign(0);
        consumer.Enqueue(KafkaScenario.Record(0, 42));

        await scenario.Signal.UntilAsync(() => consumer.Commits.Count == 1, deadline.Token);

        Assert.Single(scenario.Deliveries);
        Assert.Equal([43L, 43L, 43L], consumer.CommitAttempts.Select(position => position.Offset.Value).ToArray());
        Assert.Equal([43L], Positions(consumer, 0));
        AssertSingleConsumerOwner(consumer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_WhenOwnershipChanges_ShouldDrainOldWorkAndIgnoreItsLateSuccess(bool lost)
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        var oldRelease = Signal();
        var replacementRelease = Signal();
        var oldEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementEntered = Signal();
        var attempts = 0;
        scenario.Receive = async context =>
        {
            if (Metadata(context).Partition != 0) return;
            if (Interlocked.Increment(ref attempts) == 1)
            {
                oldEntered.TrySetResult(context.CancellationToken);
                // Deliberately emulate an application that ignores cancellation while completing its I/O.
                await oldRelease.Task;
            }
            else
            {
                replacementEntered.TrySetResult();
                await replacementRelease.Task.WaitAsync(context.CancellationToken);
            }
        };
        await using var application = await StartAsync(scenario, deadline.Token);
        try
        {
            var consumer = Assert.Single(scenario.Clients.Consumers);
            consumer.Assign(0, 1);
            consumer.Enqueue(KafkaScenario.Record(0, 0));
            var oldToken = await oldEntered.Task.WaitAsync(deadline.Token);
            if (lost) consumer.Lose(0); else consumer.Revoke(0);
            consumer.Assign(0);
            consumer.Enqueue(KafkaScenario.Record(0, 0), KafkaScenario.Record(1, 0));
            await scenario.Signal.UntilAsync(() => oldToken.IsCancellationRequested && Positions(consumer, 1).Length == 1,
                deadline.Token);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Empty(Positions(consumer, 0));
            oldRelease.TrySetResult();
            await replacementEntered.Task.WaitAsync(deadline.Token);
            Assert.Empty(Positions(consumer, 0));
            replacementRelease.TrySetResult();
            await scenario.Signal.UntilAsync(() => Positions(consumer, 0).Length == 1, deadline.Token);
            Assert.Equal([1L], Positions(consumer, 0));
            Assert.Equal(2, Volatile.Read(ref attempts));
            AssertSingleConsumerOwner(consumer);
        }
        finally
        {
            oldRelease.TrySetResult();
            replacementRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task ReInitialize_WhenWorkIsRetiring_ShouldWaitForCancellationDrainBeforeCreatingReplacement()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        var canceled = Signal();
        var drain = Signal();
        scenario.Receive = async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                await drain.Task;
            }
        };
        await using var application = await StartAsync(scenario, deadline.Token);
        try
        {
            var channel = Channel(application);
            var original = Assert.Single(scenario.Clients.Consumers);
            original.Assign(0);
            original.Enqueue(KafkaScenario.Record(0, 4));
            await scenario.Signal.UntilAsync(() => scenario.Deliveries.Count == 1, deadline.Token);
            var reinitialize = channel.ReInitialize(deadline.Token);
            await canceled.Task.WaitAsync(deadline.Token);
            Assert.False(reinitialize.IsCompleted);
            Assert.Single(scenario.Clients.Consumers);
            Assert.Equal(0, original.DisposeCalls);
            drain.TrySetResult();
            await reinitialize.WaitAsync(deadline.Token);
            Assert.Equal(1, original.DisposeCalls);
            Assert.Empty(original.Commits);
            Assert.Equal(2, scenario.Clients.Consumers.Count);

            scenario.Receive = _ => Task.CompletedTask;
            var replacement = scenario.Clients.Consumers.Last();
            replacement.Assign(0);
            replacement.Enqueue(KafkaScenario.Record(0, 4));
            await scenario.Signal.UntilAsync(() => replacement.Commits.Count == 1, deadline.Token);
            Assert.Equal([5L], Positions(replacement, 0));
        }
        finally { drain.TrySetResult(); }
    }

    [Fact]
    public async Task Shutdown_WhenInputIsRunning_ShouldCancelAndDrainBeforeDisposingItsNativeConsumer()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        var canceled = Signal();
        var drain = Signal();
        scenario.Receive = async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
            catch (OperationCanceledException) { canceled.TrySetResult(); await drain.Task; }
        };
        var application = await StartAsync(scenario, deadline.Token);
        try
        {
            var consumer = Assert.Single(scenario.Clients.Consumers);
            consumer.Assign(0);
            consumer.Enqueue(KafkaScenario.Record(0, 0));
            await scenario.Signal.UntilAsync(() => scenario.Deliveries.Count == 1, deadline.Token);
            var stop = application.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(deadline.Token);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, consumer.DisposeCalls);
            drain.TrySetResult();
            await stop.WaitAsync(deadline.Token);
            Assert.Equal(1, consumer.DisposeCalls);
            Assert.Empty(consumer.Commits);
            Assert.DoesNotContain(consumer.Calls, call => call.Method == "Close");
            AssertSingleConsumerOwner(consumer);
        }
        finally { drain.TrySetResult(); await application.DisposeAsync(); }
    }

    [Fact]
    public async Task Input_WhenRawBytesAreSelected_ShouldPreserveTombstonesEmptyValuesAndDetachedMetadata()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        scenario.Options.InputPayloadFormat = KafkaInputPayloadFormat.RawBytes;
        await using var application = await StartAsync(scenario, deadline.Token);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        var source = KafkaScenario.Record(0, 0);
        source.Message.Value = [0, 255];
        source.Message.Key = [3, 128];
        byte[] headerValue = [4, 129];
        source.Message.Headers = new Headers { { "same", null }, { "same", [] }, { "same", headerValue } };
        consumer.Assign(0);
        consumer.Enqueue(source, KafkaScenario.Record(0, 1, null), KafkaScenario.Record(0, 2, ""));
        await scenario.Signal.UntilAsync(() => consumer.Commits.Count == 3, deadline.Token);
        source.Message.Value[0] = 99;
        source.Message.Key[0] = 99;
        headerValue[0] = 99;

        var deliveries = scenario.Deliveries.ToArray();
        Assert.Equal([0, 255], Assert.IsType<byte[]>(deliveries[0].Data));
        Assert.Null(deliveries[1].Data);
        Assert.Empty(Assert.IsType<byte[]>(deliveries[2].Data));
        var metadata = Metadata(deliveries[0]);
        Assert.Equal((KafkaScenario.TOPIC, 0, 0L), (metadata.Topic, metadata.Partition, metadata.Offset));
        Assert.Equal(new byte[] { 3, 128 }, metadata.Key);
        Assert.Equal(source.Message.Timestamp, metadata.Timestamp);
        Assert.Equal(["same", "same", "same"], metadata.Headers.Select(header => header.Key).ToArray());
        Assert.Null(metadata.Headers[0].Value);
        Assert.Empty(metadata.Headers[1].Value!);
        Assert.Equal(new byte[] { 4, 129 }, metadata.Headers[2].Value);
        Assert.True(deliveries[0].CancellationToken.CanBeCanceled);
        Assert.Equal([1L, 2L, 3L], Positions(consumer, 0));
    }

    [Fact]
    public async Task Input_WhenUtf8ContainsInvalidBytes_ShouldBlockWithoutDispatchingOrAcknowledging()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        scenario.Options.ProcessingMaxAttempts = 1;
        await using var application = await StartAsync(scenario, deadline.Token);
        var channel = Channel(application);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        var invalid = KafkaScenario.Record(0, 0);
        invalid.Message.Value = [255];
        consumer.Assign(0, 1);
        consumer.Enqueue(invalid, KafkaScenario.Record(0, 1), KafkaScenario.Record(1, 0, "healthy"));
        await scenario.Signal.UntilAsync(() => KafkaScenario.Snapshot(channel).BlockedPartitions.Count == 1
            && consumer.Commits.Count == 1, deadline.Token);

        var delivered = Assert.Single(scenario.Deliveries);
        Assert.Equal("healthy", delivered.Data);
        Assert.Empty(Positions(consumer, 0));
        Assert.Equal("DecoderFallbackException", Assert.Single(KafkaScenario.Snapshot(channel).BlockedPartitions).Reason);
    }

    [Fact]
    public async Task Input_WhenUtf8IsEmptyOrNull_ShouldDeliverBothWithoutFilteringRecords()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        await using var application = await StartAsync(scenario, deadline.Token);
        var consumer = Assert.Single(scenario.Clients.Consumers);
        consumer.Assign(0);
        consumer.Enqueue(KafkaScenario.Record(0, 0, ""), KafkaScenario.Record(0, 1, null));
        await scenario.Signal.UntilAsync(() => consumer.Commits.Count == 2, deadline.Token);
        var deliveries = scenario.Deliveries.ToArray();
        Assert.Equal("", Assert.IsType<string>(deliveries[0].Data));
        Assert.Null(deliveries[1].Data);
        Assert.Equal([1L, 2L], Positions(consumer, 0));
    }

    private static async Task<MonicaTestApplication> StartAsync(KafkaScenario scenario, CancellationToken cancellationToken)
    {
        var application = await new KafkaTestApplicationFactory(scenario).CreateAsync(cancellationToken: cancellationToken);
        await scenario.Signal.UntilAsync(() => scenario.Clients.Consumers.Count == 1, cancellationToken);
        return application;
    }

    private static global::Monica.DataChannel.DataChannel Channel(MonicaTestApplication application) =>
        application.Services.GetRequiredService<IDataChannelManager>().Fetch(KafkaScenario.CHANNEL_ID)!;
    private static KafkaMessageMetadata Metadata(ChannelDataContext context) =>
        Assert.IsType<KafkaMessageMetadata>(((IDictionary<string, object?>)context.Metadata)[KafkaMessageMetadata.METADATA_KEY]);
    private static long[] Positions(ScriptedKafkaConsumer consumer, int partition) => consumer.Commits
        .Where(position => position.Partition.Value == partition).Select(position => position.Offset.Value).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return deadline;
    }
    private static void AssertSingleConsumerOwner(ScriptedKafkaConsumer consumer) =>
        Assert.Single(consumer.Calls.Select(call => call.ThreadId).Distinct());
}
