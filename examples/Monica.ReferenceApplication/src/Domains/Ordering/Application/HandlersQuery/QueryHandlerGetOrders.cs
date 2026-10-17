using Domains.Ordering.Configurations;
using Domains.Ordering.Interfaces;
using Domains.Ordering.Utilities;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

namespace Domains.Ordering.Application.HandlersQuery;

/// <summary>
/// Returns the current order collection as stable contract models.
/// </summary>
public sealed class QueryHandlerGetOrders(
    IRepositoryOrder repository,
    IOptions<OrderingOptions> options)
    : ApplicationService<QueryGetOrders, IReadOnlyList<OrderDto>>
{
    /// <summary>
    /// Gets all known orders in reverse creation order.
    /// </summary>
    /// <param name="request">The list request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The immutable order view.</returns>
    public override async Task<Res<IReadOnlyList<OrderDto>>> Handle(
        QueryGetOrders request,
        CancellationToken cancellationToken)
    {
        var orders = await repository.ListAsync(cancellationToken);
        IReadOnlyList<OrderDto> response = orders
            .Take(options.Value.MaximumOrdersReturned)
            .Select(UtilsOrderMapping.ToDto)
            .ToArray();
        return Res.Ok<IReadOnlyList<OrderDto>>(response);
    }
}
