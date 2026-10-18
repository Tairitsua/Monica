using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Domains.Ordering.Entities;

/// <summary>Contains a consistent immutable view of an order for its owning domain's application roles.</summary>
/// <param name="Id">The order identity.</param>
/// <param name="OrderNumber">The business-visible order number.</param>
/// <param name="CustomerName">The customer display name.</param>
/// <param name="Total">The order total rounded to two decimal places.</param>
/// <param name="Status">The lifecycle status captured with the approval timestamp.</param>
/// <param name="CreatedAtUtc">The creation instant.</param>
/// <param name="ApprovedAtUtc">The approval instant, or null while the order is a draft.</param>
public sealed record OrderSnapshot(
    Guid Id,
    string OrderNumber,
    string CustomerName,
    decimal Total,
    OrderStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc);
