using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.ApplicationRoles;
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
    /// Runs one bounded pass, claiming only available delivery slots. Failed attempts count toward the limit.
    /// The result counts successful acknowledgments. Distributed messages on the same route within a claim
    /// can share a bounded batch. Local deliveries and providers without batch support keep independent scopes.
    /// Delivery remains at least once.
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
        using var admission = _admission.AdmitDelivery();
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
        var sawOrdered = false;
        var delivered = 0;
        try
        {
            while (remaining > 0 || active.Count > 0)
            {
                stopping.Token.ThrowIfCancellationRequested();
                // Completed claims retain their send/acknowledgment budget after role quarantine.
                if (_admission.IsQuarantined) remaining = 0;
                var available = ReserveDeliverySlots(remaining);
                if (available > 0)
                {
                    var reserved = available;
                    try
                    {
                        // Include the claim round trip in the send budget. Reserve capacity before touching the rows.
                        var started = time.GetTimestamp();
                        var claimed = await ClaimAsync(db, cursor, available, eligibleTargets, stopping.Token);
                        if (claimed.Count == 0)
                        {
                            if (active.Count == 0) remaining = 0;
                        }
                        else
                        {
                            remaining -= claimed.Count;
                            // Settling a head may unlock an earlier storage row, including one passed by another batch.
                            if (claimed.Any(row => row.OutboxPosition.HasValue)) sawOrdered = true;
                            cursor = sawOrdered ? long.MinValue : claimed[^1].Sequence;
                            foreach (var batch in CreateDeliveryBatches(claimed, scope.ServiceProvider))
                            {
                                reserved -= batch.Rows.Count;
                                active.Add(DeliverAsync(batch, started, admission.Retain(), stopping.Token));
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

    private readonly record struct DeliveryBatch(ArraySegment<OutboxMessage> Rows, ArraySegment<EventMessage> Messages);

    private static IEnumerable<DeliveryBatch> CreateDeliveryBatches(List<OutboxMessage> candidates, IServiceProvider services)
    {
        if (candidates.Count == 1)
        {
            var row = candidates[0];
            yield return new DeliveryBatch(new[] { row }, new[] { row.ToEventMessage() });
            yield break;
        }

        foreach (var route in candidates.GroupBy(row => (row.Scope, row.ServiceKey, row.TopicName)))
        {
            var rows = route.ToArray();
            var messages = Array.ConvertAll(rows, row => row.ToEventMessage());
            EventBatchLimits? limits = null;
            if (rows.Length > 1 && route.Key.Scope == EventSubscriptionScope.Distributed)
            {
                var transport = route.Key.ServiceKey is { } key
                    ? services.GetKeyedService<IEventTransport>(key) : services.GetService<IEventTransport>();
                limits = (transport as IEventBatchTransport)?.BatchLimits;
            }

            var offset = 0;
            foreach (var count in limits?.GetBatchSizes(messages) ?? Enumerable.Repeat(1, rows.Length))
            {
                yield return new DeliveryBatch(new ArraySegment<OutboxMessage>(rows, offset, count),
                    new ArraySegment<EventMessage>(messages, offset, count));
                offset += count;
            }
        }
    }

    private async Task<int> DeliverAsync(DeliveryBatch batch, long claimStarted, RoleWorkAdmission.Lease admission,
        CancellationToken cancellationToken)
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
            delivery = SendAndSettleAsync(batch, token, cancellationToken);
            try { return await delivery.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning("Outbox delivery deadline elapsed for {Count} message(s), starting with {MessageId}; capacity remains occupied until sending finishes.", batch.Rows.Count, batch.Rows[0].MessageId);
                return 0;
            }
        }
        finally
        {
            // Waiting can end before sending. The real operation retains its scope, cancellation sources and slot.
            _ = ReleaseDeliverySlotAsync(delivery ?? Task.FromResult(0), timeout, deadline, batch.Rows, admission);
        }
    }

    private async Task ReleaseDeliverySlotAsync(Task<int> delivery, CancellationTokenSource? timeout,
        CancellationTokenSource? deadline, IReadOnlyList<OutboxMessage> candidates, RoleWorkAdmission.Lease admission)
    {
        try { await delivery; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { logger.LogError(exception, "Outbox delivery operation failed, starting with message {MessageId}.", candidates[0].MessageId); }
        finally
        {
            deadline?.Dispose();
            timeout?.Dispose();
            _deliverySlots.Release(candidates.Count);
            admission.Dispose();
        }
    }

    private async Task<int> SendAndSettleAsync(DeliveryBatch batch, CancellationToken deadline,
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
            var message = batch.Messages[0];
            if (message.Scope == EventSubscriptionScope.Local)
            {
                await (options.SharedStoreKey is null
                    ? services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, deadline)
                    : services.GetRequiredService<IEventReceiveDispatcher>().DispatchSelectedAsync(message,
                        services.GetRequiredService<OutboxDeliveryCapabilities>().SelectLocal(batch.Rows[0]), deadline));
                results = [EventSendResult.Accepted];
            }
            else if (message.Scope == EventSubscriptionScope.Distributed)
            {
                var transport = message.Metadata.ServiceKey is { } key
                    ? services.GetRequiredKeyedService<IEventTransport>(key)
                    : services.GetRequiredService<IEventTransport>();
                if (batch.Messages.Count == 1)
                {
                    await transport.SendAsync(message, deadline);
                    results = [EventSendResult.Accepted];
                }
                else
                {
                    results = await ((IEventBatchTransport)transport).SendBatchAsync(batch.Messages, deadline);
                    EventTransportBatch.ValidateResults(results, batch.Messages.Count);
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
            var result = batch.Rows.Count > 1 || exception is OperationCanceledException
                ? EventSendResult.Unknown(exception) : EventSendResult.Failed(exception);
            results = Enumerable.Repeat(result, batch.Rows.Count).ToArray();
        }

        return await SettleAsync(db, batch.Rows, results, deadline, cancellationToken);
    }
}
