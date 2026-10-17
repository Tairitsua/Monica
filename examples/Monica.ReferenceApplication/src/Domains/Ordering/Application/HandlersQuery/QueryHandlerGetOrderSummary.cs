using Domains.Ordering.Interfaces;
using Monica.Application.Abstractions;
using Monica.Core.Execution;
using Monica.Core.Results;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

namespace Domains.Ordering.Application.HandlersQuery;

/// <summary>Executes a local-only application query through the normal mediator pipeline.</summary>
public sealed class QueryHandlerGetOrderSummary(IRepositoryOrder repository) : ApplicationService<QueryGetOrderSummary, OrderSummaryDto>
{
    /// <inheritdoc />
    public override async Task<Res<OrderSummaryDto>> Handle(QueryGetOrderSummary request, CancellationToken cancellationToken)
    {
        var orders = await repository.ListAsync(cancellationToken);
        return Res.Ok(new OrderSummaryDto(orders.Count, orders.Count(order => order.CaptureSnapshot().Status == OrderStatus.Draft)));
    }
}
