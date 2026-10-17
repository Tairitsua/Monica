using Microsoft.Extensions.Logging;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Events;

namespace Domains.Ordering.Application.HandlersEvent;

/// <summary>
/// Records the in-process reaction to an approval in the demo's process-local repository.
/// </summary>
public sealed class LocalEventHandlerOrderApproved : LocalEventHandler<EventOrderApproved>
{
    /// <inheritdoc />
    public override Task HandleEventAsync(
        EventOrderApproved eventData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logger.LogInformation(
            "Observed approval for order {OrderNumber} ({OrderId}) at {ApprovedAtUtc}.",
            eventData.OrderNumber,
            eventData.OrderId,
            eventData.ApprovedAtUtc);

        return Task.CompletedTask;
    }
}
