using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Providers;

public sealed partial class InMemoryJobSchedulerStore
{
    private readonly Dictionary<(string Scope, string Owner), DateTimeOffset> _retiredOwners = [];

    /// <inheritdoc />
    public Task<JobDefinitionCleanupSnapshot> QueryDefinitionCleanupAsync(string schedulerScopeKey,
        JobDefinitionCleanupQuery query, TimeSpan ownerStaleAfter, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        query.Validate();
        if (ownerStaleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ownerStaleAfter));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = UtcNow;
            var definitions = _definitions.Values.Where(row => row.SchedulerScopeKey == schedulerScopeKey).ToArray();
            var candidates = definitions.Select(row => AssessCleanupUnsafe(schedulerScopeKey, row.Id, now - ownerStaleAfter))
                .Where(row => row.State != JobDefinitionCleanupState.Published).ToArray();
            var filtered = candidates.Where(row => (query.OwnerKey is null || row.Id.OwnerKey == query.OwnerKey)
                && (string.IsNullOrWhiteSpace(query.SearchText)
                    || new[] { row.Id.OwnerKey, row.Id.JobKey, row.Definition!.EffectiveConfiguration.JobName }
                        .Any(value => value.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)))).ToArray();
            var selected = filtered.Where(row => query.State is null || row.State == query.State).ToArray();
            return Task.FromResult(new JobDefinitionCleanupSnapshot
            {
                Items = selected.OrderBy(row => row.Id.OwnerKey, StringComparer.Ordinal)
                    .ThenBy(row => row.Id.JobKey, StringComparer.Ordinal)
                    .Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToArray(),
                Counts = new[] { JobDefinitionCleanupState.Ready, JobDefinitionCleanupState.Queued,
                        JobDefinitionCleanupState.Running, JobDefinitionCleanupState.OwnerUnconfirmed }
                    .ToDictionary(state => state, state => filtered.Count(row => row.State == state)),
                Owners = candidates.Select(row => row.Id.OwnerKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                TotalCount = selected.Length, ScopeDefinitionCount = definitions.Length, ObservedAtUtc = now
            });
        }
    }

    /// <inheritdoc />
    public Task<JobDefinitionCleanupPlan> PreviewDefinitionCleanupAsync(string schedulerScopeKey,
        IReadOnlyList<JobId> ids, bool cancelQueued, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        JobDefinitionCleanupRequest.ValidateIds(ids);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(new JobDefinitionCleanupPlan
            {
                Items = ids.Select(id => AssessCleanupUnsafe(schedulerScopeKey, id, UtcNow - OwnerStaleAfter)).ToArray(),
                CancelQueued = cancelQueued
            });
    }

    private TimeSpan OwnerStaleAfter => (options?.Value.SnapshotSyncInterval ?? TimeSpan.FromSeconds(30)) * 3;

    private JobDefinitionCleanupItem AssessCleanupUnsafe(string scope, JobId id, DateTimeOffset staleBefore)
    {
        var key = new DefinitionKey(scope, id.OwnerKey, id.JobKey);
        if (!_definitions.TryGetValue(key, out var definition))
            return new JobDefinitionCleanupItem { Id = id, State = JobDefinitionCleanupState.Missing };
        definition = WithOwnerObservationUnsafe(definition);
        _owners.TryGetValue((scope, id.OwnerKey), out var owner);
        var executions = _executionInstances.Values.Where(row => GetExecutionGateKey(row.Template) == key).ToArray();
        var queued = executions.Where(row => row.State == JobExecutionState.Queued).ToArray();
        var running = executions.Count(row => row.State == JobExecutionState.Running);
        var leased = Math.Max(_executionGates.GetValueOrDefault(key)?.ActiveCount ?? 0,
            executions.Count(row => row.ExecutionLeaseToken is not null));
        var state = owner.Hash is null ? JobDefinitionCleanupState.OwnerUnconfirmed : definition.IsPresent
            ? definition.LastObservedAtUtc < staleBefore ? JobDefinitionCleanupState.OwnerUnconfirmed : JobDefinitionCleanupState.Published
            : running > 0 || leased > 0 ? JobDefinitionCleanupState.Running
            : queued.Length > 0 ? JobDefinitionCleanupState.Queued : JobDefinitionCleanupState.Ready;
        return new JobDefinitionCleanupItem
        {
            Id = id, Definition = definition, ManifestHash = owner.Hash,
            ManifestPublishedAtUtc = owner.Published == default ? null : owner.Published,
            OwnerRetiredAtUtc = _retiredOwners.TryGetValue((scope, id.OwnerKey), out var retired) ? retired : null,
            State = state, QueuedCount = queued.Length, RunningCount = running, ActiveLeaseCount = leased,
            HistoryCount = executions.LongCount(row => IsTerminal(row.State)),
            QueueIdentityStamp = queued.Length <= JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS
                ? CleanupFingerprint.Create(queued.Select(row => row.InstanceId).Order(StringComparer.Ordinal).ToArray()) : null
        };
    }

    /// <inheritdoc />
    public Task<JobDefinitionMaintenanceResult> RemoveDefinitionsAsync(string schedulerScopeKey,
        JobDefinitionCleanupRequest request, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = UtcNow;
            var current = request.Targets.Select(target => AssessCleanupUnsafe(schedulerScopeKey, target.Id, now - OwnerStaleAfter)).ToArray();
            if (current.Where((item, index) => item.Stamp != request.Targets[index].ExpectedStamp).Any())
                return Task.FromResult(new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Changed, AtUtc = now });
            if (current.Any(item => !item.CanRemove(request.CancelQueued))
                || current.Sum(item => (long)item.QueuedCount) > JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS)
                return Task.FromResult(new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Blocked, AtUtc = now });
            foreach (var item in current)
            {
                var key = new DefinitionKey(schedulerScopeKey, item.Id.OwnerKey, item.Id.JobKey);
                foreach (var execution in _executionInstances.Values.Where(row => GetExecutionGateKey(row.Template) == key
                             && row.State == JobExecutionState.Queued))
                {
                    execution.State = JobExecutionState.Cancelled;
                    execution.CompletedAtUtc = execution.CancellationRequestedAtUtc = now;
                    ClearExecutionLeaseUnsafe(execution);
                    AddHistory(execution, Transition(now, JobExecutionState.Queued, JobExecutionState.Cancelled,
                        "Queued execution cancelled by definition cleanup", null));
                }
                _recurringCursors.Remove(key);
                _executionGates.Remove(key);
                _definitions.Remove(key);
            }
            return Task.FromResult(new JobDefinitionMaintenanceResult
            {
                Status = JobDefinitionMaintenanceStatus.Applied, AtUtc = now,
                Definitions = current.Select(item => item.Id).ToArray(),
                CancelledCount = current.Sum(item => item.QueuedCount),
                RetainedExecutionCount = current.Sum(item => item.HistoryCount + item.QueuedCount)
            });
        }
    }

    /// <inheritdoc />
    public Task<JobOwnerRetirementPreview> PreviewOwnerRetirementAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(PreviewRetirementUnsafe(schedulerScopeKey, ownerKey, cancellationToken));
    }

    private JobOwnerRetirementPreview PreviewRetirementUnsafe(string scope, string ownerKey, CancellationToken token)
    {
        _owners.TryGetValue((scope, ownerKey), out var owner);
        // This concrete store completes snapshot reads synchronously under the same shared monitor.
        var lease = MemoryLeases.GetSnapshotAsync(new LeaseResource(scope, "jobscheduler:owner:" + ownerKey), token).GetAwaiter().GetResult();
        var executions = _executionInstances.Values.Where(row => row.Template.SchedulerScopeKey == scope
            && row.Template.OwnerKey == ownerKey).ToArray();
        return new JobOwnerRetirementPreview
        {
            SchedulerScopeKey = scope, OwnerKey = ownerKey, ManifestHash = owner.Hash,
            LastObservedAtUtc = owner.Observed == default ? null : owner.Observed,
            RetiredAtUtc = _retiredOwners.TryGetValue((scope, ownerKey), out var retired) ? retired : null,
            DefinitionCount = _definitions.Keys.Count(key => key.SchedulerScopeKey == scope && key.OwnerKey == ownerKey),
            QueuedCount = executions.Count(row => row.State == JobExecutionState.Queued),
            RunningCount = executions.Count(row => row.State == JobExecutionState.Running),
            ActiveLeaseCount = Math.Max(_executionGates.Where(pair => pair.Key.SchedulerScopeKey == scope && pair.Key.OwnerKey == ownerKey)
                .Sum(pair => pair.Value.ActiveCount), executions.Count(row => row.ExecutionLeaseToken is not null)),
            HasActivePublisher = lease.Leases.Any(row => row.ExpiresAtUtc > lease.ObservedAtUtc)
        };
    }

    /// <inheritdoc />
    public Task<JobDefinitionMaintenanceResult> RetireOwnerAsync(string schedulerScopeKey, string ownerKey,
        string expectedStamp, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStamp);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var preview = PreviewRetirementUnsafe(schedulerScopeKey, ownerKey, cancellationToken);
            var now = UtcNow;
            if (preview.Stamp != expectedStamp)
                return Task.FromResult(new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Changed, AtUtc = now });
            if (!preview.CanRetire)
                return Task.FromResult(new JobDefinitionMaintenanceResult { Status = JobDefinitionMaintenanceStatus.Blocked, AtUtc = now });
            var definitions = _definitions.Values.Where(row => row.SchedulerScopeKey == schedulerScopeKey && row.OwnerKey == ownerKey).ToArray();
            _retiredOwners.Add((schedulerScopeKey, ownerKey), now);
            foreach (var definition in definitions)
            {
                var key = DefinitionKeyOf(definition);
                _definitions[key] = definition with { IsPresent = false, AbsentSinceUtc = definition.IsPresent ? now : definition.AbsentSinceUtc };
                _recurringCursors.Remove(key);
            }
            return Task.FromResult(new JobDefinitionMaintenanceResult
            {
                Status = JobDefinitionMaintenanceStatus.Applied, AtUtc = now,
                Definitions = definitions.Select(row => row.Id).ToArray()
            });
        }
    }
}
