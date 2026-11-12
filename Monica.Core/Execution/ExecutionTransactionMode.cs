namespace Monica.Core.Execution;

/// <summary>
/// Defines whether an execution boundary participates in Monica's automatic transaction behavior.
/// </summary>
public enum ExecutionTransactionMode
{
    /// <summary>
    /// Skips the automatic outer unit of work, including its save, domain-event drain, and commit.
    /// An enclosing unit of work remains active; independent writes require a fresh scope.
    /// </summary>
    None,

    /// <summary>
    /// Allows the registered unit-of-work behavior to wrap the complete execution boundary, save its changes,
    /// and commit on normal completion regardless of a returned business result. An explicit abort or an
    /// exception escaping the owning boundary rolls back the operation.
    /// </summary>
    Automatic
}
