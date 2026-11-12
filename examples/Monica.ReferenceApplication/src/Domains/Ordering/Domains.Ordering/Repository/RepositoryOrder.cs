using System.Collections.Concurrent;
using Domains.Ordering.Entities;
using Domains.Ordering.Interfaces;
using Monica.DependencyInjection.Abstractions;

namespace Domains.Ordering.Repository;

/// <summary>
/// Provides a process-local repository for a zero-infrastructure reference experience.
/// </summary>
/// <remarks>
/// Replace this class with an integration-tier persistence provider when durable storage is required.
/// </remarks>
public sealed class RepositoryOrder : IRepositoryOrder, ISingletonDependency
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();
    private readonly ConcurrentDictionary<string, Guid> _orderIdsByNumber = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes the repository with sample data so the first GET request is useful.
    /// </summary>
    public RepositoryOrder()
    {
        AddSeed(Order.Create("MON-1001", "Northwind Coffee", 1840.50m));
        AddSeed(Order.Create("MON-1002", "Contoso Studio", 725m));
    }

    /// <inheritdoc />
    public ValueTask AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_orderIdsByNumber.TryAdd(order.OrderNumber, order.Id))
        {
            throw new InvalidOperationException($"Order number '{order.OrderNumber}' already exists.");
        }

        if (_orders.TryAdd(order.Id, order))
        {
            return ValueTask.CompletedTask;
        }

        _orderIdsByNumber.TryRemove(order.OrderNumber, out _);
        throw new InvalidOperationException($"Order identifier '{order.Id}' already exists.");
    }

    /// <inheritdoc />
    public ValueTask<Order?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _orders.TryGetValue(id, out var order);
        return ValueTask.FromResult(order);
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_orders.ContainsKey(order.Id))
        {
            throw new KeyNotFoundException($"Order '{order.Id}' has not been added.");
        }

        _orders[order.Id] = order;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Order>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Order> orders = _orders.Values
            .OrderByDescending(static order => order.CreatedAtUtc)
            .ToArray();
        return ValueTask.FromResult(orders);
    }

    private void AddSeed(Order order)
    {
        _orders[order.Id] = order;
        _orderIdsByNumber[order.OrderNumber] = order.Id;
    }
}
