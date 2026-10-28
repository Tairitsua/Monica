using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

public sealed partial class OutboxDispatcher<TDbContext>
{
    private readonly SemaphoreSlim _deliverySlots = new(registration.Options.DeliveryConcurrency,
        registration.Options.DeliveryConcurrency);

    /// <summary>
    /// Runs one bounded pass, claiming only available delivery slots. Failed attempts count toward the limit;
    /// the result counts successful acknowledgments. Each send owns an independent scope. Delivery remains at least once.
    /// </summary>
    /// <remarks>
    /// Must run outside an ambient transaction: claims must commit before any external delivery starts.
    /// All passes share this dispatcher's capacity. A timed-out send retains its slot and scope until it actually
    /// finishes; a pass returns without claiming more messages when all slots are occupied.
    /// </remarks>
    public async Task<int> DrainAsync(int maximumMessages = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessages, 1);
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Outbox delivery cannot run inside an ambient transaction; claims must commit before sending.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new List<Task<int>>();
        var remaining = maximumMessages;
        var cursor = long.MinValue;
        var delivered = 0;
        try
        {
            while (remaining > 0 || active.Count > 0)
            {
                stopping.Token.ThrowIfCancellationRequested();
                var available = ReserveDeliverySlots(remaining);
                if (available > 0)
                {
                    var reserved = available;
                    try
                    {
                        // Include the claim round trip in the send budget. Reserve capacity before touching the rows.
                        var started = time.GetTimestamp();
                        var claimed = await ClaimAsync(db, cursor, available, stopping.Token);
                        if (claimed.Count == 0) remaining = 0;
                        else
                        {
                            remaining -= claimed.Count;
                            cursor = claimed.Max(message => message.Sequence);
                            foreach (var message in claimed)
                            {
                                reserved--;
                                active.Add(DeliverAsync(message, started, stopping.Token));
                            }
                        }
                    }
                    finally { if (reserved > 0) _deliverySlots.Release(reserved); }
                }
                if (active.Count == 0) break;
                await Task.WhenAny(active);
                foreach (var completed in active.Where(task => task.IsCompleted).ToArray())
                {
                    delivered += await completed;
                    active.Remove(completed);
                }
            }
            return delivered;
        }
        catch
        {
            // A failed scan or caller cancellation must not leave other delivery scopes running unobserved.
            await stopping.CancelAsync();
            try { await Task.WhenAll(active); }
            catch (Exception) { } // Preserve the original failure; each delivery records its own send failure.
            throw;
        }
    }

    private int ReserveDeliverySlots(int maximum)
    {
        var reserved = 0;
        while (reserved < maximum && _deliverySlots.Wait(0)) reserved++;
        return reserved;
    }

    private async Task<int> DeliverAsync(OutboxMessage candidate, long claimStarted, CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? deadline = null;
        Task<int>? delivery = null;
        try
        {
            var duration = registration.Options.DeliveryLease;
            var margin = TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, duration.Ticks / 10));
            var remaining = duration - margin - time.GetElapsedTime(claimStarted);
            if (remaining <= TimeSpan.Zero) return 0;
            cancellationToken.ThrowIfCancellationRequested();
            timeout = new CancellationTokenSource(remaining, time);
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var token = deadline.Token;
            delivery = SendAndSettleAsync(candidate, token, cancellationToken);
            try { return await delivery.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning("Outbox delivery deadline elapsed for message {MessageId}; its slot remains occupied until the send finishes.", candidate.MessageId);
                return 0;
            }
        }
        finally
        {
            // Waiting can end before sending. The real operation retains its scope, cancellation sources and slot.
            _ = ReleaseDeliverySlotAsync(delivery ?? Task.FromResult(0), timeout, deadline, candidate.MessageId);
        }
    }

    private async Task ReleaseDeliverySlotAsync(Task<int> delivery, CancellationTokenSource? timeout,
        CancellationTokenSource? deadline, string messageId)
    {
        try { await delivery; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { logger.LogError(exception, "Outbox delivery operation failed for message {MessageId}.", messageId); }
        finally
        {
            deadline?.Dispose();
            timeout?.Dispose();
            _deliverySlots.Release();
        }
    }

    private async Task<int> SendAndSettleAsync(OutboxMessage candidate, CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TDbContext>();
        var rows = db.Set<OutboxMessage>();
        var options = registration.Options;
        try
        {
            deadline.ThrowIfCancellationRequested();
            var message = candidate.ToEventMessage();
            Task delivery;
            if (message.Scope == EventSubscriptionScope.Local)
                delivery = services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, deadline);
            else if (message.Scope == EventSubscriptionScope.Distributed)
            {
                var transport = message.Metadata.ServiceKey is { } key
                    ? services.GetRequiredKeyedService<IEventTransport>(key)
                    : services.GetRequiredService<IEventTransport>();
                delivery = transport.SendAsync(message, deadline);
            }
            else throw new InvalidOperationException($"Unknown outbox delivery scope '{message.Scope}'.");
            await delivery;
            deadline.ThrowIfCancellationRequested();
            var acknowledgedAt = time.GetUtcNow().UtcDateTime;
            return await rows.Where(x => x.Sequence == candidate.Sequence && x.LeaseId == candidate.LeaseId
                    && x.DeliveredAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.DeliveredAtUtc, acknowledgedAt)
                    .SetProperty(x => x.LeaseId, (Guid?)null)
                    .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.LastError, (string?)null), deadline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var timedOut = deadline.IsCancellationRequested || exception is OperationCanceledException;
            if (timedOut && !deadline.IsCancellationRequested)
                logger.LogWarning(exception, "Outbox delivery deadline elapsed for message {MessageId}; lease expiry will permit retry.", candidate.MessageId);
            else if (!timedOut)
                logger.LogError(exception, "Outbox delivery failed for message {MessageId}; it remains due for retry.", candidate.MessageId);
            var error = exception.ToString();
            if (error.Length > 1000) error = error[..1000];
            try
            {
                var owned = rows.Where(x => x.Sequence == candidate.Sequence && x.LeaseId == candidate.LeaseId);
                if (timedOut)
                {
                    // A late completion never acknowledges or clears a timed-out claim.
                    await owned.ExecuteUpdateAsync(update => update.SetProperty(x => x.LastError, error), CancellationToken.None);
                }
                else
                {
                    var backoff = Math.Min(options.RetryMaxDelay.TotalMilliseconds,
                        options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(candidate.Attempts - 1, 16)));
                    var jittered = Math.Min(options.RetryMaxDelay.TotalMilliseconds,
                        backoff * (0.8 + Random.Shared.NextDouble() * 0.4));
                    // Retry eligibility must use the same clock as claiming. This extra read is only on failure.
                    var retryClock = UsesNativeClaims(db)
                        ? await db.Database.SqlQueryRaw<DateTime>("SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                            .SingleAsync(CancellationToken.None)
                        : time.GetUtcNow().UtcDateTime;
                    var retryAt = retryClock.AddMilliseconds(jittered);
                    await owned.ExecuteUpdateAsync(update => update.SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                        .SetProperty(x => x.NextAttemptAtUtc, retryAt)
                        .SetProperty(x => x.LastError, error), CancellationToken.None);
                }
            }
            catch (Exception cleanup)
            {
                logger.LogError(cleanup, "Failed to update outbox claim for message {MessageId}; lease expiry will permit retry.", candidate.MessageId);
            }
            return 0;
        }
    }
}
