namespace Monica.Locker.Models;

/// <summary>
/// Describes how a lock should be acquired.
/// </summary>
/// <remarks>
/// Any <see langword="null"/> value allows the locker module or the active provider to apply its default behavior.
/// </remarks>
public sealed class LockAcquisitionOptions
{
    /// <summary>
    /// Optional logical owner identifier recorded by providers that support ownership metadata.
    /// </summary>
    /// <remarks>The Medallion and in-process adapters ignore this value; it does not alter the shared resource key.</remarks>
    public string? Owner { get; init; }

    /// <summary>
    /// Maximum time to wait for a lock before returning <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This bounds acquisition, not the protected operation. Cancellation and backend failures may throw instead of
    /// returning null. When omitted, the Locker module's default wait timeout applies.
    /// </remarks>
    public TimeSpan? WaitTimeout { get; init; }

    /// <summary>
    /// Requested lease duration for providers that support expiring locks automatically.
    /// </summary>
    /// <remarks>
    /// The Medallion and in-process adapters ignore this value. Configure a Medallion backend's expiry and renewal
    /// through its native options. Dapr uses a fixed lease without renewal or ownership-loss observation.
    /// This value does not impose an execution timeout on protected work.
    /// </remarks>
    public TimeSpan? LeaseDuration { get; init; }
}
