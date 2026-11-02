using Microsoft.EntityFrameworkCore;
using Monica.Repository.Bulk.Services;

namespace Monica.Repository.Bulk.Models;

/// <summary>Immutable configuration for one native insert; context and input rows are borrowed.</summary>
/// <remarks>
/// Execution requires a registered native provider and an active managed transaction. Empty input is a no-op
/// after cancellation validation. No operation commits, tracks input rows, or automatically generates entity events.
/// </remarks>
public sealed class BulkInsertOperation<TRow> where TRow : class
{
    private readonly DbContext _context;
    private readonly IReadOnlyCollection<TRow> _rows;
    private readonly bool _audit;

    internal BulkInsertOperation(DbContext context, IReadOnlyCollection<TRow> rows, bool audit = false)
        => (_context, _rows, _audit) = (context, rows, audit);

    /// <summary>
    /// Returns an operation that applies the context's creation-audit field conventions before insertion.
    /// Existing creation metadata is preserved according to IAuditPropertySetter. This does not enable audit logs,
    /// concurrency policies, synchronization metadata, soft deletion, or entity events.
    /// </summary>
    public BulkInsertOperation<TRow> WithAuditProperties() => new(_context, _rows, true);

    /// <summary>Executes synchronously. Success means written in the current transaction, not committed.</summary>
    public void Execute(CancellationToken cancellationToken = default)
        => BulkWriter.ExecuteAsync(_context, _rows, _audit, false, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Executes asynchronously. Execution failures mark the owning transaction rollback-only, even if caught.</summary>
    public Task ExecuteAsync(CancellationToken cancellationToken = default)
        => BulkWriter.ExecuteAsync(_context, _rows, _audit, true, cancellationToken);
}
