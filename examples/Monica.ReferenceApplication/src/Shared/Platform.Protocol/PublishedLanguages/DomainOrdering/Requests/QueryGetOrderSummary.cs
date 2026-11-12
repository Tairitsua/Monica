using Monica.Application.Annotations;
using Monica.Application.Abstractions;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

/// <summary>Reads the product overview without requiring the reporting role's runtime resources.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "queries/GetOrderSummary")]
public sealed record QueryGetOrderSummary : IQuery<OrderSummaryDto>;
