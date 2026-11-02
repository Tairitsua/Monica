using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services.Support;

/// <summary>Shared preparation-independent batching for transport providers.</summary>
public static class EventTransportBatch
{
    /// <summary>
    /// Sends bounded route-compatible groups in input order. Stops after the first unsuccessful group,
    /// throwing its original failures as an AggregateException; earlier groups and successful neighbors
    /// may already have been accepted. Use Outbox when automatic retries must preserve message identities.
    /// </summary>
    public static async Task PublishAsync(IEventBatchTransport transport, IReadOnlyList<EventMessage> messages,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        foreach (var count in transport.BatchLimits.GetBatchSizes(messages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new EventMessage[count];
            for (var index = 0; index < count; index++) batch[index] = messages[offset + index];
            if (count == 1)
                await transport.SendAsync(batch[0], cancellationToken);
            else
            {
                var results = await transport.SendBatchAsync(batch, cancellationToken);
                ValidateResults(results, count);
                cancellationToken.ThrowIfCancellationRequested();
                var errors = results.Where(result => result.Status != EventSendStatus.Accepted)
                    .Select(result => result.Error!).ToArray();
                if (errors.Length > 0)
                    throw new AggregateException("Some events were not acknowledged; successful neighbors may already have been published.", errors);
            }
            offset += count;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Rejects incomplete provider results so callers cannot acknowledge unreported messages.</summary>
    public static void ValidateResults(IReadOnlyList<EventSendResult> results, int count)
    {
        if (results is null || results.Count != count || results.Any(result => result is null))
            throw new InvalidOperationException("A batch transport must return one non-null result per input, in input order.");
    }
}
