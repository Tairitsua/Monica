using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;

namespace Monica.JobScheduler.Providers;

public sealed partial class InMemoryJobSchedulerStore
{
    private readonly Dictionary<(string Scope, string Key), JobCoordinatorLease> _coordinators = [];
    private readonly Dictionary<(string Scope, string Owner), (string Hash, DateTimeOffset Observed, DateTimeOffset Published)> _owners = [];

    /// <inheritdoc />
    public Task<JobCoordinatorSnapshot> GetCoordinatorSnapshotAsync(string schedulerScopeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(new JobCoordinatorSnapshot
            {
                ObservedAtUtc = UtcNow,
                Coordinators = _coordinators.Where(item => item.Key.Scope == schedulerScopeKey)
                    .OrderBy(item => item.Key.Key, StringComparer.Ordinal)
                    .Select(item => new JobCoordinatorSummary
                    {
                        CoordinatorKey = item.Key.Key,
                        WorkerInstanceId = item.Value.Key.WorkerInstanceId,
                        ExpiresAtUtc = item.Value.ExpiresAtUtc
                    }).ToArray()
            });
        }
    }

    /// <inheritdoc />
    public Task<JobCoordinatorLease?> TryAcquireCoordinatorLeaseAsync(string schedulerScopeKey, string coordinatorKey,
        string workerInstanceId, TimeSpan leaseDuration, string? currentToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(workerInstanceId, nameof(workerInstanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorKey);
        if (coordinatorKey.Length > 256) throw new ArgumentOutOfRangeException(nameof(coordinatorKey));
        ValidatePositiveDuration(leaseDuration, nameof(leaseDuration));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = UtcNow;
            var identity = (schedulerScopeKey, coordinatorKey);
            _coordinators.TryGetValue(identity, out var current);
            if (current is not null && current.ExpiresAtUtc > now
                && (current.Key.WorkerInstanceId != workerInstanceId || current.Key.LeaseToken != currentToken))
                return Task.FromResult<JobCoordinatorLease?>(null);
            var grant = new JobCoordinatorLease
            {
                Key = new JobCoordinatorLeaseKey
                {
                    SchedulerScopeKey = schedulerScopeKey, CoordinatorKey = coordinatorKey, WorkerInstanceId = workerInstanceId,
                    LeaseToken = current is not null && current.ExpiresAtUtc > now ? current.Key.LeaseToken : Guid.NewGuid().ToString("N")
                },
                GrantedAtUtc = now, ExpiresAtUtc = now.Add(leaseDuration)
            };
            _coordinators[identity] = grant;
            return Task.FromResult<JobCoordinatorLease?>(grant);
        }
    }

    private void ValidateCoordinatorLeaseUnsafe(JobCoordinatorLeaseKey? key)
    {
        if (key is not null && (!_coordinators.TryGetValue((key.SchedulerScopeKey, key.CoordinatorKey), out var current)
            || current.Key != key || current.ExpiresAtUtc <= UtcNow))
            throw new InvalidOperationException("The scheduling coordinator lease is no longer current.");
    }

    /// <inheritdoc />
    public Task<string?> GetOwnerManifestHashAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_owners.TryGetValue((schedulerScopeKey, ownerKey), out var owner) ? owner.Hash : null);
        }
    }
}
