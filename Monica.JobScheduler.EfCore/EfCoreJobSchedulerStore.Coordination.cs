using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobCoordinatorSnapshot> GetCoordinatorSnapshotAsync(string schedulerScopeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        return ReadAsync(async (context, token) =>
        {
            var rows = await context.Coordinators.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == schedulerScopeKey)
                .OrderBy(row => row.CoordinatorKey)
                .Select(row => new { row.CoordinatorKey, row.WorkerInstanceId, row.ExpiresAtUtcTicks })
                .ToListAsync(token);
            var now = await GetUtcNowAsync(context, token);
            return new JobCoordinatorSnapshot
            {
                ObservedAtUtc = now,
                Coordinators = rows.Select(row => new JobCoordinatorSummary
                {
                    CoordinatorKey = row.CoordinatorKey,
                    WorkerInstanceId = row.WorkerInstanceId,
                    ExpiresAtUtc = FromTicks(row.ExpiresAtUtcTicks)
                }).ToArray()
            };
        }, cancellationToken);
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
        return AcquireCoordinatorCoreAsync(schedulerScopeKey, coordinatorKey, workerInstanceId, leaseDuration,
            currentToken, cancellationToken);
    }

    private async Task<JobCoordinatorLease?> AcquireCoordinatorCoreAsync(string schedulerScopeKey, string coordinatorKey,
        string workerInstanceId, TimeSpan leaseDuration, string? currentToken, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        cancellationToken = deadline.Token;
        // Standbys observe the indexed coordinator row without joining the holder's write-lock queue. A false
        // hint only defers this attempt; every acquisition and renewal still rechecks under the row lock below.
        var heldByOther = await ReadAsync(async (context, token) =>
        {
            if (UsesNativeRowLocks(context))
            {
                var sql = "SELECT EXISTS (SELECT 1 FROM \"JobSchedulerCoordinators\" "
                          + "WHERE \"SchedulerScopeKey\" = {0} AND \"CoordinatorKey\" = {1} "
                          + "AND \"ExpiresAtUtcTicks\" > " + DATABASE_STATEMENT_TICKS_SQL
                          + " AND (\"WorkerInstanceId\" <> {2} OR \"LeaseToken\" <> {3})) AS \"Value\"";
                return await context.Database.SqlQueryRaw<bool>(sql,
                    schedulerScopeKey, coordinatorKey, workerInstanceId, currentToken ?? string.Empty).SingleAsync(token);
            }
            var nowTicks = ToTicks(await GetUtcNowAsync(context, token));
            return await context.Coordinators.AsNoTracking().AnyAsync(row =>
                row.SchedulerScopeKey == schedulerScopeKey && row.CoordinatorKey == coordinatorKey
                && row.ExpiresAtUtcTicks > nowTicks
                && (row.WorkerInstanceId != workerInstanceId || row.LeaseToken != currentToken), token);
        }, cancellationToken, nameof(TryAcquireCoordinatorLeaseAsync));
        if (heldByOther) return null;

        return await WriteAsync<JobCoordinatorLease?>(async (context, token) =>
        {
            var entity = await LockCoordinatorAsync(context, schedulerScopeKey, coordinatorKey, token);
            var now = await GetUtcNowAsync(context, token);
            if (entity is not null && entity.ExpiresAtUtcTicks > ToTicks(now)
                && (entity.WorkerInstanceId != workerInstanceId || entity.LeaseToken != currentToken))
            {
                return null;
            }
            if (entity is null)
            {
                entity = new JobSchedulerCoordinatorEntity
                {
                    SchedulerScopeKey = schedulerScopeKey, CoordinatorKey = coordinatorKey,
                    WorkerInstanceId = workerInstanceId, LeaseToken = NewToken()
                };
                context.Coordinators.Add(entity);
            }
            else if (entity.ExpiresAtUtcTicks <= ToTicks(now))
            {
                entity.LeaseToken = NewToken();
            }
            entity.WorkerInstanceId = workerInstanceId;
            entity.ExpiresAtUtcTicks = ToTicks(now.Add(leaseDuration));
            return new JobCoordinatorLease
            {
                Key = new JobCoordinatorLeaseKey
                {
                    SchedulerScopeKey = schedulerScopeKey, CoordinatorKey = coordinatorKey,
                    WorkerInstanceId = workerInstanceId, LeaseToken = entity.LeaseToken
                },
                GrantedAtUtc = now, ExpiresAtUtc = FromTicks(entity.ExpiresAtUtcTicks)
            };
        }, cancellationToken, operationName: nameof(TryAcquireCoordinatorLeaseAsync));
    }

    private static async Task<JobSchedulerCoordinatorEntity?> LockCoordinatorAsync(
        JobSchedulerDbContext context, string scope, string key, CancellationToken token)
    {
        if (!UsesNativeRowLocks(context))
        {
            return await context.Coordinators.SingleOrDefaultAsync(row =>
                row.SchedulerScopeKey == scope && row.CoordinatorKey == key, token);
        }
        return (await context.Coordinators.FromSqlInterpolated(
                $"SELECT * FROM \"JobSchedulerCoordinators\" WHERE \"SchedulerScopeKey\" = {scope} AND \"CoordinatorKey\" = {key} FOR UPDATE")
            .ToListAsync(token)).SingleOrDefault();
    }

    private async Task ValidateCoordinatorLeaseAsync(JobSchedulerDbContext context,
        JobCoordinatorLeaseKey? key, CancellationToken token)
    {
        if (key is null) return;
        var entity = await LockCoordinatorAsync(context, key.SchedulerScopeKey, key.CoordinatorKey, token);
        var now = await GetUtcNowAsync(context, token);
        if (entity is null || entity.WorkerInstanceId != key.WorkerInstanceId || entity.LeaseToken != key.LeaseToken
            || entity.ExpiresAtUtcTicks <= ToTicks(now))
        {
            throw new InvalidOperationException("The scheduling coordinator lease is no longer current.");
        }
    }

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
