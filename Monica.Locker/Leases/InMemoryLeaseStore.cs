namespace Monica.Locker.Leases;

/// <summary>Implements the lease protocol within one process; it never coordinates independent hosts.</summary>
/// <param name="timeProvider">Clock used for expiration, including deterministic test clocks.</param>
/// <param name="synchronizationRoot">Optional owner lock shared with protected in-memory state transitions.</param>
public sealed class InMemoryLeaseStore(TimeProvider timeProvider, object? synchronizationRoot = null)
    : IExclusiveLeaseStore, ILeaseAdministration
{
    private readonly object _gate = synchronizationRoot ?? new object();
    private readonly Dictionary<LeaseResource, (LeaseGrant Grant, DateTimeOffset Acquired)> _leases = [];

    /// <inheritdoc />
    public Task<LeaseGrant?> TryAcquireAsync(LeaseResource resource, string holderId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Validate(resource, holderId, duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_leases.TryGetValue(resource, out var current) && current.Grant.ExpiresAtUtc > now)
                return Task.FromResult<LeaseGrant?>(null);
            var grant = new LeaseGrant(new LeaseIdentity(resource, holderId, Guid.NewGuid().ToString("N")), duration, now);
            _leases[resource] = (grant, now);
            return Task.FromResult<LeaseGrant?>(grant);
        }
    }

    /// <inheritdoc />
    public Task<LeaseGrant?> TryRenewAsync(LeaseIdentity identity, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Validate(identity.Resource, identity.HolderId, duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (!_leases.TryGetValue(identity.Resource, out var current)
                || current.Grant.Identity != identity || current.Grant.ExpiresAtUtc <= now)
                return Task.FromResult<LeaseGrant?>(null);
            var grant = new LeaseGrant(identity, duration, now);
            _leases[identity.Resource] = (grant, current.Acquired);
            return Task.FromResult<LeaseGrant?>(grant);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryReleaseAsync(LeaseIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_leases.TryGetValue(identity.Resource, out var current)
                && current.Grant.Identity == identity && _leases.Remove(identity.Resource));
    }

    /// <inheritdoc />
    public Task<bool> RevokeAsync(LeaseResource resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_leases.Remove(resource));
    }

    /// <summary>Checks ownership while the caller holds the shared synchronization root through its entire mutation.</summary>
    public void EnsureHeld(LeaseIdentity identity)
    {
        lock (_gate)
            if (!_leases.TryGetValue(identity.Resource, out var current)
                || current.Grant.Identity != identity || current.Grant.ExpiresAtUtc <= timeProvider.GetUtcNow())
                throw new LeaseLostException(identity);
    }

    /// <inheritdoc />
    public Task<LeaseSnapshot> GetSnapshotAsync(LeaseResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(new LeaseSnapshot(timeProvider.GetUtcNow(),
                _leases.TryGetValue(resource, out var row) ? [ToObservation(row)] : []));
    }

    /// <inheritdoc />
    public Task<LeaseSnapshot> GetSnapshotAsync(string scope, string namePrefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(new LeaseSnapshot(timeProvider.GetUtcNow(), _leases.Values
                .Where(row => row.Grant.Identity.Resource.Scope == scope
                    && row.Grant.Identity.Resource.Name.StartsWith(namePrefix, StringComparison.Ordinal))
                .OrderBy(row => row.Grant.Identity.Resource.Name, StringComparer.Ordinal)
                .Select(ToObservation).ToArray()));
    }

    private static LeaseObservation ToObservation((LeaseGrant Grant, DateTimeOffset Acquired) row) =>
        new(row.Grant.Identity.Resource, row.Grant.Identity.HolderId,
            row.Acquired, row.Grant.ConfirmedAtUtc, row.Grant.ExpiresAtUtc);

    private static void Validate(LeaseResource resource, string holderId, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        if (holderId.Length > 256) throw new ArgumentOutOfRangeException(nameof(holderId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
    }
}
