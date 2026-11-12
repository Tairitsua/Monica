using System.Diagnostics.CodeAnalysis;

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

    /// <summary>Aborts the active operation and immediately throws, retaining the first rollback cause.</summary>
    /// <param name="failure">The explicit reason, or null for a generic abort. Later aborts never overwrite the first cause.</param>
    /// <remarks>
    /// Use when a handled business failure must roll back. Returned results and caught exceptions do not
    /// automatically request rollback. Catching the abort exception cannot restore commit eligibility.
    /// The transaction owner performs database rollback as the operation unwinds.
    /// </remarks>
    /// <exception cref="TransactionRollbackOnlyException">Always thrown for an active operation.</exception>
    /// <exception cref="InvalidOperationException">There is no active operation, or the operation has ended.</exception>
    [DoesNotReturn]
    void Abort(ExecutionTransactionFailure? failure = null);
}
