using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    private const int CLEANUP_DEFINITION_PAGE_SIZE = 256;

    /// <inheritdoc />
    public Task<JobExecutionInstance> EnqueueAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return WriteJobAsync(request.SchedulerScopeKey, request.OwnerKey, request.JobKey, async (dbContext, token) =>
        {
            var existing = await LoadExecutionAsync(
                dbContext,
                request.SchedulerScopeKey,
                request.InstanceId,
                true,
                false,
                token);
            if (existing is not null)
            {
                return ResolveIdempotentEnqueue(request, existing, JobExecutionOrigin.Triggered);
            }

            var template = await ResolveTriggeredExecutionTemplateAsync(dbContext, request, token);
            return await EnqueueCapturedExecutionAsync(
                dbContext,
                request,
                template,
                await GetUtcNowAsync(dbContext, token),
                token,
                JobExecutionOrigin.Triggered);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobExecutionInstance> RunRecurringNowAsync(
        JobRecurringRunNowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        var request = JobExecutionAdmission.CreateRecurringRunNowRequest(command);
        return WriteJobAsync(command.SchedulerScopeKey, command.OwnerKey, command.JobKey, async (dbContext, token) =>
        {
            var existing = await LoadExecutionAsync(
                dbContext,
                command.SchedulerScopeKey,
                command.InstanceId,
                true,
                false,
                token);
            if (existing is not null)
            {
                return ResolveIdempotentEnqueue(request, existing, JobExecutionOrigin.RecurringRunNow);
            }

            var template = await ResolveRecurringExecutionTemplateAsync(dbContext, command, token);
            return await EnqueueCapturedExecutionAsync(
                dbContext,
                request,
                template,
                await GetUtcNowAsync(dbContext, token),
                token,
                JobExecutionOrigin.RecurringRunNow);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobExecutionInstance?> GetExecutionAsync(
        string schedulerScopeKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(instanceId, nameof(instanceId));
        return ReadAsync(async (dbContext, token) =>
        {
            var entity = await LoadExecutionAsync(
                dbContext,
                schedulerScopeKey,
                instanceId,
                false,
                true,
                token);
            return entity is null ? null : ToExecutionDetail(entity);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<QueryResult<JobExecutionInstance>> QueryExecutionsAsync(
        JobExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return ReadAsync(async (dbContext, token) =>
        {
            var filtered = dbContext.Executions.AsNoTracking()
                .Where(item => item.SchedulerScopeKey == query.SchedulerScopeKey);
            if (!string.IsNullOrWhiteSpace(query.OwnerKey))
            {
                filtered = filtered.Where(item => item.OwnerKey == query.OwnerKey);
            }
            if (!string.IsNullOrWhiteSpace(query.JobKey))
            {
                filtered = filtered.Where(item => item.JobKey == query.JobKey);
            }
            if (query.States is { Count: > 0 })
            {
                filtered = filtered.Where(item => query.States.Contains(item.State));
            }
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var search = query.SearchText.ToLower();
                filtered = filtered.Where(item =>
                    item.InstanceId.ToLower().Contains(search) || item.JobKey.ToLower().Contains(search));
            }
            if (query.CreatedAfterUtc is { } createdAfter)
            {
                var ticks = ToTicks(createdAfter);
                filtered = filtered.Where(item => item.CreatedAtUtcTicks >= ticks);
            }
            if (query.CreatedBeforeUtc is { } createdBefore)
            {
                var ticks = ToTicks(createdBefore);
                filtered = filtered.Where(item => item.CreatedAtUtcTicks <= ticks);
            }

            var totalCount = await filtered.CountAsync(token);
            var ordered = query.SortField switch
            {
                JobExecutionSortField.CreatedAtUtc => query.SortDescending
                    ? filtered.OrderByDescending(item => item.CreatedAtUtcTicks).ThenByDescending(item => item.InstanceId)
                    : filtered.OrderBy(item => item.CreatedAtUtcTicks).ThenBy(item => item.InstanceId),
                JobExecutionSortField.AvailableAtUtc => query.SortDescending
                    ? filtered.OrderByDescending(item => item.AvailableAtUtcTicks).ThenByDescending(item => item.InstanceId)
                    : filtered.OrderBy(item => item.AvailableAtUtcTicks).ThenBy(item => item.InstanceId),
                JobExecutionSortField.StartedAtUtc => query.SortDescending
                    ? filtered.OrderByDescending(item => item.StartedAtUtcTicks).ThenByDescending(item => item.InstanceId)
                    : filtered.OrderBy(item => item.StartedAtUtcTicks).ThenBy(item => item.InstanceId),
                JobExecutionSortField.CompletedAtUtc => query.SortDescending
                    ? filtered.OrderByDescending(item => item.CompletedAtUtcTicks).ThenByDescending(item => item.InstanceId)
                    : filtered.OrderBy(item => item.CompletedAtUtcTicks).ThenBy(item => item.InstanceId),
                _ => throw new ArgumentOutOfRangeException(nameof(query), query.SortField, "Unsupported sort field.")
            };
            var entities = await ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync(token);
            return new QueryResult<JobExecutionInstance>(entities.Select(ToExecution).ToList(), totalCount);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobExecutionState, int>> GetExecutionStateStatisticsAsync(
        string schedulerScopeKey,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        return ReadAsync(async (dbContext, token) =>
        {
            var query = dbContext.Executions.AsNoTracking()
                .Where(item => item.SchedulerScopeKey == schedulerScopeKey);
            if (startTimeUtc is { } start)
            {
                var ticks = ToTicks(start);
                query = query.Where(item => item.CreatedAtUtcTicks >= ticks);
            }
            if (endTimeUtc is { } end)
            {
                var ticks = ToTicks(end);
                query = query.Where(item => item.CreatedAtUtcTicks <= ticks);
            }

            var grouped = await query.GroupBy(item => item.State)
                .Select(group => new { State = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.State, item => item.Count, token);
            return (IReadOnlyDictionary<JobExecutionState, int>)Enum.GetValues<JobExecutionState>()
                .ToDictionary(state => state, state => grouped.GetValueOrDefault(state));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobId, JobExecutionInstance?>> GetLatestExecutionsAsync(
        string schedulerScopeKey,
        string ownerKey,
        IEnumerable<JobId> jobIds,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        ArgumentNullException.ThrowIfNull(jobIds);
        var ids = jobIds.Distinct().ToArray();
        foreach (var jobId in ids)
        {
            jobId.Validate();
            if (!string.Equals(jobId.OwnerKey, ownerKey, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Requested job '{jobId}' belongs to owner '{jobId.OwnerKey}', not '{ownerKey}'.",
                    nameof(jobIds));
            }
        }
        return ReadAsync(async (dbContext, token) =>
        {
            var latest = new Dictionary<JobId, JobExecutionInstance?>(ids.Length);
            foreach (var jobId in ids)
            {
                var entity = await dbContext.Executions.AsNoTracking()
                    .Where(item => item.SchedulerScopeKey == schedulerScopeKey
                                   && item.OwnerKey == ownerKey
                                   && item.JobKey == jobId.JobKey)
                    .OrderByDescending(item => item.CreatedAtUtcTicks)
                    .ThenByDescending(item => item.InstanceId)
                    .FirstOrDefaultAsync(token);
                latest.Add(jobId, entity is null ? null : ToExecution(entity));
            }

            return (IReadOnlyDictionary<JobId, JobExecutionInstance?>)latest;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetExecutionCleanupCandidatesAsync(
        string schedulerScopeKey,
        IReadOnlyDictionary<JobId, JobHistoryRetentionPolicy> retentionPolicies,
        int maxRetainedOrphanedExecutions,
        int maxDeletions,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ArgumentNullException.ThrowIfNull(retentionPolicies);
        if (maxRetainedOrphanedExecutions < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetainedOrphanedExecutions),
                maxRetainedOrphanedExecutions,
                "Orphan retention cannot be negative.");
        }
        if (maxDeletions < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeletions),
                maxDeletions,
                "The cleanup result bound must be greater than zero.");
        }

        return ReadAsync(async (dbContext, token) =>
        {
            var now = await GetUtcNowAsync(dbContext, token);
            var candidates = new Dictionary<string, long>(StringComparer.Ordinal);
            var definitionOffset = 0;
            while (true)
            {
                // Definitions are paged independently from execution rows. Each per-definition query below is also
                // capped by the global result bound, so no cleanup pass materializes an unbounded terminal history.
                var definitions = await TerminalExecutions(dbContext, schedulerScopeKey)
                    .Select(item => new { item.OwnerKey, item.JobKey })
                    .Distinct()
                    .OrderBy(item => item.OwnerKey)
                    .ThenBy(item => item.JobKey)
                    .Skip(definitionOffset)
                    .Take(CLEANUP_DEFINITION_PAGE_SIZE)
                    .ToArrayAsync(token);
                foreach (var definition in definitions)
                {
                    var terminal = TerminalExecutions(dbContext, schedulerScopeKey)
                        .Where(item => item.OwnerKey == definition.OwnerKey && item.JobKey == definition.JobKey);
                    var jobId = new JobId(definition.OwnerKey, definition.JobKey);
                    var hasPolicy = retentionPolicies.TryGetValue(jobId, out var policy);
                    var maxRecords = hasPolicy ? policy!.MaxRecords : maxRetainedOrphanedExecutions;
                    if (maxRecords > 0)
                    {
                        var terminalCount = await terminal.CountAsync(token);
                        var countCandidateCount = Math.Min(maxDeletions, terminalCount - maxRecords);
                        if (countCandidateCount > 0)
                        {
                            var countCandidates = await OldestCleanupCandidates(terminal)
                                .Take(countCandidateCount)
                                .ToArrayAsync(token);
                            MergeCleanupCandidates(candidates, countCandidates, maxDeletions);
                        }
                    }

                    if (hasPolicy && policy!.MaxDays is > 0)
                    {
                        var cutoffTicks = ToTicks(now.AddDays(-policy.MaxDays.Value));
                        var ageCandidates = await OldestCleanupCandidates(terminal.Where(item =>
                                (item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks) < cutoffTicks))
                            .Take(maxDeletions)
                            .ToArrayAsync(token);
                        MergeCleanupCandidates(candidates, ageCandidates, maxDeletions);
                    }
                }

                if (definitions.Length < CLEANUP_DEFINITION_PAGE_SIZE)
                {
                    break;
                }

                definitionOffset += definitions.Length;
            }

            return (IReadOnlyList<string>)candidates
                .OrderBy(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => pair.Key)
                .ToArray();
        }, cancellationToken);
    }

    private static IQueryable<JobExecutionEntity> TerminalExecutions(
        JobSchedulerDbContext dbContext,
        string schedulerScopeKey) =>
        dbContext.Executions.AsNoTracking()
            .Where(item => item.SchedulerScopeKey == schedulerScopeKey
                           && (item.State == JobExecutionState.Succeeded
                               || item.State == JobExecutionState.Failed
                               || item.State == JobExecutionState.Cancelled
                               || item.State == JobExecutionState.Skipped));

    private static IQueryable<ExecutionCleanupCandidate> OldestCleanupCandidates(
        IQueryable<JobExecutionEntity> terminal) =>
        terminal
            .OrderBy(item => item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks)
            .ThenBy(item => item.InstanceId)
            .Select(item => new ExecutionCleanupCandidate
            {
                InstanceId = item.InstanceId,
                SortTicks = item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks
            });

    private static void MergeCleanupCandidates(
        Dictionary<string, long> selected,
        IEnumerable<ExecutionCleanupCandidate> additions,
        int maxDeletions)
    {
        foreach (var addition in additions)
        {
            selected[addition.InstanceId] = addition.SortTicks;
        }
        if (selected.Count <= maxDeletions)
        {
            return;
        }

        var retained = selected
            .OrderBy(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(maxDeletions)
            .ToArray();
        selected.Clear();
        foreach (var pair in retained)
        {
            selected.Add(pair.Key, pair.Value);
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteExecutionsAsync(
        string schedulerScopeKey,
        IEnumerable<string> instanceIds,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ArgumentNullException.ThrowIfNull(instanceIds);
        var ids = instanceIds.Distinct(StringComparer.Ordinal).ToArray();
        return WriteAsync(async (dbContext, token) =>
        {
            var executions = await dbContext.Executions
                .Where(item => item.SchedulerScopeKey == schedulerScopeKey
                               && ids.Contains(item.InstanceId)
                               && (item.State == JobExecutionState.Succeeded
                                   || item.State == JobExecutionState.Failed
                                   || item.State == JobExecutionState.Cancelled
                                   || item.State == JobExecutionState.Skipped))
                .ToListAsync(token);
            dbContext.Executions.RemoveRange(executions);
            return executions.Count;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionLease>> ClaimAsync(
        JobClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return ClaimCoreAsync(request, cancellationToken);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Scope, string Owner), string> _claimPositions = new();

    private async Task<IReadOnlyList<JobExecutionLease>> ClaimCoreAsync(
        JobClaimRequest request,
        CancellationToken cancellationToken)
    {
        // The eligibility hint and every per-job transaction share one request budget; moving to another Gate
        // must not reset the deadline while earlier grants are already aging.
        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        cancellationToken = deadline.Token;
        // A read-only eligibility hint keeps empty, future-only and fully saturated queues out of write
        // transactions. The claim transaction still rechecks eligibility and its manifest under the Gate protocol.
        var hasDueCapacity = await ReadAsync((context, token) => HasDueClaimCapacityAsync(context, request, token),
            cancellationToken, nameof(ClaimAsync));
        if (!hasDueCapacity) return [];

        var leases = new List<JobExecutionLease>(request.MaxCount);
        var positionKey = (request.SchedulerScopeKey, request.OwnerKey);
        _claimPositions.TryGetValue(positionKey, out var afterJob);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (leases.Count < request.MaxCount && visited.Count < request.GetCandidateBudget())
            {
                var batch = await WriteAsync(async (context, token) =>
                {
                    if (request.OwnerManifestHash is { } expectedManifest)
                    {
                        var accepted = await context.SchedulerOwners.AsNoTracking()
                            .Where(row => row.SchedulerScopeKey == request.SchedulerScopeKey && row.OwnerKey == request.OwnerKey)
                            .Select(row => row.ManifestHash).SingleOrDefaultAsync(token);
                        if (accepted != expectedManifest) return Array.Empty<JobExecutionLease>();
                    }
                    var now = await GetUtcNowAsync(context, token);
                    var gate = await LockClaimGateAsync(context, request, afterJob ?? string.Empty, visited, now, token);
                    if (gate is null)
                    {
                        return Array.Empty<JobExecutionLease>();
                    }
                    afterJob = gate.JobKey;
                    visited.Add(gate.JobKey);
                    now = await GetUtcNowAsync(context, token);
                    var nowTicks = ToTicks(now);
                    var available = Math.Min(request.MaxCount - leases.Count, gate.MaxConcurrency - gate.ActiveCount);
                    var candidates = await context.Executions
                        .Where(row => row.SchedulerScopeKey == request.SchedulerScopeKey && row.OwnerKey == request.OwnerKey
                                      && row.JobKey == gate.JobKey && row.State == JobExecutionState.Queued
                                      && row.AvailableAtUtcTicks <= nowTicks)
                        .OrderBy(row => row.AvailableAtUtcTicks).ThenBy(row => row.CreatedAtUtcTicks).ThenBy(row => row.InstanceId)
                        .Take(available).ToListAsync(token);
                    var claimed = new List<JobExecutionLease>(candidates.Count);
                    foreach (var execution in candidates)
                    {
                        gate.ActiveCount++;
                        execution.State = JobExecutionState.Running;
                        execution.StartedAtUtcTicks = nowTicks;
                        execution.CompletedAtUtcTicks = null;
                        execution.ExecutionAttempt++;
                        execution.RunningWorkerInstanceId = request.WorkerInstanceId;
                        execution.ExecutionLeaseToken = NewToken();
                        execution.LeaseExpiresAtUtcTicks = ToTicks(now.Add(request.LeaseDuration));
                        execution.CancellationRequestedAtUtcTicks = null;
                        AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                            $"Claimed by worker '{request.WorkerInstanceId}'", request.WorkerInstanceId,
                            JobExecutionState.Queued, JobExecutionState.Running);
                        claimed.Add(new JobExecutionLease
                        {
                            GrantedAtUtc = now,
                            Execution = ToExecution(execution),
                            LeaseKey = new JobLeaseKey
                            {
                                SchedulerScopeKey = execution.SchedulerScopeKey,
                                InstanceId = execution.InstanceId,
                                WorkerInstanceId = request.WorkerInstanceId,
                                LeaseToken = execution.ExecutionLeaseToken
                            }
                        });
                    }
                    return claimed.ToArray();
                }, cancellationToken);
                if (afterJob is not null)
                {
                    _claimPositions[positionKey] = afterJob;
                }
                if (batch.Length == 0)
                {
                    break;
                }
                leases.AddRange(batch);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
                                                  && !callerToken.IsCancellationRequested && leases.Count > 0)
        {
            // Earlier batches are confirmed commits and can still be supervised. Never include the interrupted
            // batch: an unknown commit is left for lease recovery. Caller cancellation still aborts delivery.
        }
        return leases;
    }

    /// <inheritdoc />
    public Task<JobLeaseRenewalResult> RenewLeaseAsync(
        JobLeaseKey leaseKey,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseKey);
        leaseKey.Validate();
        ValidatePositiveDuration(leaseDuration, nameof(leaseDuration));
        return WriteAsync(async (dbContext, token) =>
        {
            await LockExecutionAsync(dbContext, leaseKey.SchedulerScopeKey, leaseKey.InstanceId, token);
            var now = await GetUtcNowAsync(dbContext, token);
            var execution = await LoadCurrentExecutionLeaseAsync(dbContext, leaseKey, now, token);
            if (execution is null)
            {
                return new JobLeaseRenewalResult { Status = JobLeaseRenewalStatus.Lost };
            }

            var expiryTicks = ToTicks(now.Add(leaseDuration));
            var changed = await dbContext.Executions
                .Where(row => row.SchedulerScopeKey == leaseKey.SchedulerScopeKey && row.InstanceId == leaseKey.InstanceId
                              && row.State == JobExecutionState.Running && row.RunningWorkerInstanceId == leaseKey.WorkerInstanceId
                              && row.ExecutionLeaseToken == leaseKey.LeaseToken && row.LeaseExpiresAtUtcTicks > ToTicks(now))
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseExpiresAtUtcTicks, expiryTicks), token);
            return changed == 0 ? new JobLeaseRenewalResult { Status = JobLeaseRenewalStatus.Lost }
                : new JobLeaseRenewalResult
                {
                    Status = execution.CancellationRequestedAtUtcTicks.HasValue
                        ? JobLeaseRenewalStatus.CancellationRequested : JobLeaseRenewalStatus.Active,
                    GrantedAtUtc = now,
                    LeaseExpiresAtUtc = FromTicks(expiryTicks)
                };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobAttemptCompletionResult> CompleteAttemptAsync(
        JobAttemptCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        completion.Validate();
        return WriteExecutionAsync(completion.LeaseKey.SchedulerScopeKey, completion.LeaseKey.InstanceId, async (dbContext, token) =>
            await CompleteAttemptAsync(dbContext, completion, await GetUtcNowAsync(dbContext, token), token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobAttemptCompletionResult> ReleaseLeaseAsync(
        JobLeaseKey leaseKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseKey);
        leaseKey.Validate();
        return CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = leaseKey,
            Outcome = JobAttemptOutcome.Abandoned,
            Message = "Worker released the execution after user code exited"
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobCancellationResult> RequestCancellationAsync(
        string schedulerScopeKey,
        string instanceId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(instanceId, nameof(instanceId));
        return WriteExecutionAsync(schedulerScopeKey, instanceId, async (dbContext, token) =>
        {
            var execution = await LoadExecutionAsync(
                dbContext,
                schedulerScopeKey,
                instanceId,
                true,
                false,
                token);
            if (execution is null)
            {
                return new JobCancellationResult { Status = JobCancellationStatus.NotFound };
            }
            if (IsTerminal(execution.State))
            {
                return new JobCancellationResult
                {
                    Status = JobCancellationStatus.AlreadyTerminal,
                    Execution = ToExecution(execution)
                };
            }

            var now = await GetUtcNowAsync(dbContext, token);
            var message = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested" : reason;
            if (execution.State == JobExecutionState.Queued
                || execution.LeaseExpiresAtUtcTicks is null
                || execution.LeaseExpiresAtUtcTicks <= ToTicks(now))
            {
                var worker = execution.RunningWorkerInstanceId;
                if (execution.State == JobExecutionState.Running)
                {
                    await ReleaseExecutionGateAsync(dbContext, execution, token);
                }
                var previous = execution.State;
                execution.State = JobExecutionState.Cancelled;
                execution.CompletedAtUtcTicks = ToTicks(now);
                execution.CancellationRequestedAtUtcTicks = ToTicks(now);
                ClearExecutionLease(execution);
                execution.ConcurrencyToken = NewVersion();
                AddHistory(execution, now, JobExecutionHistoryKind.StateTransition, message, worker,
                    previous, JobExecutionState.Cancelled);
                return new JobCancellationResult
                {
                    Status = JobCancellationStatus.Cancelled,
                    Execution = ToExecution(execution)
                };
            }

            if (!execution.CancellationRequestedAtUtcTicks.HasValue)
            {
                execution.CancellationRequestedAtUtcTicks = ToTicks(now);
                execution.ConcurrencyToken = NewVersion();
                AddHistory(
                    execution,
                    now,
                    JobExecutionHistoryKind.Cancellation,
                    message,
                    execution.RunningWorkerInstanceId);
            }
            return new JobCancellationResult
            {
                Status = JobCancellationStatus.CancellationRequested,
                Execution = ToExecution(execution)
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobLeaseMutationStatus> AppendExecutionLogAsync(
        JobExecutionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.LeaseKey);
        entry.LeaseKey.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Message);
        return WriteAsync(async (dbContext, token) =>
        {
            await LockExecutionAsync(dbContext, entry.LeaseKey.SchedulerScopeKey, entry.LeaseKey.InstanceId, token);
            var now = await GetUtcNowAsync(dbContext, token);
            var execution = await LoadCurrentExecutionLeaseAsync(dbContext, entry.LeaseKey, now, token);
            if (execution is null)
            {
                return JobLeaseMutationStatus.Lost;
            }

            execution.ConcurrencyToken = NewVersion();
            AddHistory(execution, now, JobExecutionHistoryKind.ExecutionLog, entry.Message,
                entry.LeaseKey.WorkerInstanceId, logLevel: entry.LogLevel);
            return JobLeaseMutationStatus.Applied;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionInstance>> RecoverExpiredLeasesAsync(
        ExpiredLeaseRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        request.CoordinatorLease?.ValidatePartition(request.SchedulerScopeKey, "maintenance");
        return RecoverExpiredLeasesCoreAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<JobExecutionInstance>> RecoverExpiredLeasesCoreAsync(
        ExpiredLeaseRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        cancellationToken = deadline.Token;
        var candidates = await ReadAsync(async (context, token) =>
        {
            var nowTicks = ToTicks(await GetUtcNowAsync(context, token));
            return await context.Executions.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == request.SchedulerScopeKey && row.State == JobExecutionState.Running
                              && row.LeaseExpiresAtUtcTicks <= nowTicks)
                .OrderBy(row => row.LeaseExpiresAtUtcTicks).ThenBy(row => row.InstanceId)
                .Select(row => row.InstanceId).Take(request.MaxCount).ToArrayAsync(token);
        }, cancellationToken);
        var recovered = new List<JobExecutionInstance>();
        foreach (var instance in candidates)
        {
            var result = await WriteExecutionAsync(request.SchedulerScopeKey, instance, async (dbContext, token) =>
            {
                var now = await GetUtcNowAsync(dbContext, token);
                var nowTicks = ToTicks(now);
                var execution = await dbContext.Executions.SingleOrDefaultAsync(row =>
                    row.SchedulerScopeKey == request.SchedulerScopeKey && row.InstanceId == instance
                    && row.State == JobExecutionState.Running && row.LeaseExpiresAtUtcTicks <= nowTicks, token);
                if (execution is null)
                {
                    return null;
                }
                var worker = execution.RunningWorkerInstanceId;
                await ReleaseExecutionGateAsync(dbContext, execution, token);
                if (execution.CancellationRequestedAtUtcTicks.HasValue)
                {
                    execution.State = JobExecutionState.Cancelled;
                    execution.CompletedAtUtcTicks = nowTicks;
                    AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                        "Expired lease recovered after cancellation was requested", worker,
                        JobExecutionState.Running, JobExecutionState.Cancelled);
                }
                else
                {
                    execution.LeaseLossCount++;
                    // The template snapshot carries the immutable retry and timeout budget the recovery enforces;
                    // the worker-side timeout timer alone must never decide an over-budget attempt's fate.
                    var template = Deserialize<JobExecutionTemplate>(execution.TemplateJson);
                    var outcome = request.ResolveOutcome(
                        now,
                        FromTicks(execution.StartedAtUtcTicks),
                        template.MaxExecutionTimeout,
                        execution.LeaseLossCount,
                        execution.RetryAttempt,
                        template.RetryCount);
                    execution.State = outcome.NewState;
                    if (outcome.ConsumesRetryAttempt)
                    {
                        execution.RetryAttempt++;
                    }

                    if (outcome.AvailableAtUtc is { } availableAtUtc)
                    {
                        execution.AvailableAtUtcTicks = ToTicks(availableAtUtc);
                    }

                    if (outcome.NewState == JobExecutionState.Failed)
                    {
                        execution.CompletedAtUtcTicks = nowTicks;
                    }

                    AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                        outcome.HistoryMessage, worker,
                        JobExecutionState.Running, outcome.NewState, outcome.HistoryLogLevel);
                }
                ClearExecutionLease(execution);
                execution.ConcurrencyToken = NewVersion();


                return ToExecution(execution);
            }, cancellationToken, request.CoordinatorLease);
            if (result is not null)
            {
                recovered.Add(result);
            }
        }
        return recovered;
    }

    internal async Task<JobDefinition> ResolvePresentDefinitionAsync(
        JobSchedulerDbContext dbContext,
        string schedulerScopeKey,
        string ownerKey,
        string jobKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Definitions.SingleOrDefaultAsync(
            item => item.SchedulerScopeKey == schedulerScopeKey
                    && item.OwnerKey == ownerKey
                    && item.JobKey == jobKey,
            cancellationToken)
            ?? throw new JobDefinitionNotFoundException(
                $"Job '{ownerKey}/{jobKey}' was not found in scope '{schedulerScopeKey}'.");
        if (!entity.IsPresent)
        {
            throw new JobDefinitionNotFoundException(
                $"Job '{ownerKey}/{jobKey}' is absent from the latest owner snapshot in scope '{schedulerScopeKey}'.");
        }

        return ToDefinition(entity);
    }

    private async Task<JobExecutionTemplate> ResolveTriggeredExecutionTemplateAsync(
        JobSchedulerDbContext dbContext,
        JobEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        var definition = await ResolvePresentDefinitionAsync(
            dbContext,
            request.SchedulerScopeKey,
            request.OwnerKey,
            request.JobKey,
            cancellationToken);
        if (definition.IsDisabled)
        {
            throw new InvalidOperationException($"Job '{request.OwnerKey}/{request.JobKey}' is disabled.");
        }

        var template = definition.CreateExecutionTemplate();
        if (template.JobType != JobType.Triggered)
        {
            throw new InvalidOperationException(
                $"Job '{request.OwnerKey}/{request.JobKey}' is recurring and cannot be admitted through the triggered queue API.");
        }

        if (string.IsNullOrWhiteSpace(request.JobArgs))
        {
            throw new ArgumentException("Triggered job arguments cannot be empty.", nameof(request));
        }

        return template;
    }

    private async Task<JobExecutionTemplate> ResolveRecurringExecutionTemplateAsync(
        JobSchedulerDbContext dbContext,
        JobRecurringRunNowCommand command,
        CancellationToken cancellationToken)
    {
        var definition = await ResolvePresentDefinitionAsync(
            dbContext,
            command.SchedulerScopeKey,
            command.OwnerKey,
            command.JobKey,
            cancellationToken);
        var template = definition.CreateExecutionTemplate();
        if (template.JobType != JobType.Recurring)
        {
            throw new InvalidOperationException(
                $"Job '{command.OwnerKey}/{command.JobKey}' is triggered and cannot be admitted through the recurring run-now API.");
        }

        return template;
    }

    internal async Task<JobExecutionInstance> EnqueueCapturedExecutionAsync(
        JobSchedulerDbContext dbContext,
        JobEnqueueRequest request,
        JobExecutionTemplate template,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        JobExecutionOrigin origin,
        DateTimeOffset? recurringOccurrenceUtc = null,
        JobExecutionSkipReason? skipReason = null,
        string? historyMessage = null)
    {
        JobExecutionAdmission.Validate(template, origin, recurringOccurrenceUtc, skipReason);
        var availableAtUtc = request.AvailableAtUtc ?? now;
        long? recurringOccurrenceTicks = recurringOccurrenceUtc is { } occurrence
            ? ToTicks(occurrence)
            : null;
        var existing = await LoadExecutionAsync(
            dbContext,
            template.SchedulerScopeKey,
            request.InstanceId,
            true,
            false,
            cancellationToken);
        if (existing is not null)
        {
            if (Deserialize<JobExecutionTemplate>(existing.TemplateJson) != template
                || !string.Equals(existing.JobArgs, request.JobArgs, StringComparison.Ordinal)
                || existing.Origin != origin
                || existing.RecurringOccurrenceUtcTicks != recurringOccurrenceTicks
                || existing.SkipReason != skipReason
                || (request.AvailableAtUtc is not null
                    && existing.AvailableAtUtcTicks != ToTicks(availableAtUtc)))
            {
                throw new InvalidOperationException(
                    $"Execution identifier '{request.InstanceId}' was reused for a different enqueue request.");
            }

            return ToExecution(existing);
        }

        var entity = new JobExecutionEntity
        {
            SchedulerScopeKey = template.SchedulerScopeKey,
            InstanceId = request.InstanceId,
            TemplateJson = Serialize(template),
            OwnerKey = template.OwnerKey,
            JobKey = template.JobKey,
            JobArgs = request.JobArgs,
            Origin = origin,
            RecurringOccurrenceUtcTicks = recurringOccurrenceTicks,
            SkipReason = skipReason,
            AvailableAtUtcTicks = ToTicks(availableAtUtc),
            State = skipReason is null ? JobExecutionState.Queued : JobExecutionState.Skipped,
            CreatedAtUtcTicks = ToTicks(now),
            CompletedAtUtcTicks = skipReason is null ? null : ToTicks(now),
            ConcurrencyToken = NewVersion()
        };
        AddHistory(
            entity,
            now,
            JobExecutionHistoryKind.StateTransition,
            historyMessage ?? request.EnqueueReason,
            newState: entity.State,
            logLevel: skipReason is null ? LogLevel.Information : LogLevel.Warning);
        dbContext.Executions.Add(entity);
        return ToExecution(entity);
    }

    internal async Task<int> CountOutstandingExecutionsAsync(
        JobSchedulerDbContext dbContext,
        JobExecutionTemplate template,
        CancellationToken cancellationToken)
    {
        var capacity = await ResolveCurrentMaxConcurrencyAsync(dbContext, template, cancellationToken);
        return await dbContext.Executions.AsNoTracking()
            .Where(item => item.SchedulerScopeKey == template.SchedulerScopeKey && item.OwnerKey == template.OwnerKey
                           && item.JobKey == template.JobKey
                           && (item.State == JobExecutionState.Queued || item.State == JobExecutionState.Running))
            .Select(item => item.InstanceId).Take(capacity).CountAsync(cancellationToken);
    }

    internal async Task<int> ResolveCurrentMaxConcurrencyAsync(
        JobSchedulerDbContext dbContext,
        JobExecutionTemplate template,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecutionGates.AsNoTracking()
            .Where(item => item.SchedulerScopeKey == template.SchedulerScopeKey
                           && item.OwnerKey == template.OwnerKey
                           && item.JobKey == template.JobKey)
            .Select(item => (int?)item.MaxConcurrency)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Job '{template.OwnerKey}/{template.JobKey}' has no concurrency-gate projection.");
    }

    private static JobExecutionInstance ResolveIdempotentEnqueue(
        JobEnqueueRequest request,
        JobExecutionEntity existing,
        JobExecutionOrigin expectedOrigin)
    {
        var template = Deserialize<JobExecutionTemplate>(existing.TemplateJson);
        var expectedTemplateMatches = string.Equals(request.JobKey, template.JobKey, StringComparison.Ordinal)
                                      && string.Equals(request.OwnerKey, template.OwnerKey, StringComparison.Ordinal);
        if (!expectedTemplateMatches
            || existing.Origin != expectedOrigin
            || !string.Equals(existing.JobArgs, request.JobArgs, StringComparison.Ordinal)
            || (request.AvailableAtUtc is { } requestedAvailability
                && existing.AvailableAtUtcTicks != ToTicks(requestedAvailability)))
        {
            throw new InvalidOperationException(
                $"Execution identifier '{request.InstanceId}' was reused for a different enqueue request.");
        }

        return ToExecution(existing);
    }

    private async Task<JobAttemptCompletionResult> CompleteAttemptAsync(
        JobSchedulerDbContext dbContext,
        JobAttemptCompletion completion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var execution = await LoadCurrentExecutionLeaseAsync(dbContext, completion.LeaseKey, now, cancellationToken);
        if (execution is null)
        {
            return new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Lost };
        }

        var worker = execution.RunningWorkerInstanceId;
        await ReleaseExecutionGateAsync(dbContext, execution, cancellationToken);
        var outcome = execution.CancellationRequestedAtUtcTicks.HasValue ? JobAttemptOutcome.Cancelled : completion.Outcome;
        var nowTicks = ToTicks(now);
        switch (outcome)
        {
            case JobAttemptOutcome.Succeeded:
                execution.State = JobExecutionState.Succeeded;
                execution.CompletedAtUtcTicks = nowTicks;
                AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                    completion.Message ?? "Execution completed successfully", worker,
                    JobExecutionState.Running, JobExecutionState.Succeeded);
                break;
            case JobAttemptOutcome.Cancelled:
                execution.State = JobExecutionState.Cancelled;
                execution.CompletedAtUtcTicks = nowTicks;
                AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                    completion.Message ?? "Execution observed cancellation", worker,
                    JobExecutionState.Running, JobExecutionState.Cancelled);
                break;
            case JobAttemptOutcome.Abandoned:
                execution.State = JobExecutionState.Queued;
                execution.AvailableAtUtcTicks = nowTicks;
                AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                    completion.Message ?? "Execution released for another worker", worker,
                    JobExecutionState.Running, JobExecutionState.Queued);
                break;
            case JobAttemptOutcome.Failed:
                execution.RetryAttempt++;
                var template = Deserialize<JobExecutionTemplate>(execution.TemplateJson);
                if (execution.RetryAttempt <= template.RetryCount)
                {
                    execution.State = JobExecutionState.Queued;
                    execution.AvailableAtUtcTicks = ToTicks(now.Add(completion.RetryDelay));
                    AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                        completion.Message ?? $"Execution failed; retry {execution.RetryAttempt} queued", worker,
                        JobExecutionState.Running, JobExecutionState.Queued, LogLevel.Warning);
                }
                else
                {
                    execution.State = JobExecutionState.Failed;
                    execution.CompletedAtUtcTicks = nowTicks;
                    AddHistory(execution, now, JobExecutionHistoryKind.StateTransition,
                        completion.Message ?? "Execution failed and exhausted its retries", worker,
                        JobExecutionState.Running, JobExecutionState.Failed, LogLevel.Error);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(completion), completion.Outcome, "Unsupported outcome.");
        }

        ClearExecutionLease(execution);
        execution.ConcurrencyToken = NewVersion();
        return new JobAttemptCompletionResult
        {
            Status = JobAttemptCompletionStatus.Applied,
            Execution = ToExecution(execution)
        };
    }

    private static Task<JobExecutionEntity?> LoadCurrentExecutionLeaseAsync(
        JobSchedulerDbContext dbContext,
        JobLeaseKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.Executions.SingleOrDefaultAsync(item =>
            item.SchedulerScopeKey == key.SchedulerScopeKey
            && item.InstanceId == key.InstanceId
            && item.State == JobExecutionState.Running
            && item.RunningWorkerInstanceId == key.WorkerInstanceId
            && item.ExecutionLeaseToken == key.LeaseToken
            && item.LeaseExpiresAtUtcTicks > ToTicks(now), cancellationToken);

    private static Task<JobExecutionEntity?> LoadExecutionAsync(
        JobSchedulerDbContext dbContext,
        string schedulerScopeKey,
        string instanceId,
        bool tracked,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        IQueryable<JobExecutionEntity> query = dbContext.Executions;
        if (!tracked)
        {
            query = query.AsNoTracking();
        }
        if (includeHistory)
        {
            query = query.Include(item => item.History);
        }
        return query.SingleOrDefaultAsync(item =>
            item.SchedulerScopeKey == schedulerScopeKey && item.InstanceId == instanceId, cancellationToken);
    }

    private static async Task ReleaseExecutionGateAsync(
        JobSchedulerDbContext dbContext,
        JobExecutionEntity execution,
        CancellationToken cancellationToken)
    {
        var gate = await dbContext.ExecutionGates.SingleOrDefaultAsync(item =>
                item.SchedulerScopeKey == execution.SchedulerScopeKey
                && item.OwnerKey == execution.OwnerKey
                && item.JobKey == execution.JobKey, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Execution gate '{execution.OwnerKey}/{execution.JobKey}' does not exist.");
        if (gate.ActiveCount < 1)
        {
            throw new InvalidOperationException(
                $"Execution gate '{execution.OwnerKey}/{execution.JobKey}' has no active lease to release.");
        }
        gate.ActiveCount--;
        gate.ConcurrencyToken = NewVersion();
    }

    private static void ClearExecutionLease(JobExecutionEntity entity)
    {
        entity.RunningWorkerInstanceId = null;
        entity.ExecutionLeaseToken = null;
        entity.LeaseExpiresAtUtcTicks = null;
        if (entity.State == JobExecutionState.Queued)
        {
            entity.CancellationRequestedAtUtcTicks = null;
        }
    }

    private void AddHistory(
        JobExecutionEntity execution,
        DateTimeOffset timestamp,
        JobExecutionHistoryKind kind,
        string message,
        string? workerInstanceId = null,
        JobExecutionState? previousState = null,
        JobExecutionState? newState = null,
        LogLevel logLevel = LogLevel.Information)
    {
        execution.History.Add(new JobExecutionHistoryEntity
        {
            SchedulerScopeKey = execution.SchedulerScopeKey,
            InstanceId = execution.InstanceId,
            Sequence = execution.NextHistorySequence++,
            TimestampUtcTicks = ToTicks(timestamp),
            Kind = kind,
            PreviousState = previousState,
            NewState = newState,
            LogLevel = logLevel,
            Message = _historyLimits.NormalizeMessage(message),
            WorkerInstanceId = workerInstanceId
        });
    }

    private static JobExecutionInstance ToExecution(JobExecutionEntity entity) => new()
    {
        InstanceId = entity.InstanceId,
        Template = Deserialize<JobExecutionTemplate>(entity.TemplateJson),
        JobArgs = entity.JobArgs,
        Origin = entity.Origin,
        RecurringOccurrenceUtc = FromTicks(entity.RecurringOccurrenceUtcTicks),
        SkipReason = entity.SkipReason,
        AvailableAtUtc = FromTicks(entity.AvailableAtUtcTicks),
        State = entity.State,
        CreatedAtUtc = FromTicks(entity.CreatedAtUtcTicks),
        StartedAtUtc = FromTicks(entity.StartedAtUtcTicks),
        CompletedAtUtc = FromTicks(entity.CompletedAtUtcTicks),
        ExecutionAttempt = entity.ExecutionAttempt,
        RetryAttempt = entity.RetryAttempt,
        LeaseLossCount = entity.LeaseLossCount,
        RunningWorkerInstanceId = entity.RunningWorkerInstanceId,
        LeaseExpiresAtUtc = FromTicks(entity.LeaseExpiresAtUtcTicks),
        CancellationRequestedAtUtc = FromTicks(entity.CancellationRequestedAtUtcTicks)
    };

    private static JobExecutionInstance ToExecutionDetail(JobExecutionEntity entity) =>
        ToExecution(entity) with
        {
            History = entity.History.OrderBy(item => item.Sequence).Select(item => new JobExecutionHistoryEntry
            {
                TimestampUtc = FromTicks(item.TimestampUtcTicks),
                Kind = item.Kind,
                PreviousState = item.PreviousState,
                NewState = item.NewState,
                LogLevel = item.LogLevel,
                Message = item.Message,
                WorkerInstanceId = item.WorkerInstanceId
            }).ToArray()
        };

    private sealed class ExecutionCleanupCandidate
    {
        public required string InstanceId { get; init; }
        public long SortTicks { get; init; }
    }

    private static bool IsTerminal(JobExecutionState state) =>
        state is JobExecutionState.Succeeded
            or JobExecutionState.Failed
            or JobExecutionState.Cancelled
            or JobExecutionState.Skipped;

    private static void ValidatePositiveDuration(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, duration, "Duration must be greater than zero.");
        }
    }
}
