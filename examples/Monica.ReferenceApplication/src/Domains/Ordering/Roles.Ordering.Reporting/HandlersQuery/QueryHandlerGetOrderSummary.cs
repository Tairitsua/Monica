using Domains.Ordering.Interfaces;
using Monica.Application.Abstractions;
using Monica.Core.Results;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

namespace Roles.Ordering.Reporting.HandlersQuery;

/// <summary>Reads the overview locally even when this host does not carry the reporting role.</summary>
public sealed class QueryHandlerGetOrderSummary(IRepositoryOrder repository) : ApplicationService<QueryGetOrderSummary, OrderSummaryDto>
{
    /// <inheritdoc />
    public override async Task<Res<OrderSummaryDto>> Handle(QueryGetOrderSummary request, CancellationToken cancellationToken)
    {
        var orders = await repository.ListAsync(cancellationToken);
        return Res.Ok(new OrderSummaryDto(orders.Count, orders.Count(order => order.CaptureSnapshot().Status == OrderStatus.Draft)));
    }
}
