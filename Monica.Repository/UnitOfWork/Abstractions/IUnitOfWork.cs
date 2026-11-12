using Monica.Core.Execution;

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

    /// <summary>Prevents commit and retains the first failure, even when its caller handles it.</summary>
    /// <param name="failure">Failure details, or null to record an explicit rollback request. Later calls never overwrite the first cause.</param>
    /// <remarks>
    /// Records state without throwing, so exception handlers can preserve the original exception and result
    /// boundaries can return a failed outcome. Save callbacks that cannot continue must throw their failure.
    /// The execution boundary owns rollback; this method does not immediately roll back the database transaction.
    /// </remarks>
    void MarkRollbackOnly(ExecutionTransactionFailure? failure = null);
}
