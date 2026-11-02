using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

public sealed partial class OutboxDispatcher<TDbContext>
{
    private readonly SemaphoreSlim _deliverySlots = new(registration.Options.DeliveryConcurrency,
        registration.Options.DeliveryConcurrency);

    /// <summary>
    /// Runs one bounded pass, claiming only available delivery slots. Failed attempts count toward the limit;
    /// the result counts successful acknowledgments. Compatible distributed messages use bounded transport batches;
    /// local deliveries and providers without batch support keep independent scopes. Delivery remains at least once.
    /// </summary>
    /// <remarks>
    /// Must run outside an ambient transaction: claims must commit before any external delivery starts.
    /// All passes share this dispatcher's capacity. A timed-out send retains its slot and scope until it actually
    /// finishes; a pass returns without claiming more messages when all slots are occupied. Accepted messages use
    /// bounded EF conditional updates; a native bulk-insert provider is not required for acknowledgment.
    /// </remarks>
    public async Task<int> DrainAsync(int maximumMessages = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessages, 1);
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Outbox delivery cannot run inside an ambient transaction; claims must commit before sending.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var eligibleTargets = registration.Options.SharedStoreKey is null ? null
            : scope.ServiceProvider.GetRequiredService<OutboxDeliveryCapabilities>().GetEligibleTargets();
        if (eligibleTargets is { Length: 0 }) return 0;
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
                        var claimed = await ClaimAsync(db, cursor, available, eligibleTargets, stopping.Token);
                        if (claimed.Count == 0) remaining = 0;
                        else
                        {
                            remaining -= claimed.Count;
                            cursor = claimed.Max(message => message.Sequence);
                            foreach (var batch in CreateDeliveryBatches(claimed, scope.ServiceProvider))
                            {
                                reserved -= batch.Length;
                                active.Add(DeliverAsync(batch, started, stopping.Token));
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

    private static IEnumerable<OutboxMessage[]> CreateDeliveryBatches(List<OutboxMessage> candidates, IServiceProvider services)
    {
        var messages = candidates.Select(candidate => candidate.ToEventMessage()).ToArray();
        var offset = 0;
        while (offset < messages.Length)
        {
            var message = messages[offset];
            var count = 1;
            if (message.Scope == EventSubscriptionScope.Distributed && offset + 1 < messages.Length)
            {
                var transport = message.Metadata.ServiceKey is { } key
                    ? services.GetKeyedService<IEventTransport>(key) : services.GetService<IEventTransport>();
                if (transport is IEventBatchTransport batchTransport)
                    count = batchTransport.BatchLimits.GetBatchSizes(
                        new ArraySegment<EventMessage>(messages, offset, messages.Length - offset)).First();
            }
            yield return candidates.GetRange(offset, count).ToArray();
            offset += count;
        }
    }

    private async Task<int> DeliverAsync(IReadOnlyList<OutboxMessage> candidates, long claimStarted, CancellationToken cancellationToken)
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
            delivery = SendAndSettleAsync(candidates, token, cancellationToken);
            try { return await delivery.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning("Outbox delivery deadline elapsed for {Count} message(s), starting with {MessageId}; capacity remains occupied until sending finishes.", candidates.Count, candidates[0].MessageId);
                return 0;
            }
        }
        finally
        {
            // Waiting can end before sending. The real operation retains its scope, cancellation sources and slot.
            _ = ReleaseDeliverySlotAsync(delivery ?? Task.FromResult(0), timeout, deadline, candidates);
        }
    }

    private async Task ReleaseDeliverySlotAsync(Task<int> delivery, CancellationTokenSource? timeout,
        CancellationTokenSource? deadline, IReadOnlyList<OutboxMessage> candidates)
    {
        try { await delivery; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { logger.LogError(exception, "Outbox delivery operation failed, starting with message {MessageId}.", candidates[0].MessageId); }
        finally
        {
            deadline?.Dispose();
            timeout?.Dispose();
            _deliverySlots.Release(candidates.Count);
        }
    }

    private async Task<int> SendAndSettleAsync(IReadOnlyList<OutboxMessage> candidates, CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TDbContext>();
        var options = registration.Options;
        IReadOnlyList<EventSendResult> results;
        try
        {
            deadline.ThrowIfCancellationRequested();
            var candidate = candidates[0];
            var message = candidate.ToEventMessage();
            if (message.Scope == EventSubscriptionScope.Local)
            {
                await (options.SharedStoreKey is null
                    ? services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, deadline)
                    : services.GetRequiredService<IEventReceiveDispatcher>().DispatchSelectedAsync(message,
                        services.GetRequiredService<OutboxDeliveryCapabilities>().SelectLocal(candidate), deadline));
                results = [EventSendResult.Accepted];
            }
            else if (message.Scope == EventSubscriptionScope.Distributed)
            {
                var transport = message.Metadata.ServiceKey is { } key
                    ? services.GetRequiredKeyedService<IEventTransport>(key)
                    : services.GetRequiredService<IEventTransport>();
                if (candidates.Count == 1)
                {
                    await transport.SendAsync(message, deadline);
                    results = [EventSendResult.Accepted];
                }
                else
                {
                    results = await ((IEventBatchTransport)transport).SendBatchAsync(
                        candidates.Select(row => row.ToEventMessage()).ToArray(), deadline);
                    EventTransportBatch.ValidateResults(results, candidates.Count);
                }
            }
            else throw new InvalidOperationException($"Unknown outbox delivery scope '{message.Scope}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A thrown batch call has no trustworthy per-entry outcome. Keep those claims until expiry.
            var result = candidates.Count > 1 || exception is OperationCanceledException
                ? EventSendResult.Unknown(exception) : EventSendResult.Failed(exception);
            results = Enumerable.Repeat(result, candidates.Count).ToArray();
        }

        return await SettleAsync(db, candidates, results, deadline, cancellationToken);
    }
}
