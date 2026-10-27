using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Services.Support;

internal static class JobCoordinatorLeaseAdapter
{
    internal const string PREFIX = "jobscheduler:";

    internal static async Task<JobCoordinatorLease?> AcquireAsync(IExclusiveLeaseStore store, string scope,
        string partition, string holder, TimeSpan duration, string? currentToken, CancellationToken token)
    {
        JobSchedulerIdentity.ValidateStandard(scope, nameof(scope));
        JobSchedulerIdentity.ValidateStandard(holder, nameof(holder));
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (partition.Length > 256) throw new ArgumentOutOfRangeException(nameof(partition));
        var resource = new LeaseResource(scope, PREFIX + partition);
        var grant = currentToken is null ? null : await store.TryRenewAsync(new LeaseIdentity(resource, holder, currentToken), duration, token);
        grant ??= await store.TryAcquireAsync(resource, holder, duration, token);
        return grant is null ? null : new JobCoordinatorLease
        {
            Key = ToKey(grant.Identity), GrantedAtUtc = grant.ConfirmedAtUtc, ExpiresAtUtc = grant.ExpiresAtUtc
        };
    }

    internal static JobCoordinatorLeaseKey ToKey(LeaseIdentity identity) => new()
    {
        SchedulerScopeKey = identity.Resource.Scope, CoordinatorKey = identity.Resource.Name[PREFIX.Length..],
        WorkerInstanceId = identity.HolderId, LeaseToken = identity.TermId
    };

    internal static async Task<JobCoordinatorSnapshot> SnapshotAsync(IExclusiveLeaseStore store, string scope, CancellationToken token)
    {
        JobSchedulerIdentity.ValidateStandard(scope, nameof(scope));
        var snapshot = await store.GetSnapshotAsync(scope, PREFIX, token);
        return new JobCoordinatorSnapshot
        {
            ObservedAtUtc = snapshot.ObservedAtUtc,
            Coordinators = snapshot.Leases.Select(row => new JobCoordinatorSummary
            {
                CoordinatorKey = row.Resource.Name[PREFIX.Length..], WorkerInstanceId = row.HolderId, ExpiresAtUtc = row.ExpiresAtUtc
            }).ToArray()
        };
    }
}
