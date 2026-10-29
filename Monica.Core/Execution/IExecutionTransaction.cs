namespace Monica.Core.Execution;

/// <summary>Exposes the current scope's local transaction boundary without a persistence-provider dependency.</summary>
/// <remarks>The transaction owner registers this scoped service. Absence means no managed local transaction.</remarks>
public interface IExecutionTransaction
{
    /// <summary>Gets whether this scope currently owns an active physical transaction.</summary>
    bool IsActive { get; }

    /// <summary>Gets the first failure that required rollback, or null while the transaction is healthy.</summary>
    ExecutionTransactionFailure? FirstFailure { get; }

    /// <summary>Prevents commit and retains the first failure, even when its caller handles it.</summary>
    /// <param name="failure">Failure details, or null to record an explicit rollback request. Later calls never overwrite the first cause.</param>
    void MarkRollbackOnly(ExecutionTransactionFailure? failure = null);
}
