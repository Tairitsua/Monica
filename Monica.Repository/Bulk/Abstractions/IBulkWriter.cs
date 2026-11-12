using Microsoft.EntityFrameworkCore;
using Monica.Repository.Bulk.Models;

namespace Monica.Repository.Bulk.Abstractions;

/// <summary>Creates explicit native insert operations for mapped rows in a managed repository transaction.</summary>
/// <remarks>Rows need not implement IEntity. This service does not load aggregates or own a context or transaction.</remarks>
public interface IBulkWriter
{
    /// <summary>Describes an insert without executing database I/O. Audit stamping is disabled by default.</summary>
    /// <remarks>
    /// Execute inside the supplied physical context's managed UnitOfWork. Input rows remain caller-owned;
    /// do not mutate them or use the context concurrently during execution. Generated values are not hydrated.
    /// </remarks>
    BulkInsertOperation<TRow> Insert<TRow>(DbContext context, IReadOnlyCollection<TRow> rows) where TRow : class;
}
