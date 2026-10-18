using Domains.Ordering.Entities;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Roles.Ordering.Utilities;

internal static class UtilsOrderMapping
{
    public static OrderDto ToDto(Order order)
    {
        var snapshot = order.CaptureSnapshot();
        return new OrderDto(
            snapshot.Id,
            snapshot.OrderNumber,
            snapshot.CustomerName,
            snapshot.Total,
            snapshot.Status,
            snapshot.CreatedAtUtc,
            snapshot.ApprovedAtUtc);
    }
}
