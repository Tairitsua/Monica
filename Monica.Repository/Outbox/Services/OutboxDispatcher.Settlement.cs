using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

public sealed partial class OutboxDispatcher<TDbContext>
{
    private const int MAX_ACKNOWLEDGMENT_BATCH_SIZE = 100;

    private async Task<int> SettleAsync(TDbContext db, IReadOnlyList<OutboxMessage> candidates,
        IReadOnlyList<EventSendResult> results, CancellationToken deadline, CancellationToken cancellationToken)
    {
        var acknowledged = 0;
        // A delivery group is a slice of one claim and therefore shares its non-null lease token.
        var accepted = candidates.Where((_, index) => results[index].Status == EventSendStatus.Accepted);
        foreach (var batch in accepted.Chunk(MAX_ACKNOWLEDGMENT_BATCH_SIZE))
            acknowledged += await AcknowledgeAsync(db, batch, deadline, cancellationToken);

        for (var index = 0; index < candidates.Count; index++)
            if (results[index].Status != EventSendStatus.Accepted)
                await SettleFailureAsync(db, candidates[index], results[index], deadline, cancellationToken);
        return acknowledged;
    }

    private async Task<int> AcknowledgeAsync(TDbContext db, OutboxMessage[] candidates,
        CancellationToken deadline, CancellationToken cancellationToken)
    {
        var sequences = candidates.Select(candidate => candidate.Sequence).ToArray();
        var lease = candidates[0].LeaseId;
        var owned = db.Set<OutboxMessage>().Where(row => sequences.Contains(row.Sequence)
            && row.LeaseId == lease && row.DeliveredAtUtc == null);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            deadline.ThrowIfCancellationRequested();
            var acknowledgedAt = time.GetUtcNow().UtcDateTime;
            return await owned.ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveredAtUtc, acknowledgedAt)
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(row => row.LastError, (string?)null), deadline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The broker accepted these messages; a failed/ambiguous database acknowledgment is not a send failure.
            // Do not release claims or retry individual updates: the statement may already have committed.
            await RecordUnconfirmedAsync(owned, candidates[0].MessageId, exception);
            return 0;
        }
    }

    private async Task SettleFailureAsync(TDbContext db, OutboxMessage candidate, EventSendResult result,
        CancellationToken deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exception = result.Error!;
        var owned = db.Set<OutboxMessage>().Where(row => row.Sequence == candidate.Sequence
            && row.LeaseId == candidate.LeaseId && row.DeliveredAtUtc == null);
        if (result.Status == EventSendStatus.Unknown || deadline.IsCancellationRequested || exception is OperationCanceledException)
        {
            await RecordUnconfirmedAsync(owned, candidate.MessageId, exception);
            return;
        }

        logger.LogError(exception, "Outbox delivery failed for message {MessageId}; it remains due for retry.", candidate.MessageId);
        var error = exception.ToString();
        if (error.Length > 1000) error = error[..1000];
        try
        {
            var options = registration.Options;
            var backoff = Math.Min(options.RetryMaxDelay.TotalMilliseconds,
                options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(candidate.Attempts - 1, 16)));
            var jittered = Math.Min(options.RetryMaxDelay.TotalMilliseconds,
                backoff * (0.8 + Random.Shared.NextDouble() * 0.4));
            // Retry eligibility must use the same clock as claiming. This extra read is only on failure.
            var retryClock = UsesNativeClaims(db)
                ? await ReadStoreScalarAsync<DateTime>(db,
                    "SELECT (clock_timestamp() AT TIME ZONE 'UTC')", CancellationToken.None)
                : time.GetUtcNow().UtcDateTime;
            var retryAt = retryClock.AddMilliseconds(jittered);
            await owned.ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(row => row.NextAttemptAtUtc, retryAt)
                .SetProperty(row => row.LastError, error), CancellationToken.None);
        }
        catch (Exception cleanup)
        {
            logger.LogError(cleanup, "Failed to update outbox claim for message {MessageId}; lease expiry will permit retry.", candidate.MessageId);
        }
    }

    private async Task RecordUnconfirmedAsync(IQueryable<OutboxMessage> owned, string messageId, Exception exception)
    {
        logger.LogWarning(exception, "Outbox delivery or acknowledgment is unconfirmed, starting with message {MessageId}; unacknowledged claims remain until lease expiry.", messageId);
        var error = exception.ToString();
        if (error.Length > 1000) error = error[..1000];
        try
        {
            // Never clear a lease or overwrite a row that has already been acknowledged or reclaimed.
            await owned.ExecuteUpdateAsync(update => update.SetProperty(row => row.LastError, error), CancellationToken.None);
        }
        catch (Exception cleanup)
        {
            logger.LogError(cleanup, "Failed to record the unconfirmed outbox delivery for message {MessageId}; lease expiry will permit retry.", messageId);
        }
    }
}
