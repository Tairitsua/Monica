using Monica.Repository.Entity.Abstractions;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Domains.Ordering.Entities;

/// <summary>
/// Owns the identity and lifecycle invariants of an order.
/// </summary>
public sealed class Order : Entity<Guid>
{
    private readonly object _stateGate = new();
    private OrderStatus _status;
    private DateTimeOffset? _approvedAtUtc;

    private Order(
        Guid id,
        string orderNumber,
        string customerName,
        decimal total,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrderNumber = NormalizeRequired(orderNumber, nameof(orderNumber), 64);
        CustomerName = NormalizeRequired(customerName, nameof(customerName), 160);

        var normalizedTotal = decimal.Round(total, 2, MidpointRounding.ToEven);
        if (normalizedTotal <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(total),
                total,
                "Order total must be greater than zero after currency rounding.");
        }

        Total = normalizedTotal;
        CreatedAtUtc = createdAtUtc;
        _status = OrderStatus.Draft;
    }

    /// <summary>
    /// Gets the business-visible order number.
    /// </summary>
    public string OrderNumber { get; }

    /// <summary>
    /// Gets the customer display name.
    /// </summary>
    public string CustomerName { get; }

    /// <summary>
    /// Gets the order total rounded to two decimal places.
    /// </summary>
    public decimal Total { get; }

    /// <summary>
    /// Gets when the order was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Creates a new draft order with a generated identifier and UTC timestamp.
    /// </summary>
    /// <param name="orderNumber">The unique business-visible order number.</param>
    /// <param name="customerName">The customer display name.</param>
    /// <param name="total">The positive order total.</param>
    /// <returns>The new draft order.</returns>
    public static Order Create(string orderNumber, string customerName, decimal total)
    {
        return new Order(Guid.NewGuid(), orderNumber, customerName, total, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Moves the order from draft to approved exactly once.
    /// </summary>
    /// <param name="approvedAtUtc">The approval timestamp, which cannot precede creation.</param>
    /// <exception cref="InvalidOperationException">Thrown when the order has already been approved.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when approval predates creation.</exception>
    public void Approve(DateTimeOffset approvedAtUtc)
    {
        lock (_stateGate)
        {
            if (_status == OrderStatus.Approved)
            {
                throw new InvalidOperationException($"Order '{OrderNumber}' is already approved.");
            }

            if (approvedAtUtc < CreatedAtUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(approvedAtUtc),
                    approvedAtUtc,
                    "Approval time cannot precede order creation.");
            }

            _status = OrderStatus.Approved;
            _approvedAtUtc = approvedAtUtc;
        }
    }

    /// <summary>
    /// Captures a consistent immutable view of the current order state.
    /// </summary>
    /// <returns>A detached snapshot that role libraries may inspect without retaining mutable entity state.</returns>
    public OrderSnapshot CaptureSnapshot()
    {
        lock (_stateGate)
        {
            return new OrderSnapshot(
                Id,
                OrderNumber,
                CustomerName,
                Total,
                _status,
                CreatedAtUtc,
                _approvedAtUtc);
        }
    }

    /// <inheritdoc />
    public override void AutoSetNewId(bool notSetWhenNotDefault = false)
    {
        if (notSetWhenNotDefault && Id != Guid.Empty)
        {
            return;
        }

        Id = Guid.NewGuid();
    }

    private static string NormalizeRequired(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }
}
