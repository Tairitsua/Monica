using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Services.Support;

internal static class JobCoordinatorLeaseAdapter
{
    internal const string PREFIX = "jobscheduler:";

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
