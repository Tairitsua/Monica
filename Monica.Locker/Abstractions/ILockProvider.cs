using Monica.Locker.Models;

namespace Monica.Locker.Abstractions;

/// <summary>
/// Implements provider-specific lock acquisition against normalized lock keys.
/// </summary>
/// <remarks>
/// This abstraction is intended for Monica locker internals and custom provider integrations. Application code should
/// depend on <see cref="IDistributedLock"/> instead of calling providers directly.
/// </remarks>
public interface ILockProvider
{
    /// <summary>
    /// Tries to acquire a lock using a provider-ready key.
    /// </summary>
    /// <param name="normalizedKey">The normalized key produced by Monica locker services.</param>
    /// <param name="options">Effective acquisition options after module defaults have been applied.</param>
    /// <param name="cancellationToken">Cancels the acquisition attempt.</param>
    /// <returns>
    /// A provider-specific lock handle when acquisition succeeds; otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// A successful result transfers handle ownership to the caller. Acquisition errors and cancellation follow the
    /// selected provider's behavior; the Medallion adapter forwards native failures instead of turning them into
    /// contention results. The handle reports ownership loss only when the backend supports observing it.
    /// </remarks>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string normalizedKey,
        LockAcquisitionOptions options,
        CancellationToken cancellationToken = default);
}
