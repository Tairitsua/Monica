using Monica.Application.Annotations;
using Monica.Application.Abstractions;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

/// <summary>Reads the product overview locally without publishing an HTTP endpoint.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "queries/GetOrderSummary")]
public sealed record QueryGetOrderSummary : IResultRequest<OrderSummaryDto>;
