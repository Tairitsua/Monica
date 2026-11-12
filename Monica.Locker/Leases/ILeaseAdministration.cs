namespace Monica.Locker.Leases;

/// <summary>Optional operator capability for forcibly revoking a role, independent of its current holder.</summary>
/// <remarks>
/// This is not normal release. Existing work observes revocation only on renewal or its local deadline and may
/// still be running when another holder acquires. Use only for an explicit administrative action; callers own
/// authorization. Workers must use <see cref="IExclusiveLeaseStore.TryReleaseAsync"/> with their exact identity.
/// </remarks>
public interface ILeaseAdministration
{
    /// <summary>Deletes the current grant, if any. Returns false when the resource was absent.</summary>
    Task<bool> RevokeAsync(LeaseResource resource, CancellationToken cancellationToken = default);
}
