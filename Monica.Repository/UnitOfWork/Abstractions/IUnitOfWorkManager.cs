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

    /// <summary>Runs work and commits on success. Failures roll back with an uncanceled cleanup token.</summary>
    /// <remarks>Nested options may enlist compatible contexts; they cannot replace active isolation or command timeout.</remarks>
    Task RunAsync(Func<Task> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs work and returns its result. Failed Monica result envelopes and execution outcomes roll back.
    /// Completion or failure makes this scope terminal; retry in a fresh scope.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default);
}
