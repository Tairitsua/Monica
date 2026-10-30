using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    private readonly TimeSpan _ownerStaleAfter = (options?.Value.SnapshotSyncInterval ?? TimeSpan.FromSeconds(30)) * 3;

    /// <inheritdoc />
    public Task<JobDefinitionCleanupSnapshot> QueryDefinitionCleanupAsync(string schedulerScopeKey,
        JobDefinitionCleanupQuery query, TimeSpan ownerStaleAfter, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        query.Validate();
        if (ownerStaleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ownerStaleAfter));
        return ReadAsync(async (db, token) =>
        {
            var now = await GetUtcNowAsync(db, token);
            var cutoff = ToTicks(now - ownerStaleAfter);
            var definitions = db.Definitions.AsNoTracking().Where(row => row.SchedulerScopeKey == schedulerScopeKey);
            var candidates = CleanupRows(db, definitions).Where(row => row.ManifestHash == null || !row.Definition.IsPresent || row.ObservedTicks < cutoff);
            var owners = await candidates.Select(row => row.Definition.OwnerKey).Distinct().OrderBy(key => key).ToArrayAsync(token);
            var filtered = candidates;
            if (query.OwnerKey is not null) filtered = filtered.Where(row => row.Definition.OwnerKey == query.OwnerKey);
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var search = query.SearchText.ToLower();
                filtered = filtered.Where(row => row.Definition.OwnerKey.ToLower().Contains(search)
                    || row.Definition.JobKey.ToLower().Contains(search) || row.Definition.JobName.ToLower().Contains(search));
            }
            var counts = new Dictionary<JobDefinitionCleanupState, int>();
            foreach (var state in new[] { JobDefinitionCleanupState.Ready, JobDefinitionCleanupState.Queued,
                         JobDefinitionCleanupState.Running, JobDefinitionCleanupState.OwnerUnconfirmed })
                counts[state] = await FilterCleanupState(filtered, state, cutoff).CountAsync(token);
            var selected = query.State is { } selection ? FilterCleanupState(filtered, selection, cutoff) : filtered;
            var total = await selected.CountAsync(token);
            var rows = await selected.OrderBy(row => row.Definition.OwnerKey).ThenBy(row => row.Definition.JobKey)
                .Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToListAsync(token);
            return new JobDefinitionCleanupSnapshot
            {
                Items = rows.Select(row => row.ToItem(cutoff)).ToArray(), Counts = counts, Owners = owners,
                TotalCount = total, ScopeDefinitionCount = await definitions.CountAsync(token), ObservedAtUtc = now
            };
        }, cancellationToken);
    }

    private static IQueryable<CleanupRow> FilterCleanupState(IQueryable<CleanupRow> query, JobDefinitionCleanupState state, long cutoff) => state switch
    {
        JobDefinitionCleanupState.Ready => query.Where(row => row.ManifestHash != null && !row.Definition.IsPresent && row.Running == 0 && row.Leased == 0 && row.Slots == 0 && row.Queued == 0),
        JobDefinitionCleanupState.Queued => query.Where(row => row.ManifestHash != null && !row.Definition.IsPresent && row.Running == 0 && row.Leased == 0 && row.Slots == 0 && row.Queued > 0),
        JobDefinitionCleanupState.Running => query.Where(row => row.ManifestHash != null && !row.Definition.IsPresent && (row.Running > 0 || row.Leased > 0 || row.Slots > 0)),
        JobDefinitionCleanupState.OwnerUnconfirmed => query.Where(row => row.ManifestHash == null || (row.Definition.IsPresent && row.ObservedTicks < cutoff)),
        JobDefinitionCleanupState.Published => query.Where(row => row.ManifestHash != null && row.Definition.IsPresent && row.ObservedTicks >= cutoff),
        _ => query.Where(row => false)
    };

    private static IQueryable<CleanupRow> CleanupRows(JobSchedulerDbContext db, IQueryable<JobDefinitionEntity> definitions) =>
        from definition in definitions
        join owner in db.SchedulerOwners on new { definition.SchedulerScopeKey, definition.OwnerKey }
            equals new { owner.SchedulerScopeKey, owner.OwnerKey } into ownerRows
        from owner in ownerRows.DefaultIfEmpty()
        select new CleanupRow
        {
            Definition = definition,
            ManifestHash = owner == null ? null : owner.ManifestHash,
            PublishedTicks = owner == null ? null : owner.ManifestPublishedAtUtcTicks,
            RetiredTicks = owner == null ? null : owner.RetiredAtUtcTicks,
            ObservedTicks = owner == null ? definition.LastObservedAtUtcTicks : owner.LastObservedAtUtcTicks,
            Queued = db.Executions.Count(row => row.SchedulerScopeKey == definition.SchedulerScopeKey
                && row.OwnerKey == definition.OwnerKey && row.JobKey == definition.JobKey && row.State == JobExecutionState.Queued),
            Running = db.Executions.Count(row => row.SchedulerScopeKey == definition.SchedulerScopeKey
                && row.OwnerKey == definition.OwnerKey && row.JobKey == definition.JobKey && row.State == JobExecutionState.Running),
            Leased = db.Executions.Count(row => row.SchedulerScopeKey == definition.SchedulerScopeKey
                && row.OwnerKey == definition.OwnerKey && row.JobKey == definition.JobKey && row.ExecutionLeaseToken != null),
            Slots = db.ExecutionGates.Where(row => row.SchedulerScopeKey == definition.SchedulerScopeKey
                && row.OwnerKey == definition.OwnerKey && row.JobKey == definition.JobKey).Select(row => (int?)row.ActiveCount).SingleOrDefault() ?? 0,
            History = db.Executions.LongCount(row => row.SchedulerScopeKey == definition.SchedulerScopeKey
                && row.OwnerKey == definition.OwnerKey && row.JobKey == definition.JobKey
                && row.State != JobExecutionState.Queued && row.State != JobExecutionState.Running)
        };

    private sealed class CleanupRow
    {
        public JobDefinitionEntity Definition { get; init; } = null!;
        public string? ManifestHash { get; init; }
        public long? PublishedTicks { get; init; }
        public long? RetiredTicks { get; init; }
        public long ObservedTicks { get; init; }
        public int Queued { get; init; }
        public int Running { get; init; }
        public int Leased { get; init; }
        public int Slots { get; init; }
        public long History { get; init; }
        public JobDefinitionCleanupItem ToItem(long cutoff) => new()
        {
            Id = new JobId(Definition.OwnerKey, Definition.JobKey),
            Definition = ToDefinition(Definition) with { LastObservedAtUtc = FromTicks(ObservedTicks) },
            ManifestHash = ManifestHash, ManifestPublishedAtUtc = FromTicks(PublishedTicks), OwnerRetiredAtUtc = FromTicks(RetiredTicks),
            QueuedCount = Queued, RunningCount = Running, ActiveLeaseCount = Math.Max(Leased, Slots), HistoryCount = History,
            State = ManifestHash == null ? JobDefinitionCleanupState.OwnerUnconfirmed
                : Definition.IsPresent ? ObservedTicks < cutoff ? JobDefinitionCleanupState.OwnerUnconfirmed : JobDefinitionCleanupState.Published
                : Running > 0 || Leased > 0 || Slots > 0 ? JobDefinitionCleanupState.Running
                : Queued > 0 ? JobDefinitionCleanupState.Queued : JobDefinitionCleanupState.Ready
        };
    }

    private static IQueryable<TEntity> WhereCleanupIds<TEntity>(IQueryable<TEntity> query, IReadOnlyList<JobId> ids)
    {
        var row = Expression.Parameter(typeof(TEntity), "row");
        Expression predicate = Expression.Constant(false);
        foreach (var id in ids)
            predicate = Expression.OrElse(predicate, Expression.AndAlso(
                Expression.Equal(Expression.Property(row, "OwnerKey"), Expression.Constant(id.OwnerKey)),
                Expression.Equal(Expression.Property(row, "JobKey"), Expression.Constant(id.JobKey))));
        return query.Where(Expression.Lambda<Func<TEntity, bool>>(predicate, row));
    }

    private async Task<IReadOnlyList<JobDefinitionCleanupItem>> ReadCleanupItemsAsync(JobSchedulerDbContext db,
        string scope, IReadOnlyList<JobId> ids, DateTimeOffset now, CancellationToken token)
    {
        var definitions = WhereCleanupIds(db.Definitions.Where(row => row.SchedulerScopeKey == scope), ids);
        var rows = await CleanupRows(db, definitions).ToListAsync(token);
        var items = rows.Select(row => row.ToItem(ToTicks(now - _ownerStaleAfter))).ToDictionary(item => item.Id);
        var queueIds = items.Values.Where(item => item.CanRemove(true)
                && item.QueuedCount <= JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS).Select(item => item.Id).ToArray();
        var queued = await WhereCleanupIds(db.Executions.AsNoTracking().Where(row => row.SchedulerScopeKey == scope
                && row.State == JobExecutionState.Queued), queueIds)
            .OrderBy(row => row.OwnerKey).ThenBy(row => row.JobKey).ThenBy(row => row.InstanceId)
            .Select(row => new { row.OwnerKey, row.JobKey, row.InstanceId })
            .Take(JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS + 1).ToListAsync(token);
        foreach (var id in queueIds)
        {
            var exact = queued.Where(row => row.OwnerKey == id.OwnerKey && row.JobKey == id.JobKey)
                .Select(row => row.InstanceId).Order(StringComparer.Ordinal).ToArray();
            if (exact.Length == items[id].QueuedCount)
                items[id] = items[id] with { QueueIdentityStamp = CleanupFingerprint.Create(exact) };
        }
        return ids.Select(id => items.GetValueOrDefault(id)
            ?? new JobDefinitionCleanupItem { Id = id, State = JobDefinitionCleanupState.Missing }).ToArray();
    }

    /// <inheritdoc />
    public Task<JobDefinitionCleanupPlan> PreviewDefinitionCleanupAsync(string schedulerScopeKey,
        IReadOnlyList<JobId> ids, bool cancelQueued, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        JobDefinitionCleanupRequest.ValidateIds(ids);
        var frozen = ids.ToArray();
        return ReadAsync(async (db, token) => new JobDefinitionCleanupPlan
        {
            Items = await ReadCleanupItemsAsync(db, schedulerScopeKey, frozen, await GetUtcNowAsync(db, token), token),
            CancelQueued = cancelQueued
        }, cancellationToken);
    }

    private static async Task LockCleanupGatesAsync(JobSchedulerDbContext db, string scope,
        IReadOnlyList<JobId>? ids, string? owner, CancellationToken token)
    {
        if (!UsesNativeRowLocks(db))
        {
            var query = db.ExecutionGates.Where(row => row.SchedulerScopeKey == scope);
            if (owner is not null) query = query.Where(row => row.OwnerKey == owner);
            if (ids is not null) query = WhereCleanupIds(query, ids);
            await query.OrderBy(row => row.OwnerKey).ThenBy(row => row.JobKey).ToListAsync(token);
            return;
        }
        var parameters = new List<object> { scope };
        var predicate = "";
        if (owner is not null) { parameters.Add(owner); predicate = " AND \"OwnerKey\" = {1}"; }
        if (ids is not null)
        {
            var clauses = new List<string>();
            foreach (var id in ids)
            {
                clauses.Add("(\"OwnerKey\" = {" + parameters.Count + "} AND \"JobKey\" = {" + (parameters.Count + 1) + "})");
                parameters.Add(id.OwnerKey); parameters.Add(id.JobKey);
            }
            predicate = " AND (" + string.Join(" OR ", clauses) + ")";
        }
        var sql = "SELECT * FROM " + TableSql<JobExecutionGateEntity>(db) + " WHERE \"SchedulerScopeKey\" = {0}"
                  + predicate + " ORDER BY \"OwnerKey\", \"JobKey\" FOR UPDATE";
        await db.ExecutionGates.FromSqlRaw(sql, parameters.ToArray()).ToListAsync(token);
    }

    /// <inheritdoc />
    public Task<JobDefinitionMaintenanceResult> RemoveDefinitionsAsync(string schedulerScopeKey,
        JobDefinitionCleanupRequest request, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        request.Validate();
        var targets = request.Targets.ToArray();
        var cancelQueued = request.CancelQueued;
        var ids = targets.Select(target => target.Id).ToArray();
        return WriteAsync(async (db, token) =>
        {
            // Publication owns Owner -> Gate; every admission/claim owns Gate. Lock all prerequisites before
            // validating any target, then mutate only after the entire submitted set has passed.
            foreach (var owner in ids.Select(id => id.OwnerKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                await LockOwnerAsync(db, schedulerScopeKey, owner, token);
            await LockCleanupGatesAsync(db, schedulerScopeKey, ids, null, token);
            var now = await GetUtcNowAsync(db, token);
            var current = await ReadCleanupItemsAsync(db, schedulerScopeKey, ids, now, token);
            if (current.Where((item, index) => item.Stamp != targets[index].ExpectedStamp).Any())
                return new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Changed, AtUtc = now };
            if (current.Any(item => !item.CanRemove(cancelQueued))
                || current.Sum(item => (long)item.QueuedCount) > JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS)
                return new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Blocked, AtUtc = now };
            var queued = await WhereCleanupIds(db.Executions.Where(row => row.SchedulerScopeKey == schedulerScopeKey
                && row.State == JobExecutionState.Queued), ids).ToListAsync(token);
            foreach (var execution in queued)
            {
                execution.State = JobExecutionState.Cancelled;
                execution.CompletedAtUtcTicks = execution.CancellationRequestedAtUtcTicks = ToTicks(now);
                ClearExecutionLease(execution);
                AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                    "Queued execution cancelled by definition cleanup", null, JobExecutionState.Queued, JobExecutionState.Cancelled);
            }
            db.RecurringCursors.RemoveRange(await WhereCleanupIds(db.RecurringCursors.Where(row => row.SchedulerScopeKey == schedulerScopeKey), ids).ToListAsync(token));
            db.ExecutionGates.RemoveRange(db.ExecutionGates.Local.Where(row => row.SchedulerScopeKey == schedulerScopeKey
                && ids.Contains(new JobId(row.OwnerKey, row.JobKey))).ToArray());
            db.Definitions.RemoveRange(await WhereCleanupIds(db.Definitions.Where(row => row.SchedulerScopeKey == schedulerScopeKey), ids).ToListAsync(token));
            return new JobDefinitionMaintenanceResult
            {
                Status = JobDefinitionMaintenanceStatus.Applied, AtUtc = now, Definitions = ids,
                CancelledCount = queued.Count, RetainedExecutionCount = current.Sum(item => item.HistoryCount + item.QueuedCount)
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobOwnerRetirementPreview> PreviewOwnerRetirementAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        return ReadAsync((db, token) => ReadOwnerRetirementAsync(db, schedulerScopeKey, ownerKey, token), cancellationToken);
    }

    private async Task<JobOwnerRetirementPreview> ReadOwnerRetirementAsync(JobSchedulerDbContext db, string scope, string ownerKey, CancellationToken token)
    {
        var owner = await db.SchedulerOwners.SingleOrDefaultAsync(row => row.SchedulerScopeKey == scope && row.OwnerKey == ownerKey, token);
        var executions = db.Executions.Where(row => row.SchedulerScopeKey == scope && row.OwnerKey == ownerKey);
        // This observation is deliberately non-locking: an already accepted publication is serialized by Owner;
        // a coordinator acquired concurrently can no longer publish after the durable retirement fence commits.
        var publisher = await RelationalLeases.GetSnapshotAsync(new LeaseResource(scope, "jobscheduler:owner:" + ownerKey), token);
        return new JobOwnerRetirementPreview
        {
            SchedulerScopeKey = scope, OwnerKey = ownerKey, ManifestHash = owner?.ManifestHash,
            LastObservedAtUtc = FromTicks(owner?.LastObservedAtUtcTicks), RetiredAtUtc = FromTicks(owner?.RetiredAtUtcTicks),
            DefinitionCount = await db.Definitions.CountAsync(row => row.SchedulerScopeKey == scope && row.OwnerKey == ownerKey, token),
            QueuedCount = await executions.CountAsync(row => row.State == JobExecutionState.Queued, token),
            RunningCount = await executions.CountAsync(row => row.State == JobExecutionState.Running, token),
            ActiveLeaseCount = Math.Max(await executions.CountAsync(row => row.ExecutionLeaseToken != null, token),
                await db.ExecutionGates.Where(row => row.SchedulerScopeKey == scope && row.OwnerKey == ownerKey).SumAsync(row => row.ActiveCount, token)),
            HasActivePublisher = publisher.Leases.Any(row => row.ExpiresAtUtc > publisher.ObservedAtUtc)
        };
    }

    /// <inheritdoc />
    public Task<JobDefinitionMaintenanceResult> RetireOwnerAsync(string schedulerScopeKey, string ownerKey,
        string expectedStamp, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStamp);
        return WriteAsync(async (db, token) =>
        {
            var owner = await LockOwnerAsync(db, schedulerScopeKey, ownerKey, token);
            await LockCleanupGatesAsync(db, schedulerScopeKey, null, ownerKey, token);
            var preview = await ReadOwnerRetirementAsync(db, schedulerScopeKey, ownerKey, token);
            var now = await GetUtcNowAsync(db, token);
            if (preview.Stamp != expectedStamp)
                return new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Changed, AtUtc = now };
            if (!preview.CanRetire)
                return new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Blocked, AtUtc = now };
            var definitions = await db.Definitions.Where(row => row.SchedulerScopeKey == schedulerScopeKey && row.OwnerKey == ownerKey).ToListAsync(token);
            owner!.RetiredAtUtcTicks = ToTicks(now);
            foreach (var definition in definitions)
            {
                if (definition.IsPresent) definition.AbsentSinceUtcTicks = ToTicks(now);
                definition.IsPresent = false;
            }
            db.RecurringCursors.RemoveRange(await db.RecurringCursors.Where(row => row.SchedulerScopeKey == schedulerScopeKey && row.OwnerKey == ownerKey).ToListAsync(token));
            return new JobDefinitionMaintenanceResult
            {
                Status = JobDefinitionMaintenanceStatus.Applied, AtUtc = now,
                Definitions = definitions.Select(row => new JobId(row.OwnerKey, row.JobKey)).ToArray()
            };
        }, cancellationToken);
    }
}
