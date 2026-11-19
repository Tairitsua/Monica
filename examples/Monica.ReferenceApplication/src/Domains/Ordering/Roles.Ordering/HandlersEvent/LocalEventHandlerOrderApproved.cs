using Microsoft.Extensions.Logging;
using Monica.ApplicationModel.Annotations;
using Monica.EventBus.Abstractions.Handlers;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Events;

namespace Roles.Ordering.HandlersEvent;

/// <summary>
/// Records approvals delivered by the local event bus.
/// </summary>
[BuildingBlockMetadata("Record Order Approval",
    Description = "Logs the order identity and time after the reference application records its approval.")]
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
