using Monica.Repository.UnitOfWork.Models;

namespace Monica.Repository.UnitOfWork.Abstractions;

/// <summary>
/// Runs a single write operation in the current DI scope. Nested calls join it; an independent operation
/// must resolve a new manager and all its dependencies from a fresh scope. The manager exposes its borrowed scope
/// to nested API calls but never creates or disposes that scope.
/// </summary>
public interface IUnitOfWorkManager
{
    /// <summary>Gets the active session, or null outside execution.</summary>
    IUnitOfWork? Current { get; }

    /// <summary>Runs work and commits after normal completion; an explicit abort or escaping exception rolls back.</summary>
    /// <remarks>
    /// Only the outermost call owns completion. Exceptions caught by its delegate, including nested exceptions,
    /// do not automatically request rollback. Nested options may enlist compatible contexts but cannot replace
    /// active isolation or command timeout. Rollback uses an uncanceled cleanup token.
    /// </remarks>
    Task RunAsync(Func<Task> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs work and commits on normal completion regardless of the returned business result or status.
    /// An exception escaping the outermost operation or an explicit <see cref="IUnitOfWork.Abort"/> rolls back.
    /// Completion or failure makes this scope terminal; retry in a fresh scope.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default);
}
