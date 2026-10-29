using Monica.Repository.Snapshots.Models;

namespace Monica.Repository.Snapshots.Abstractions;

/// <summary>Scoped access to a host-owned immutable projection. Never use it to read the current operation's changes.</summary>
public interface IRepositorySnapshot<T> where T : class
{
    /// <summary>Validates the primary database on every call. Throws when freshness cannot be established.</summary>
    Task<SnapshotRead<T>> ReadFreshAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns validation within the positive supplied age, checked at return. Slow validation can exceed this budget and throw. Cancellation never returns a fallback value.</summary>
    Task<SnapshotRead<T>> ReadBoundedAsync(TimeSpan maxValidationAge, CancellationToken cancellationToken = default);
}

/// <summary>Scoped access to parameterized immutable projections. Inputs must be immutable, explicitly versioned values.</summary>
public interface IRepositorySnapshot<T, in TInput> where T : class where TInput : notnull
{
    /// <summary>Validates the primary database and obtains the exact projection/input identity.</summary>
    Task<SnapshotRead<T>> ReadFreshAsync(TInput input, CancellationToken cancellationToken = default);
    /// <summary>Returns matching input validation within the positive supplied age, checked at return. Throws if validation itself exceeds the budget.</summary>
    Task<SnapshotRead<T>> ReadBoundedAsync(TInput input, TimeSpan maxValidationAge, CancellationToken cancellationToken = default);
}
