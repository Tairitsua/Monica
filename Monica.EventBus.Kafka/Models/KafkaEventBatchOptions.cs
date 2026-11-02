namespace Monica.EventBus.Kafka.Models;

/// <summary>Immutable collection bounds for explicit distributed batch handlers on native Kafka.</summary>
/// <remarks>
/// Defaults are 100 records, 1 MiB of UTF-8 body/key/topic/header bytes, and a 50 ms window from the first record.
/// Groups contain ordered records from one partition. A record exceeding the byte bound runs alone.
/// These limits do not replace broker limits or ConsumerMaxPollInterval; include all retries and commit time there.
/// </remarks>
public sealed class KafkaEventBatchOptions
{
    /// <summary>Creates positive count/byte limits and a nonnegative collection window; zero drains only available records.</summary>
    public KafkaEventBatchOptions(int maxMessages = 100, long maxBytes = 1024 * 1024, TimeSpan? maxWait = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        var wait = maxWait ?? TimeSpan.FromMilliseconds(50);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero, nameof(maxWait));
        MaxMessages = maxMessages;
        MaxBytes = maxBytes;
        MaxWait = wait;
    }

    /// <summary>Maximum records in one receive execution.</summary>
    public int MaxMessages { get; }
    /// <summary>Maximum UTF-8 body and envelope bytes, excluding broker framing.</summary>
    public long MaxBytes { get; }
    /// <summary>Maximum collection time after the first record, excluding handler and transaction time.</summary>
    public TimeSpan MaxWait { get; }
}
