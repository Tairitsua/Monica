using Microsoft.EntityFrameworkCore;

namespace Monica.Repository.Bulk.Abstractions;

/// <summary>Provider extension point for native insertion. Applications execute through <see cref="IBulkWriter"/>.</summary>
/// <remarks>
/// Implementations are singleton, thread-safe across independent contexts, and must borrow the supplied
/// connection and transaction. They must not save, commit, attach rows, traverse graphs, or generate events.
/// A failed attempt propagates its original exception; the shared writer owns rollback-only propagation.
/// </remarks>
public interface IBulkInsertProvider
{
    /// <summary>Rejects unsupported providers, mappings, or row values without I/O or changing inputs.</summary>
    void ValidateInsert<TRow>(DbContext context, IReadOnlyList<TRow> rows) where TRow : class;

    /// <summary>Writes all prepared rows synchronously in the supplied transaction, without hydrating generated values.</summary>
    void Insert<TRow>(DbContext context, IReadOnlyList<TRow> rows, CancellationToken cancellationToken) where TRow : class;

    /// <summary>Writes all prepared rows asynchronously in the supplied transaction, without hydrating generated values.</summary>
    Task InsertAsync<TRow>(DbContext context, IReadOnlyList<TRow> rows, CancellationToken cancellationToken) where TRow : class;
}
