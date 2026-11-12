using Monica.Locker.Models;

namespace Monica.Locker.Abstractions;

/// <summary>
/// Exposes the public lock acquisition entry point for Monica locker consumers.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Tries to acquire a named lock and returns a handle when successful.
    /// </summary>
    /// <param name="resourceName">The logical resource name before provider-specific key normalization is applied.</param>
    /// <param name="options">Optional acquisition settings such as owner, wait timeout, and lease duration.</param>
    /// <param name="cancellationToken">Cancels the acquisition attempt.</param>
    /// <returns>
    /// A caller-owned lock handle when acquisition succeeds, or <see langword="null"/> when no lock is obtained
    /// within the provider's acquisition deadline.
    /// </returns>
    /// <remarks>
    /// Acquisition cancellation and backend failures retain the selected provider's behavior. The Medallion adapter
    /// propagates cancellation as <see cref="OperationCanceledException"/>; Dapr can return <see langword="null"/>
    /// when its polling is canceled. Do not treat every exception as lock contention. The acquisition token does not
    /// automatically cancel later protected work: observe the acquired handle's
    /// <see cref="IDistributedLockHandle.HandleLostToken"/> and supply an operation token explicitly.
    /// </remarks>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string resourceName,
        LockAcquisitionOptions? options = null,
        CancellationToken cancellationToken = default);
}
