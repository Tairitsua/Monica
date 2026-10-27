namespace Monica.Locker.Leases;

/// <summary>Owns atomic, expiring role grants in one explicitly selected storage authority.</summary>
/// <remarks>
/// Null/false represents contention or a definitive ownership mismatch. Cancellation and storage failures propagate;
/// an unknown write outcome must not be reported as contention. The store does not run protected business work.
/// </remarks>
public interface IExclusiveLeaseStore
{
    /// <summary>Attempts an immediate acquisition with a fresh term; an active grant cannot be reacquired.</summary>
    Task<LeaseGrant?> TryAcquireAsync(LeaseResource resource, string holderId, TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Extends exactly the current unexpired term; never reacquires an expired grant.</summary>
    Task<LeaseGrant?> TryRenewAsync(LeaseIdentity identity, TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Releases only the specified term. Call after protected work has drained.</summary>
    Task<bool> TryReleaseAsync(LeaseIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Reads retained roles in one scope and module prefix without acquiring or renewing them.</summary>
    /// <remarks>Intended for low-cardinality coordination roles, not per-message or per-execution inventories.</remarks>
    Task<LeaseSnapshot> GetSnapshotAsync(string scope, string namePrefix,
        CancellationToken cancellationToken = default);
}
