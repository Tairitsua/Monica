using Confluent.Kafka;
using Monica.Kafka;

namespace Monica.DataChannel.Providers.Kafka;

/// <summary>Owns all native consumer calls. Workers execute the pipeline or publish a dead letter and report completion to the owner.</summary>
internal sealed class KafkaConsumerRuntime(
    KafkaOptions options,
    IKafkaDataChannelClientFactory clients,
    IProducer<byte[], byte[]>? deadLetterProducer,
    Func<ConsumeResult<byte[], byte[]>, CancellationToken, Task> deliver,
    Action<Exception, string> reportFailure,
    Action<bool> reportAvailability)
{
    private static readonly TimeSpan POLL_TIMEOUT = TimeSpan.FromMilliseconds(100);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<TopicPartition, PartitionState> _partitions = [];
    private readonly List<Worker> _workers = [];
    private readonly List<PartitionState> _retiredStates = [];
    private readonly List<Exception> _cleanupFailures = [];
    private readonly HashSet<TopicPartition> _paused = [];
    private Task? _owner;
    private long _epoch;
    private long _received;
    private long _processed;
    private long _committed;
    private long _skipped;
    private long _deadLettered;
    private string _state = "Starting";
    private Error? _fatalError;
    private DateTimeOffset _nextNativeErrorReport;
    private KafkaConsumerSnapshot _snapshot = new("Starting", 0, 0, 0, 0, 0, 0, 0, []);

    internal KafkaConsumerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        _owner = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await _started.Task.WaitAsync(cancellationToken);
    }

    internal async Task StopAsync()
    {
        // No consumer is disposed while its owner is polling or while protected work still runs.
        try { await _lifetime.CancelAsync(); }
        finally
        {
            try
            {
                if (_owner is not null) await _owner;
                if (_cleanupFailures.Count > 0)
                    throw new AggregateException("Kafka consumer cleanup failed.", _cleanupFailures);
            }
            finally { _lifetime.Dispose(); }
        }
    }

    private void Run()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                IConsumer<byte[], byte[]>? consumer = null;
                try
                {
                    _fatalError = null;
                    var callbacks = new KafkaConsumerCallbacks(Assign, Retire, Retire) { Error = OnNativeError };
                    consumer = clients.CreateConsumer(KafkaClientConfiguration.Consumer(options), callbacks);
                    consumer.Subscribe(options.Topic!);
                    _state = "Polling";
                    _started.TrySetResult();
                    Poll(consumer);
                }
                catch (Exception exception)
                {
                    if (!_started.Task.IsCompleted)
                    {
                        _started.TrySetException(exception);
                        return;
                    }
                    if (!_lifetime.IsCancellationRequested)
                    {
                        _state = "Recovering";
                        reportFailure(exception, "Kafka input failed; restarting from committed offsets after owned work drains.");
                    }
                }
                finally
                {
                    try
                    {
                        Retire(_partitions.Keys.ToArray());
                        DrainWorkers();
                    }
                    catch (Exception exception)
                    {
                        _cleanupFailures.Add(exception);
                        reportFailure(exception, "Kafka worker cleanup failed.");
                    }
                    finally
                    {
                        try
                        {
                            // Close enters the same librdkafka LeaveGroup path avoided by the native EventBus provider.
                            // Dispose uses NO_CONSUMER_CLOSE. Group reassignment therefore waits for session timeout.
                            consumer?.Dispose();
                        }
                        catch (Exception exception)
                        {
                            _cleanupFailures.Add(exception);
                            reportFailure(exception, "Kafka consumer disposal failed.");
                        }
                    }
                    _paused.Clear();
                    PublishSnapshot();
                }
                if (!_lifetime.IsCancellationRequested)
                    _lifetime.Token.WaitHandle.WaitOne(options.RetryDelay);
            }
        }
        finally
        {
            _state = "Stopped";
            _started.TrySetCanceled();
            PublishSnapshot();
        }
    }

    private void Poll(IConsumer<byte[], byte[]> consumer)
    {
        var nextErrorReport = DateTimeOffset.MinValue;
        while (!_lifetime.IsCancellationRequested)
        {
            CompleteWorkers();
            ReleaseRetiredStates();
            CommitReadyHeads(consumer);
            StartWorkers();
            ApplyBackpressure(consumer);
            PublishSnapshot();
            try
            {
                var record = consumer.Consume(POLL_TIMEOUT);
                if (_fatalError is { } fatalError) throw new KafkaException(fatalError);
                if (record is not null && !record.IsPartitionEOF) Accept(consumer, record);
            }
            catch (ConsumeException exception) when (!exception.Error.IsFatal && !IsOwnershipLoss(exception))
            {
                // Continue polling so broker callbacks and other partitions remain live. Never acknowledge an error.
                _state = "Recovering";
                BlockConsumeFailure(consumer, exception);
                if (DateTimeOffset.UtcNow >= nextErrorReport)
                {
                    reportFailure(exception, "Kafka input polling failed; no offset was acknowledged.");
                    nextErrorReport = DateTimeOffset.UtcNow + options.RetryDelay;
                }
                _lifetime.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10));
            }
        }
    }

    private void OnNativeError(Error error)
    {
        if (!error.IsError) return;
        _state = "Recovering";
        if (error.IsFatal) _fatalError = error;
        if (DateTimeOffset.UtcNow < _nextNativeErrorReport) return;
        _nextNativeErrorReport = DateTimeOffset.UtcNow + options.RetryDelay;
        reportFailure(new KafkaException(error), "Kafka reported a native transport error; input acknowledgment remains explicit.");
    }

    private void BlockConsumeFailure(IConsumer<byte[], byte[]> consumer, ConsumeException exception)
    {
        var record = exception.ConsumerRecord;
        if (record is null || string.IsNullOrEmpty(record.Topic) || !_partitions.ContainsKey(record.TopicPartition)) return;
        // A deserialization or offset-reset error can otherwise be followed by a later readable offset. Fence the
        // partition before continuing to poll; a corrected deployment must explicitly reinitialize this input.
        Retire([record.TopicPartition]);
        _partitions.Add(record.TopicPartition, new PartitionState(record.TopicPartition, ++_epoch)
        {
            Blocked = new(record.Topic, record.Partition.Value, record.Offset.Value, exception.Error.Code.ToString())
        });
        consumer.Pause([record.TopicPartition]);
        _paused.Add(record.TopicPartition);
    }

    private void Assign(IReadOnlyList<TopicPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            if (_partitions.ContainsKey(partition)) Retire([partition]);
            _partitions.Add(partition, new PartitionState(partition, ++_epoch));
            // Native assignment is applied after this callback; pause decisions run after Consume returns.
            _paused.Remove(partition);
        }
    }

    private void Retire(IReadOnlyList<TopicPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            if (!_partitions.Remove(partition, out var state)) continue;
            _paused.Remove(partition);
            // Removing ownership fences completions before cancellation callbacks can run.
            try { state.CancellationCompletion = state.Cancellation.CancelAsync(); }
            catch (Exception exception) { reportFailure(exception, "Kafka partition cancellation callback failed."); }
            state.Record = null;
            _retiredStates.Add(state);
        }
    }

    private void Accept(IConsumer<byte[], byte[]> consumer, ConsumeResult<byte[], byte[]> record)
    {
        if (!_partitions.TryGetValue(record.TopicPartition, out var state)) return;
        if (state.Record is not null || state.Blocked is not null || BufferedCount() >= options.MaxBufferedMessages
            || _retiredStates.Any(retired => retired.Partition == state.Partition))
        {
            // An assignment callback can add a partition while every existing partition is paused for capacity.
            // Retain the broker position rather than keeping a record beyond the application buffer bound.
            consumer.Seek(record.TopicPartitionOffset);
            consumer.Pause([state.Partition]);
            _paused.Add(state.Partition);
            return;
        }
        state.Record = record;
        state.RetryAt = DateTimeOffset.MinValue;
        _received++;
        consumer.Pause([state.Partition]);
        _paused.Add(state.Partition);
    }

    private void StartWorkers()
    {
        foreach (var state in _partitions.Values)
        {
            if (_lifetime.IsCancellationRequested) break;
            if (ActiveWorkCount() >= options.MaxConcurrentPartitions) break;
            if (state.Record is null || state.IsRunning || state.ReadyToCommit || state.Blocked is not null
                || state.RetryAt > DateTimeOffset.UtcNow || _workers.Any(worker => worker.State.Partition == state.Partition)
                || _retiredStates.Any(retired => retired.Partition == state.Partition)) continue;
            var processingFailure = state.ProcessingFailure;
            if (processingFailure is null) state.Attempts++;
            state.IsRunning = true;
            var record = state.Record;
            var token = state.Cancellation.Token;
            var task = Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (processingFailure is null)
                        await deliver(record, token);
                    else
                        await KafkaDeliveryFailure.ForwardAsync(deadLetterProducer!, options.DeadLetterTopic!,
                            options.ConsumerGroupId!, record, processingFailure, token);
                    token.ThrowIfCancellationRequested();
                    return (Exception?)null;
                }
                catch (Exception exception) { return exception; }
            }, CancellationToken.None);
            _workers.Add(new Worker(state, task));
        }
    }

    private void CompleteWorkers()
    {
        for (var index = _workers.Count - 1; index >= 0; index--)
        {
            var worker = _workers[index];
            if (!worker.Task.IsCompleted) continue;
            _workers.RemoveAt(index);
            worker.State.IsRunning = false;
            var failure = worker.Task.GetAwaiter().GetResult();
            var state = worker.State;
            if (!IsCurrent(state) || state.Cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested) continue;
            var record = state.Record!;
            if (failure is null)
            {
                state.ReadyToCommit = true;
                if (state.ProcessingFailure is null) _processed++;
                continue;
            }
            if (state.ProcessingFailure is not null)
            {
                state.RetryAt = DateTimeOffset.UtcNow + options.RetryDelay;
                reportFailure(failure, $"Kafka dead-letter publication failed at {record.TopicPartitionOffset}; retrying the handoff without acknowledging.");
                continue;
            }
            if (state.Attempts < options.ProcessingMaxAttempts)
            {
                state.RetryAt = DateTimeOffset.UtcNow + options.RetryDelay;
                reportFailure(failure, $"Kafka processing failed at {record.TopicPartitionOffset}; retry {state.Attempts + 1} is pending without acknowledgment.");
                continue;
            }

            var identity = KafkaDeliveryFailure.Describe(record, failure);
            switch (options.ProcessingFailureAction)
            {
                case KafkaFailureAction.LogAndSkip:
                    reportFailure(failure, $"Kafka input is skipping {identity} after {state.Attempts} attempts.");
                    state.ProcessingFailure = failure;
                    state.ReadyToCommit = true;
                    break;
                case KafkaFailureAction.DeadLetter:
                    reportFailure(failure, $"Kafka input is forwarding {identity} to '{options.DeadLetterTopic}' after {state.Attempts} attempts.");
                    state.ProcessingFailure = failure;
                    break;
                default:
                    state.Blocked = new(record.Topic, record.Partition.Value, record.Offset.Value, failure.GetType().Name);
                    state.Record = null;
                    reportFailure(failure, $"Kafka partition {record.TopicPartition} is blocked at offset {record.Offset}; reinitialize after correcting the failure. {identity}");
                    break;
            }
        }
    }

    private void CommitReadyHeads(IConsumer<byte[], byte[]> consumer)
    {
        foreach (var state in _partitions.Values)
        {
            if (_lifetime.IsCancellationRequested) break;
            if (!state.ReadyToCommit || state.Record is null || state.Cancellation.IsCancellationRequested
                || state.RetryAt > DateTimeOffset.UtcNow) continue;
            var record = state.Record;
            try
            {
                // Each earlier record in this partition already has a committed disposition.
                consumer.Commit([new TopicPartitionOffset(state.Partition, record.Offset + 1)]);
                if (state.ProcessingFailure is not null)
                {
                    if (options.ProcessingFailureAction == KafkaFailureAction.LogAndSkip) _skipped++;
                    else _deadLettered++;
                }
                state.ProcessingFailure = null;
                state.Record = null;
                state.ReadyToCommit = false;
                state.CommitFailed = false;
                state.Attempts = 0;
                state.RetryAt = DateTimeOffset.MinValue;
                _committed++;
                _state = "Polling";
            }
            catch (KafkaException exception) when (IsOwnershipLoss(exception) || exception.Error.IsFatal)
            {
                // Do not seek a lost partition or commit any later position with an unsafe generation.
                throw new InvalidOperationException("Kafka ownership changed before acknowledgment; recreate the consumer.", exception);
            }
            catch (Exception exception)
            {
                state.RetryAt = DateTimeOffset.UtcNow + options.RetryDelay;
                state.CommitFailed = true;
                _state = "Recovering";
                reportFailure(exception, $"Kafka acknowledgment failed at {record.TopicPartitionOffset}; retrying only the commit.");
            }
        }
    }

    private void ApplyBackpressure(IConsumer<byte[], byte[]> consumer)
    {
        var full = BufferedCount() >= options.MaxBufferedMessages;
        foreach (var state in _partitions.Values)
        {
            var pause = state.Record is not null || state.Blocked is not null || full
                || _retiredStates.Any(retired => retired.Partition == state.Partition);
            if (pause && _paused.Add(state.Partition)) consumer.Pause([state.Partition]);
            if (!pause && _paused.Remove(state.Partition)) consumer.Resume([state.Partition]);
        }
    }

    private bool IsCurrent(PartitionState state) => _partitions.TryGetValue(state.Partition, out var current)
        && current.Epoch == state.Epoch && ReferenceEquals(current, state);

    private int BufferedCount() => _partitions.Values.Count(state => state.Record is not null)
        + _retiredStates.Count(state => state.IsRunning || !state.CancellationCompletion.IsCompleted);

    private int ActiveWorkCount() => _workers.Count
        + _retiredStates.Count(state => !state.IsRunning && !state.CancellationCompletion.IsCompleted);

    private void DrainWorkers()
    {
        foreach (var worker in _workers)
        {
            // Pipeline cancellation is cooperative. Never release the inner receiver or start its replacement early.
            worker.Task.GetAwaiter().GetResult();
            worker.State.IsRunning = false;
        }
        _workers.Clear();
        foreach (var state in _retiredStates)
        {
            ObserveCancellation(state);
            state.Cancellation.Dispose();
        }
        _retiredStates.Clear();
    }

    private void ReleaseRetiredStates()
    {
        for (var index = _retiredStates.Count - 1; index >= 0; index--)
        {
            var state = _retiredStates[index];
            if (state.IsRunning || !state.CancellationCompletion.IsCompleted) continue;
            ObserveCancellation(state);
            state.Cancellation.Dispose();
            _retiredStates.RemoveAt(index);
        }
    }

    private void ObserveCancellation(PartitionState state)
    {
        try { state.CancellationCompletion.GetAwaiter().GetResult(); }
        catch (Exception exception) { reportFailure(exception, "Kafka partition cancellation callback failed."); }
    }

    private void PublishSnapshot()
    {
        var blocked = _partitions.Values.Where(state => state.Blocked is not null).Select(state => state.Blocked!).ToArray();
        var stateName = blocked.Length > 0 ? "Blocked"
            : _partitions.Values.Any(state => state.CommitFailed) ? "Recovering"
            : _partitions.Values.Any(state => state.Record is not null && !state.ReadyToCommit && !state.IsRunning && state.Attempts > 0) ? "Retrying"
            : _state;
        Volatile.Write(ref _snapshot, new(stateName, _partitions.Count, _paused.Count, BufferedCount(), ActiveWorkCount(),
            _received, _processed, _committed, Array.AsReadOnly(blocked)) { SkippedMessages = _skipped, DeadLetteredMessages = _deadLettered });
        reportAvailability(stateName is "Recovering" or "Retrying" or "Blocked" or "Stopped");
    }

    private static bool IsOwnershipLoss(KafkaException exception) => IsOwnershipLoss(exception.Error.Code)
        || exception is TopicPartitionOffsetException offsetFailure && offsetFailure.Results.Any(item => IsOwnershipLoss(item.Error.Code));

    private static bool IsOwnershipLoss(ErrorCode code) => code is ErrorCode.Local_State or ErrorCode.Local_AssignmentLost
        or ErrorCode.Local_MaxPollExceeded or ErrorCode.IllegalGeneration or ErrorCode.UnknownMemberId or ErrorCode.RebalanceInProgress;

    private sealed class PartitionState(TopicPartition partition, long epoch)
    {
        internal TopicPartition Partition { get; } = partition;
        internal long Epoch { get; } = epoch;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task CancellationCompletion { get; set; } = Task.CompletedTask;
        internal ConsumeResult<byte[], byte[]>? Record { get; set; }
        internal KafkaBlockedPartition? Blocked { get; set; }
        internal Exception? ProcessingFailure { get; set; }
        internal bool IsRunning { get; set; }
        internal bool ReadyToCommit { get; set; }
        internal bool CommitFailed { get; set; }
        internal int Attempts { get; set; }
        internal DateTimeOffset RetryAt { get; set; }
    }

    private sealed record Worker(PartitionState State, Task<Exception?> Task);
}
