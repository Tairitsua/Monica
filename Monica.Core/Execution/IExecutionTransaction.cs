namespace Monica.Core.Execution;

/// <summary>Exposes the current scope's local transaction boundary without a persistence-provider dependency.</summary>
/// <remarks>The transaction owner registers this scoped service. Absence means no managed local transaction.</remarks>
public interface IExecutionTransaction
{
    /// <summary>Gets whether this scope currently owns an active physical transaction.</summary>
    bool IsActive { get; }

    /// <summary>Prevents the operation from committing, including when a nested failure is handled by its caller.</summary>
    void MarkRollbackOnly();
}
