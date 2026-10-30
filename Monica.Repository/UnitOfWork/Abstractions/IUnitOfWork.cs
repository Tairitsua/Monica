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

    /// <summary>Prevents commit, including when a nested failure was caught by its caller.</summary>
    /// <remarks>
    /// Records failure without throwing or immediately rolling back. The operation owner performs rollback.
    /// Save callbacks must throw when they cannot continue; a callback that only marks rollback is rejected
    /// with InvalidOperationException when its EF flush returns. Catching the rejection does not restore health.
    /// </remarks>
    void MarkRollbackOnly();
}
