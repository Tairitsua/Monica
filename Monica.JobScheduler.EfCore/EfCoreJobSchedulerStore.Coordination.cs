using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.Locker.EfCore;
using Monica.Locker.Leases;
using Monica.JobScheduler.Services.Support;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    private RelationalLeaseStore<JobSchedulerDbContext>? _coordinatorLeases;
    private RelationalLeaseStore<JobSchedulerDbContext> RelationalLeases =>
        LazyInitializer.EnsureInitialized(ref _coordinatorLeases, () => new(_dbContextFactory, _timeProvider, _operationTimeout));

    /// <inheritdoc />
    public IExclusiveLeaseStore CoordinatorLeases => this;

    Task<LeaseGrant?> IExclusiveLeaseStore.TryAcquireAsync(LeaseResource resource, string holderId, TimeSpan duration,
        CancellationToken cancellationToken) => RunStoreOperationAsync("CoordinatorAcquire", false,
        token => RelationalLeases.TryAcquireAsync(resource, holderId, duration, token), cancellationToken);

    Task<LeaseGrant?> IExclusiveLeaseStore.TryRenewAsync(LeaseIdentity identity, TimeSpan duration,
        CancellationToken cancellationToken) => RunStoreOperationAsync("CoordinatorRenew", true,
        token => RelationalLeases.TryRenewAsync(identity, duration, token), cancellationToken);

    Task<bool> IExclusiveLeaseStore.TryReleaseAsync(LeaseIdentity identity, CancellationToken cancellationToken) =>
        RunStoreOperationAsync("CoordinatorRelease", true,
            token => RelationalLeases.TryReleaseAsync(identity, token), cancellationToken);

    Task<LeaseSnapshot> IExclusiveLeaseStore.GetSnapshotAsync(string scope, string namePrefix, CancellationToken cancellationToken) =>
        RunStoreOperationAsync("CoordinatorSnapshot", false,
            token => RelationalLeases.GetSnapshotAsync(scope, namePrefix, token), cancellationToken);

    /// <inheritdoc />
    public Task<JobCoordinatorSnapshot> GetCoordinatorSnapshotAsync(string schedulerScopeKey,
        CancellationToken cancellationToken = default) =>
        JobCoordinatorLeaseAdapter.SnapshotAsync(CoordinatorLeases, schedulerScopeKey, cancellationToken);

    /// <inheritdoc />
    public async Task<JobCoordinatorLease?> TryAcquireCoordinatorLeaseAsync(string schedulerScopeKey, string coordinatorKey,
        string workerInstanceId, TimeSpan leaseDuration, string? currentToken = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        return await JobCoordinatorLeaseAdapter.AcquireAsync(CoordinatorLeases, schedulerScopeKey, coordinatorKey,
            workerInstanceId, leaseDuration, currentToken, deadline.Token);
    }

    private Task ValidateCoordinatorLeaseAsync(JobSchedulerDbContext context, JobCoordinatorLeaseKey? key,
        CancellationToken cancellationToken) => key is null ? Task.CompletedTask
        : RelationalLeases.EnsureHeldAsync(context, key.ToIdentity(), cancellationToken);

    private static async Task<JobSchedulerOwnerEntity?> LockOwnerAsync(
        JobSchedulerDbContext context, string scope, string owner, CancellationToken token)
    {
        if (!UsesNativeRowLocks(context))
        {
            return await context.SchedulerOwners.SingleOrDefaultAsync(row =>
                row.SchedulerScopeKey == scope && row.OwnerKey == owner, token);
        }
        return (await context.SchedulerOwners.FromSqlInterpolated(
                $"SELECT * FROM \"JobSchedulerOwners\" WHERE \"SchedulerScopeKey\" = {scope} AND \"OwnerKey\" = {owner} FOR UPDATE")
            .ToListAsync(token)).SingleOrDefault();
    }

    /// <inheritdoc />
    public Task<string?> GetOwnerManifestHashAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        return ReadAsync((context, token) => context.SchedulerOwners.AsNoTracking()
            .Where(row => row.SchedulerScopeKey == schedulerScopeKey && row.OwnerKey == ownerKey)
            .Select(row => (string?)row.ManifestHash).SingleOrDefaultAsync(token), cancellationToken);
    }
}
