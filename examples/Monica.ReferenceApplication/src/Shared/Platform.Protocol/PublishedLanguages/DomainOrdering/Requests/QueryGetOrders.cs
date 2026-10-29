using Monica.Application.Abstractions;
using Monica.Application.Annotations;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

/// <summary>
/// Requests all orders in reverse creation order. The published Query convention marks this request read-only,
/// so mediated dispatch opens no automatic write transaction.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Post, "queries/GetOrders")]
public sealed record QueryGetOrders : IQuery<IReadOnlyList<OrderDto>>;
