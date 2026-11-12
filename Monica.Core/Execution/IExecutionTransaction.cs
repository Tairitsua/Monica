namespace Monica.Core.Execution;

/// <summary>Exposes the current scope's logical unit of work and physical transaction without a persistence-provider dependency.</summary>
/// <remarks>The transaction owner registers this scoped service. Absence means no managed unit of work.</remarks>
public interface IExecutionTransaction
{
    /// <summary>Gets whether this scope has an active logical unit of work that nested local calls must join.</summary>
    /// <remarks>
    /// A unit of work can be active before it enlists a database. Rollback-only work remains active until its owner
    /// finishes; it must never be mistaken for a new independent operation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The scope's unit of work has already completed or failed.</exception>
    bool IsOperationActive { get; }

    /// <summary>Gets whether this scope currently owns an active physical transaction.</summary>
    bool IsActive { get; }

    /// <summary>Gets the first failure that required rollback, or null while the transaction is healthy.</summary>
    ExecutionTransactionFailure? FirstFailure { get; }

    /// <summary>Prevents commit and retains the first failure, even when its caller handles it.</summary>
    /// <param name="failure">Failure details, or null to record an explicit rollback request. Later calls never overwrite the first cause.</param>
    /// <remarks>
    /// Records state without throwing, so exception handlers can preserve the original exception and result
    /// boundaries can return a failed outcome. Code that cannot continue must still propagate its failure.
    /// The transaction owner performs rollback when the operation ends.
    /// </remarks>
    void MarkRollbackOnly(ExecutionTransactionFailure? failure = null);
}
