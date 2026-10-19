namespace Monica.Locker.Abstractions;

/// <summary>
/// Represents an acquired lock that must be disposed to release the underlying provider resource.
/// </summary>
/// <remarks>
/// The acquiring caller owns this handle and must await its disposal, including after cancellation or lock loss.
/// Release failures propagate from the provider; disposing a handle does not prove that ownership lasted until release.
/// </remarks>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>Gets the provider's signal that ownership was lost before the handle was disposed.</summary>
    /// <remarks>
    /// Read this property while the handle is held. Providers that cannot observe ownership loss return
    /// <see cref="CancellationToken.None"/>; a non-cancelable token is not a guarantee of continued ownership.
    /// Monitoring may incur backend costs. Link this signal with the operation's cancellation token and pass the
    /// resulting token to protected work and commit boundaries. Cancellation cannot fence an already issued external
    /// effect or replace database concurrency conditions.
    /// </remarks>
    CancellationToken HandleLostToken { get; }
}
