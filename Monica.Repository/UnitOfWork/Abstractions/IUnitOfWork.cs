using Monica.Core.Execution;
using System.Diagnostics.CodeAnalysis;

namespace Monica.Repository.UnitOfWork.Abstractions;

/// <summary>
/// The active operation's persistence session. The execution boundary owns commit and rollback;
/// the dependency-injection scope owns its contexts. Never share a session between concurrent operations.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Gets the operation identity.</summary>
    Guid Id { get; }

    /// <summary>Flushes staged changes without committing. Throws after failure or completion.</summary>
    Task<int> FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the first failure that required rollback, or null while the transaction is healthy.</summary>
    ExecutionTransactionFailure? FirstFailure { get; }

    /// <summary>Aborts the active operation and immediately throws, retaining the first rollback cause.</summary>
    /// <param name="failure">The explicit reason, or null for a generic abort. Later aborts never overwrite the first cause.</param>
    /// <remarks>
    /// Returned business failures and exceptions caught before the owning boundary do not automatically roll back.
    /// Developers must inspect child outcomes and call this method when continuing would violate the operation's contract.
    /// Catching the abort exception cannot restore commit eligibility. The owning boundary performs database rollback.
    /// </remarks>
    /// <exception cref="TransactionRollbackOnlyException">Always thrown for an active operation.</exception>
    /// <exception cref="InvalidOperationException">There is no active operation, or the operation has ended.</exception>
    [DoesNotReturn]
    void Abort(ExecutionTransactionFailure? failure = null);
}
