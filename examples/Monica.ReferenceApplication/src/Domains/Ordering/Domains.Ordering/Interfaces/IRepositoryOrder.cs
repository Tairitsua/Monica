using Domains.Ordering.Entities;

namespace Domains.Ordering.Interfaces;

/// <summary>
/// Defines persistence operations required by the ordering use cases.
/// </summary>
public interface IRepositoryOrder
{
    /// <summary>
    /// Adds a new order and rejects duplicate order numbers.
    /// </summary>
    /// <param name="order">The order to add.</param>
    /// <param name="cancellationToken">Cancels the operation before mutation.</param>
    /// <returns>A task representing completion.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the order identifier or number already exists.</exception>
    ValueTask AddAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an order by identifier.
    /// </summary>
    /// <param name="id">The order identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The order, or <see langword="null"/> when it does not exist.</returns>
    ValueTask<Order?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the current state of an existing order.
    /// </summary>
    /// <param name="order">The order to persist.</param>
    /// <param name="cancellationToken">Cancels the operation before mutation.</param>
    /// <returns>A task representing completion.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the order has not been added.</exception>
    ValueTask SaveAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all orders in reverse creation order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A read-only list snapshot.</returns>
    ValueTask<IReadOnlyList<Order>> ListAsync(CancellationToken cancellationToken = default);
}
