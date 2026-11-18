using System.Text;
using Confluent.Kafka;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Services.Support;
using static Monica.EventBus.Kafka.Providers.ConfluentKafka.KafkaEventBusSubscriptionHostedService;

namespace Monica.EventBus.Kafka.Providers.ConfluentKafka;

/// <summary>Collects bounded partition groups and commits ordered prefixes after processing or the configured failure action.</summary>
internal static class KafkaEventBatchReceiver
{
    internal sealed record CollectedBatch(IReadOnlyList<ConsumeResult<byte[], byte[]>> Records,
        ConsumeResult<byte[], byte[]>? Pending)
    {
        // Kafka may interleave partitions while polling. Keep one bounded window, then preserve
        // order within each partition instead of reducing every interleaved record to a singleton.
        internal IEnumerable<IReadOnlyList<ConsumeResult<byte[], byte[]>>> PartitionGroups
            => Records.GroupBy(record => record.TopicPartition).Select(group => (IReadOnlyList<ConsumeResult<byte[], byte[]>>)group.ToArray());
    }

    internal static CollectedBatch Collect(IConsumer<byte[], byte[]> consumer, ConsumeResult<byte[], byte[]> first,
        KafkaEventBatchOptions options, Func<int> assignmentVersion, CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        var version = assignmentVersion();
        var records = new List<ConsumeResult<byte[], byte[]>> { first };
        var bytes = GetSize(first);
        while (records.Count < options.MaxMessages && bytes < options.MaxBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = options.MaxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero && options.MaxWait != TimeSpan.Zero) break;
            var poll = remaining <= TimeSpan.Zero ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, 100));
            var next = consumer.Consume(poll);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != assignmentVersion())
                throw new KafkaConsumerRestartRequiredException(
                    "Kafka partition ownership changed while collecting an unacknowledged batch.",
                    new InvalidOperationException("The assignment generation changed."));
            if (next?.Message is null)
            {
                if (options.MaxWait == TimeSpan.Zero) break;
                continue;
            }
            var nextBytes = GetSize(next);
            if (nextBytes > options.MaxBytes - bytes)
                return new CollectedBatch(records, next);
            records.Add(next);
            bytes += nextBytes;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new CollectedBatch(records, null);
    }

    internal static async Task DeliverAsync(IReadOnlyList<ConsumeResult<byte[], byte[]>> records,
        Func<IReadOnlyList<ConsumeResult<byte[], byte[]>>, Task> handle,
        Action<ConsumeResult<byte[], byte[]>, int> commit, EventMessageRetryPolicy retryPolicy,
        CancellationToken cancellationToken, Func<ConsumeResult<byte[], byte[]>, Exception, Task>? handleExhausted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processed = records.Count;
        if (records.Count == 1)
        {
            try
            {
                await retryPolicy.ExecuteAsync(() => handle(records), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                if (handleExhausted is null)
                    throw new KafkaProcessingBlockedException(
                        $"Batch consumer stopped at {records[0].TopicPartitionOffset} after exhausting singleton retries; the record remains unacknowledged.", failure);
                await handleExhausted(records[0], failure).ConfigureAwait(false);
                processed = 0;
            }
        }
        else
        {
            try
            {
                await handle(records).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Each tree node is attempted once. Singleton retry budgets never restart in this tree.
                var middle = records.Count / 2;
                await DeliverAsync(records.Take(middle).ToArray(), handle, commit, retryPolicy, cancellationToken, handleExhausted).ConfigureAwait(false);
                await DeliverAsync(records.Skip(middle).ToArray(), handle, commit, retryPolicy, cancellationToken, handleExhausted).ConfigureAwait(false);
                return;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // A failed/ambiguous commit must never cause splitting or a later offset acknowledgment.
        try { commit(records[^1], processed); }
        catch (Exception failure)
        {
            throw new KafkaConsumerRestartRequiredException(
                "Kafka batch acknowledgment failed; restart from the broker's committed position.", failure);
        }
    }

    internal static long GetSize(ConsumeResult<byte[], byte[]> record)
    {
        static int Size(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
        return (long)Size(record.Topic) + (record.Message?.Key?.Length ?? 0) + (record.Message?.Value?.Length ?? 0)
            + (record.Message?.Headers?.Sum(header => (long)Size(header.Key) + (header.GetValueBytes()?.Length ?? 0)) ?? 0);
    }
}
