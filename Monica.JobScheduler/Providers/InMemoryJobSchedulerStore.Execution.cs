using Microsoft.Extensions.Logging;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Providers;

/// <summary>
/// Implements the execution half of the volatile scheduler store.
/// </summary>
public sealed partial class InMemoryJobSchedulerStore
{
    private readonly Dictionary<ExecutionKey, StoredExecution> _executionInstances = [];
    private readonly Dictionary<DefinitionKey, StoredExecutionGate> _executionGates = [];

    /// <inheritdoc />
    public Task<JobExecutionInstance> EnqueueAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var executionKey = new ExecutionKey(request.SchedulerScopeKey, request.InstanceId);
            if (_executionInstances.TryGetValue(executionKey, out var existing))
            {
                return Task.FromResult(ResolveIdempotentEnqueueUnsafe(
                    request,
                    existing,
                    JobExecutionOrigin.Triggered));
            }

            var template = ResolveTriggeredExecutionTemplateUnsafe(request);
            return Task.FromResult(EnqueueCapturedUnsafe(
                request,
                template,
                UtcNow,
                JobExecutionOrigin.Triggered));
        }
    }

    /// <inheritdoc />
    public Task<JobExecutionInstance> RunRecurringNowAsync(
        JobRecurringRunNowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var request = JobExecutionAdmission.CreateRecurringRunNowRequest(command);
        lock (_gate)
        {
            var executionKey = new ExecutionKey(command.SchedulerScopeKey, command.InstanceId);
            if (_executionInstances.TryGetValue(executionKey, out var existing))
            {
                return Task.FromResult(ResolveIdempotentEnqueueUnsafe(
                    request,
                    existing,
                    JobExecutionOrigin.RecurringRunNow));
            }

            var template = ResolveRecurringExecutionTemplateUnsafe(command);
            return Task.FromResult(EnqueueCapturedUnsafe(
                request,
                template,
                UtcNow,
                JobExecutionOrigin.RecurringRunNow));
        }
    }

    /// <inheritdoc />
    public Task<JobExecutionInstance?> GetExecutionAsync(
        string schedulerScopeKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(instanceId, nameof(instanceId));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = new ExecutionKey(schedulerScopeKey, instanceId);
            return Task.FromResult(_executionInstances.TryGetValue(key, out var execution)
                ? ToSnapshot(execution)
                : null);
        }
    }

    /// <inheritdoc />
    public Task<QueryResult<JobExecutionInstance>> QueryExecutionsAsync(
        JobExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IEnumerable<StoredExecution> filtered = _executionInstances.Values
                .Where(execution => string.Equals(
                    execution.Template.SchedulerScopeKey,
                    query.SchedulerScopeKey,
                    StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(query.OwnerKey))
            {
                filtered = filtered.Where(execution => string.Equals(
                    execution.Template.OwnerKey,
                    query.OwnerKey,
                    StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query.JobKey))
            {
                filtered = filtered.Where(execution => string.Equals(
                    execution.Template.JobKey,
                    query.JobKey,
                    StringComparison.Ordinal));
            }

            if (query.States is { Count: > 0 })
            {
                var states = query.States.ToHashSet();
                filtered = filtered.Where(execution => states.Contains(execution.State));
            }

            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                filtered = filtered.Where(execution =>
                    execution.InstanceId.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                    || execution.Template.JobKey.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
            }

            if (query.CreatedAfterUtc is { } createdAfter)
            {
                var normalized = NormalizeUtc(createdAfter);
                filtered = filtered.Where(execution => execution.CreatedAtUtc >= normalized);
            }

            if (query.CreatedBeforeUtc is { } createdBefore)
            {
                var normalized = NormalizeUtc(createdBefore);
                filtered = filtered.Where(execution => execution.CreatedAtUtc <= normalized);
            }

            var materialized = filtered.ToList();
            var totalCount = materialized.Count;
            var ordered = OrderExecutions(materialized, query.SortField, query.SortDescending);
            var page = ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .Select(ToSummary)
                .ToList();

            return Task.FromResult(new QueryResult<JobExecutionInstance>(page, totalCount));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobExecutionState, int>> GetExecutionStateStatisticsAsync(
        string schedulerScopeKey,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var executions = _executionInstances.Values.Where(execution =>
                string.Equals(execution.Template.SchedulerScopeKey, schedulerScopeKey, StringComparison.Ordinal));
            if (startTimeUtc is { } start)
            {
                var normalized = NormalizeUtc(start);
                executions = executions.Where(execution => execution.CreatedAtUtc >= normalized);
            }

            if (endTimeUtc is { } end)
            {
                var normalized = NormalizeUtc(end);
                executions = executions.Where(execution => execution.CreatedAtUtc <= normalized);
            }

            var grouped = executions
                .GroupBy(execution => execution.State)
                .ToDictionary(group => group.Key, group => group.Count());
            IReadOnlyDictionary<JobExecutionState, int> result = Enum
                .GetValues<JobExecutionState>()
                .ToDictionary(state => state, state => grouped.GetValueOrDefault(state));
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobId, JobExecutionInstance?>> GetLatestExecutionsAsync(
        string schedulerScopeKey,
        string ownerKey,
        IEnumerable<JobId> jobIds,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(ownerKey, nameof(ownerKey));
        ArgumentNullException.ThrowIfNull(jobIds);
        var requestedIds = jobIds.Distinct().ToArray();
        foreach (var jobId in requestedIds)
        {
            jobId.Validate();
            if (!string.Equals(jobId.OwnerKey, ownerKey, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Requested job '{jobId}' belongs to owner '{jobId.OwnerKey}', not '{ownerKey}'.",
                    nameof(jobIds));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyDictionary<JobId, JobExecutionInstance?> result = requestedIds.ToDictionary(
                jobId => jobId,
                jobId => _executionInstances.Values
                    .Where(execution =>
                        string.Equals(execution.Template.SchedulerScopeKey, schedulerScopeKey, StringComparison.Ordinal)
                        && string.Equals(execution.Template.OwnerKey, ownerKey, StringComparison.Ordinal)
                        && string.Equals(execution.Template.JobKey, jobId.JobKey, StringComparison.Ordinal))
                    .OrderByDescending(execution => execution.CreatedAtUtc)
                    .ThenByDescending(execution => execution.InstanceId, StringComparer.Ordinal)
                    .Select(ToSummary)
                    .FirstOrDefault());
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetExecutionCleanupCandidatesAsync(
        string schedulerScopeKey,
        IReadOnlyDictionary<JobId, JobHistoryRetentionPolicy> retentionPolicies,
        int maxRetainedOrphanedExecutions,
        int maxDeletions,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
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
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = UtcNow;
            var candidates = new Dictionary<string, StoredExecution>(StringComparer.Ordinal);
            var terminalGroups = _executionInstances.Values
                .Where(execution =>
                    string.Equals(execution.Template.SchedulerScopeKey, schedulerScopeKey, StringComparison.Ordinal)
                    && IsTerminal(execution.State))
                .GroupBy(execution => new JobId(execution.Template.OwnerKey, execution.Template.JobKey));

            foreach (var group in terminalGroups)
            {
                var ordered = group
                    .OrderByDescending(execution => execution.CompletedAtUtc ?? execution.CreatedAtUtc)
                    .ThenByDescending(execution => execution.InstanceId, StringComparer.Ordinal)
                    .ToArray();
                var hasPolicy = retentionPolicies.TryGetValue(group.Key, out var policy);
                var maxRecords = hasPolicy
                    ? policy!.MaxRecords
                    : maxRetainedOrphanedExecutions;
                var cutoff = hasPolicy && policy!.MaxDays is > 0
                    ? now.AddDays(-policy.MaxDays.Value)
                    : (DateTimeOffset?)null;

                if (maxRecords > 0)
                {
                    MergeCleanupCandidatesUnsafe(
                        candidates,
                        ordered.Skip(maxRecords)
                            .OrderBy(execution => execution.CompletedAtUtc ?? execution.CreatedAtUtc)
                            .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal)
                            .Take(maxDeletions),
                        maxDeletions);
                }

                if (cutoff is { } cutoffUtc)
                {
                    MergeCleanupCandidatesUnsafe(
                        candidates,
                        ordered.Where(execution =>
                                (execution.CompletedAtUtc ?? execution.CreatedAtUtc) < cutoffUtc)
                            .OrderBy(execution => execution.CompletedAtUtc ?? execution.CreatedAtUtc)
                            .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal)
                            .Take(maxDeletions),
                        maxDeletions);
                }
            }

            IReadOnlyList<string> result = candidates
                .Values
                .OrderBy(execution => execution.CompletedAtUtc ?? execution.CreatedAtUtc)
                .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal)
                .Select(execution => execution.InstanceId)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteExecutionsAsync(
        string schedulerScopeKey,
        IEnumerable<string> instanceIds,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        ArgumentNullException.ThrowIfNull(instanceIds);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var deleted = 0;
            foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
            {
                JobSchedulerIdentity.ValidateStandard(instanceId, nameof(instanceIds));
                var key = new ExecutionKey(schedulerScopeKey, instanceId);
                if (_executionInstances.TryGetValue(key, out var execution)
                    && IsTerminal(execution.State)
                    && _executionInstances.Remove(key))
                {
                    deleted++;
                }
            }

            return Task.FromResult(deleted);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionLease>> ClaimAsync(
        JobClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (request.OwnerManifestHash is { } expectedManifest
                && (!_owners.TryGetValue((request.SchedulerScopeKey, request.OwnerKey), out var owner)
                    || owner.Hash != expectedManifest))
                return Task.FromResult<IReadOnlyList<JobExecutionLease>>([]);
            var now = UtcNow;
            var executableKeys = request.JobKeys.ToHashSet(StringComparer.Ordinal);
            var eligibleExecutions = _executionInstances.Values
                .Where(execution =>
                    execution.State == JobExecutionState.Queued
                    && execution.AvailableAtUtc <= now
                    && string.Equals(execution.Template.SchedulerScopeKey, request.SchedulerScopeKey, StringComparison.Ordinal)
                    && string.Equals(execution.Template.OwnerKey, request.OwnerKey, StringComparison.Ordinal)
                    && executableKeys.Contains(execution.Template.JobKey)
                    && _executionGates.TryGetValue(GetExecutionGateKey(execution.Template), out var eligibleGate)
                    && eligibleGate.ActiveCount < eligibleGate.MaxConcurrency)
                .OrderBy(execution => execution.AvailableAtUtc)
                .ThenBy(execution => execution.CreatedAtUtc)
                .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal);
            var candidateBudget = request.GetCandidateBudget();
            var fairCandidates = eligibleExecutions
                .DistinctBy(execution => execution.Template.JobKey, StringComparer.Ordinal)
                .Take(candidateBudget)
                .ToArray();
            var fairCandidateIds = fairCandidates
                .Select(execution => execution.InstanceId)
                .ToHashSet(StringComparer.Ordinal);
            var candidates = fairCandidates
                .Concat(eligibleExecutions
                    .Where(execution => !fairCandidateIds.Contains(execution.InstanceId))
                    .Take(candidateBudget - fairCandidates.Length))
                .ToArray();
            var leases = new List<JobExecutionLease>(request.MaxCount);

            foreach (var execution in candidates)
            {
                if (leases.Count == request.MaxCount)
                {
                    break;
                }

                var gateKey = GetExecutionGateKey(execution.Template);
                if (!_executionGates.TryGetValue(gateKey, out var gate))
                {
                    throw new InvalidOperationException(
                        $"Job '{execution.Template.OwnerKey}/{execution.Template.JobKey}' has no concurrency-gate projection.");
                }

                if (gate.ActiveCount >= gate.MaxConcurrency)
                {
                    continue;
                }

                gate.ActiveCount++;
                execution.State = JobExecutionState.Running;
                execution.StartedAtUtc = now;
                execution.CompletedAtUtc = null;
                execution.ExecutionAttempt++;
                execution.RunningWorkerInstanceId = request.WorkerInstanceId;
                var executionLeaseToken = NewToken();
                execution.ExecutionLeaseToken = executionLeaseToken;
                execution.LeaseExpiresAtUtc = now.Add(request.LeaseDuration);
                execution.CancellationRequestedAtUtc = null;
                AddHistory(execution, Transition(
                    now,
                    JobExecutionState.Queued,
                    JobExecutionState.Running,
                    $"Claimed by worker '{request.WorkerInstanceId}'",
                    request.WorkerInstanceId));

                leases.Add(new JobExecutionLease
                {
                    GrantedAtUtc = now,
                    Execution = ToSummary(execution),
                    LeaseKey = new JobLeaseKey
                    {
                        SchedulerScopeKey = execution.Template.SchedulerScopeKey,
                        InstanceId = execution.InstanceId,
                        WorkerInstanceId = request.WorkerInstanceId,
                        LeaseToken = executionLeaseToken
                    }
                });
            }

            return Task.FromResult<IReadOnlyList<JobExecutionLease>>(leases);
        }
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
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = UtcNow;
            if (!TryGetCurrentExecutionLeaseUnsafe(leaseKey, now, out var execution))
            {
                return Task.FromResult(new JobLeaseRenewalResult { Status = JobLeaseRenewalStatus.Lost });
            }

            execution.LeaseExpiresAtUtc = now.Add(leaseDuration);
            return Task.FromResult(new JobLeaseRenewalResult
            {
                Status = execution.CancellationRequestedAtUtc.HasValue
                    ? JobLeaseRenewalStatus.CancellationRequested
                    : JobLeaseRenewalStatus.Active,
                GrantedAtUtc = now,
                LeaseExpiresAtUtc = execution.LeaseExpiresAtUtc
            });
        }
    }

    /// <inheritdoc />
    public Task<JobAttemptCompletionResult> CompleteAttemptAsync(
        JobAttemptCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        completion.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(CompleteAttemptUnsafe(completion, UtcNow));
        }
    }

    /// <inheritdoc />
    public Task<JobAttemptCompletionResult> ReleaseLeaseAsync(
        JobLeaseKey leaseKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseKey);
        leaseKey.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(CompleteAttemptUnsafe(
                new JobAttemptCompletion
                {
                    LeaseKey = leaseKey,
                    Outcome = JobAttemptOutcome.Abandoned,
                    Message = "Worker released the execution after user code exited"
                },
                UtcNow));
        }
    }

    /// <inheritdoc />
    public Task<JobCancellationResult> RequestCancellationAsync(
        string schedulerScopeKey,
        string instanceId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(instanceId, nameof(instanceId));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = new ExecutionKey(schedulerScopeKey, instanceId);
            if (!_executionInstances.TryGetValue(key, out var execution))
            {
                return Task.FromResult(new JobCancellationResult { Status = JobCancellationStatus.NotFound });
            }

            if (IsTerminal(execution.State))
            {
                return Task.FromResult(new JobCancellationResult
                {
                    Status = JobCancellationStatus.AlreadyTerminal,
                    Execution = ToSummary(execution)
                });
            }

            var now = UtcNow;
            var message = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested" : reason;
            if (execution.State == JobExecutionState.Queued
                || execution.LeaseExpiresAtUtc is null
                || execution.LeaseExpiresAtUtc <= now)
            {
                if (execution.State == JobExecutionState.Running)
                {
                    ReleaseExecutionGateUnsafe(execution);
                }

                var previousState = execution.State;
                var workerInstanceId = execution.RunningWorkerInstanceId;
                execution.State = JobExecutionState.Cancelled;
                execution.CompletedAtUtc = now;
                execution.CancellationRequestedAtUtc = now;
                ClearExecutionLeaseUnsafe(execution);
                AddHistory(execution, Transition(
                    now,
                    previousState,
                    JobExecutionState.Cancelled,
                    message,
                    workerInstanceId));
                return Task.FromResult(new JobCancellationResult
                {
                    Status = JobCancellationStatus.Cancelled,
                    Execution = ToSummary(execution)
                });
            }

            if (!execution.CancellationRequestedAtUtc.HasValue)
            {
                execution.CancellationRequestedAtUtc = now;
                AddHistory(execution, new JobExecutionHistoryEntry
                {
                    TimestampUtc = now,
                    Kind = JobExecutionHistoryKind.Cancellation,
                    Message = message,
                    WorkerInstanceId = execution.RunningWorkerInstanceId
                });
            }
            return Task.FromResult(new JobCancellationResult
            {
                Status = JobCancellationStatus.CancellationRequested,
                Execution = ToSummary(execution)
            });
        }
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
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = UtcNow;
            if (!TryGetCurrentExecutionLeaseUnsafe(entry.LeaseKey, now, out var execution))
            {
                return Task.FromResult(JobLeaseMutationStatus.Lost);
            }

            AddHistory(execution, new JobExecutionHistoryEntry
            {
                TimestampUtc = now,
                Kind = JobExecutionHistoryKind.ExecutionLog,
                LogLevel = entry.LogLevel,
                Message = entry.Message,
                WorkerInstanceId = entry.LeaseKey.WorkerInstanceId
            });
            return Task.FromResult(JobLeaseMutationStatus.Applied);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionInstance>> RecoverExpiredLeasesAsync(
        ExpiredLeaseRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        request.CoordinatorLease?.ValidatePartition(request.SchedulerScopeKey, "maintenance");

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateCoordinatorLeaseUnsafe(request.CoordinatorLease);
            var now = UtcNow;
            var expired = _executionInstances.Values
                .Where(execution =>
                    execution.State == JobExecutionState.Running
                    && execution.LeaseExpiresAtUtc <= now
                    && string.Equals(
                        execution.Template.SchedulerScopeKey,
                        request.SchedulerScopeKey,
                        StringComparison.Ordinal))
                .OrderBy(execution => execution.LeaseExpiresAtUtc)
                .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal)
                .Take(request.MaxCount)
                .ToArray();

            var recovered = new List<JobExecutionInstance>(expired.Length);
            foreach (var execution in expired)
            {
                var workerInstanceId = execution.RunningWorkerInstanceId;
                ReleaseExecutionGateUnsafe(execution);
                if (execution.CancellationRequestedAtUtc.HasValue)
                {
                    execution.State = JobExecutionState.Cancelled;
                    execution.CompletedAtUtc = now;
                    AddHistory(execution, Transition(
                        now,
                        JobExecutionState.Running,
                        JobExecutionState.Cancelled,
                        "Expired lease recovered after cancellation was requested",
                        workerInstanceId));
                }
                else
                {
                    execution.LeaseLossCount++;
                    // The recovery shares the timeout and retry policy resolution with the EF Core store so both
                    // providers fail over-budget attempts identically.
                    var outcome = request.ResolveOutcome(
                        now,
                        execution.StartedAtUtc,
                        execution.Template.MaxExecutionTimeout,
                        execution.LeaseLossCount,
                        execution.RetryAttempt,
                        execution.Template.RetryCount);
                    execution.State = outcome.NewState;
                    if (outcome.ConsumesRetryAttempt)
                    {
                        execution.RetryAttempt++;
                    }

                    if (outcome.AvailableAtUtc is { } availableAtUtc)
                    {
                        execution.AvailableAtUtc = availableAtUtc;
                    }

                    if (outcome.NewState == JobExecutionState.Failed)
                    {
                        execution.CompletedAtUtc = now;
                    }

                    AddHistory(execution, Transition(
                        now,
                        JobExecutionState.Running,
                        outcome.NewState,
                        outcome.HistoryMessage,
                        workerInstanceId,
                        outcome.HistoryLogLevel));
                }

                ClearExecutionLeaseUnsafe(execution);
                recovered.Add(ToSummary(execution));
            }

            return Task.FromResult<IReadOnlyList<JobExecutionInstance>>(recovered);
        }
    }

    internal JobExecutionTemplate ResolveTriggeredExecutionTemplateUnsafe(JobEnqueueRequest request)
    {
        var definition = ResolvePresentDefinitionUnsafe(request.SchedulerScopeKey, request.OwnerKey, request.JobKey);
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

    internal JobExecutionTemplate ResolveRecurringExecutionTemplateUnsafe(JobRecurringRunNowCommand command)
    {
        var definition = ResolvePresentDefinitionUnsafe(command.SchedulerScopeKey, command.OwnerKey, command.JobKey);
        var template = definition.CreateExecutionTemplate();
        if (template.JobType != JobType.Recurring)
        {
            throw new InvalidOperationException(
                $"Job '{command.OwnerKey}/{command.JobKey}' is triggered and cannot be admitted through the recurring run-now API.");
        }

        return template;
    }

    internal JobExecutionInstance EnqueueCapturedUnsafe(
        JobEnqueueRequest request,
        JobExecutionTemplate template,
        DateTimeOffset now,
        JobExecutionOrigin origin,
        DateTimeOffset? recurringOccurrenceUtc = null,
        JobExecutionSkipReason? skipReason = null,
        string? historyMessage = null)
    {
        JobExecutionAdmission.Validate(template, origin, recurringOccurrenceUtc, skipReason);
        var normalizedAvailableAt = request.AvailableAtUtc is { } requestedAvailability
            ? NormalizeUtc(requestedAvailability)
            : now;
        DateTimeOffset? normalizedOccurrence = recurringOccurrenceUtc is { } occurrence
            ? NormalizeUtc(occurrence)
            : null;
        var key = new ExecutionKey(template.SchedulerScopeKey, request.InstanceId);
        if (_executionInstances.TryGetValue(key, out var existing))
        {
            if (existing.Template != template
                || !string.Equals(existing.JobArgs, request.JobArgs, StringComparison.Ordinal)
                || existing.Origin != origin
                || existing.RecurringOccurrenceUtc != normalizedOccurrence
                || existing.SkipReason != skipReason
                || (request.AvailableAtUtc is not null
                    && existing.AvailableAtUtc != normalizedAvailableAt))
            {
                throw new InvalidOperationException(
                    $"Execution identifier '{request.InstanceId}' was reused for a different enqueue request.");
            }

            return ToSummary(existing);
        }

        var execution = new StoredExecution
        {
            InstanceId = request.InstanceId,
            Template = template,
            JobArgs = request.JobArgs,
            Origin = origin,
            RecurringOccurrenceUtc = normalizedOccurrence,
            SkipReason = skipReason,
            AvailableAtUtc = normalizedAvailableAt,
            State = skipReason is null ? JobExecutionState.Queued : JobExecutionState.Skipped,
            CreatedAtUtc = now,
            CompletedAtUtc = skipReason is null ? null : now
        };
        AddHistory(execution, new JobExecutionHistoryEntry
        {
            TimestampUtc = now,
            Kind = JobExecutionHistoryKind.StateTransition,
            NewState = execution.State,
            LogLevel = skipReason is null ? LogLevel.Information : LogLevel.Warning,
            Message = historyMessage ?? request.EnqueueReason
        });
        _executionInstances.Add(key, execution);
        return ToSummary(execution);
    }

    internal int CountOutstandingExecutionsUnsafe(JobExecutionTemplate template) =>
        _executionInstances.Values.Count(execution =>
            string.Equals(execution.Template.SchedulerScopeKey, template.SchedulerScopeKey, StringComparison.Ordinal)
            && string.Equals(execution.Template.OwnerKey, template.OwnerKey, StringComparison.Ordinal)
            && string.Equals(execution.Template.JobKey, template.JobKey, StringComparison.Ordinal)
            && execution.State is JobExecutionState.Queued or JobExecutionState.Running);

    private static JobExecutionInstance ResolveIdempotentEnqueueUnsafe(
        JobEnqueueRequest request,
        StoredExecution existing,
        JobExecutionOrigin expectedOrigin)
    {
        var template = existing.Template;
        var jobMatches = string.Equals(request.JobKey, template.JobKey, StringComparison.Ordinal)
                         && string.Equals(request.OwnerKey, template.OwnerKey, StringComparison.Ordinal);
        var payloadMatches = string.Equals(existing.JobArgs, request.JobArgs, StringComparison.Ordinal);
        var availabilityMatches = request.AvailableAtUtc is not { } requestedAvailability
                                  || existing.AvailableAtUtc == NormalizeUtc(requestedAvailability);
        if (!jobMatches || !payloadMatches || !availabilityMatches || existing.Origin != expectedOrigin)
        {
            throw new InvalidOperationException(
                $"Execution identifier '{request.InstanceId}' was reused for a different enqueue request.");
        }

        return ToSummary(existing);
    }

    private JobAttemptCompletionResult CompleteAttemptUnsafe(
        JobAttemptCompletion completion,
        DateTimeOffset now)
    {
        if (!TryGetCurrentExecutionLeaseUnsafe(completion.LeaseKey, now, out var execution))
        {
            return new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Lost };
        }

        var workerInstanceId = execution.RunningWorkerInstanceId;
        ReleaseExecutionGateUnsafe(execution);
        var cancellationWins = execution.CancellationRequestedAtUtc.HasValue;
        var outcome = cancellationWins ? JobAttemptOutcome.Cancelled : completion.Outcome;
        var message = completion.Message;

        switch (outcome)
        {
            case JobAttemptOutcome.Succeeded:
                execution.State = JobExecutionState.Succeeded;
                execution.CompletedAtUtc = now;
                AddHistory(execution, Transition(
                    now,
                    JobExecutionState.Running,
                    JobExecutionState.Succeeded,
                    message ?? "Execution completed successfully",
                    workerInstanceId));
                break;

            case JobAttemptOutcome.Cancelled:
                execution.State = JobExecutionState.Cancelled;
                execution.CompletedAtUtc = now;
                AddHistory(execution, Transition(
                    now,
                    JobExecutionState.Running,
                    JobExecutionState.Cancelled,
                    message ?? (cancellationWins
                        ? "Execution cancelled by persisted request"
                        : "Execution observed cancellation"),
                    workerInstanceId));
                break;

            case JobAttemptOutcome.Abandoned:
                execution.State = JobExecutionState.Queued;
                execution.AvailableAtUtc = now;
                AddHistory(execution, Transition(
                    now,
                    JobExecutionState.Running,
                    JobExecutionState.Queued,
                    message ?? "Execution released for another worker",
                    workerInstanceId));
                break;

            case JobAttemptOutcome.Failed:
                execution.RetryAttempt++;
                if (execution.RetryAttempt <= execution.Template.RetryCount)
                {
                    execution.State = JobExecutionState.Queued;
                    execution.AvailableAtUtc = now.Add(completion.RetryDelay);
                    AddHistory(execution, Transition(
                        now,
                        JobExecutionState.Running,
                        JobExecutionState.Queued,
                        message ?? $"Execution failed; retry {execution.RetryAttempt} queued",
                        workerInstanceId,
                        LogLevel.Warning));
                }
                else
                {
                    execution.State = JobExecutionState.Failed;
                    execution.CompletedAtUtc = now;
                    AddHistory(execution, Transition(
                        now,
                        JobExecutionState.Running,
                        JobExecutionState.Failed,
                        message ?? "Execution failed and exhausted its retries",
                        workerInstanceId,
                        LogLevel.Error));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(completion), completion.Outcome, "Unsupported outcome.");
        }

        ClearExecutionLeaseUnsafe(execution);
        return new JobAttemptCompletionResult
        {
            Status = JobAttemptCompletionStatus.Applied,
            Execution = ToSummary(execution)
        };
    }

    private bool TryGetCurrentExecutionLeaseUnsafe(
        JobLeaseKey leaseKey,
        DateTimeOffset now,
        out StoredExecution execution)
    {
        var key = new ExecutionKey(leaseKey.SchedulerScopeKey, leaseKey.InstanceId);
        return _executionInstances.TryGetValue(key, out execution!)
               && execution.State == JobExecutionState.Running
               && execution.LeaseExpiresAtUtc > now
               && string.Equals(execution.RunningWorkerInstanceId, leaseKey.WorkerInstanceId, StringComparison.Ordinal)
               && string.Equals(execution.ExecutionLeaseToken, leaseKey.LeaseToken, StringComparison.Ordinal);
    }

    private void ReleaseExecutionGateUnsafe(StoredExecution execution)
    {
        var gateKey = GetExecutionGateKey(execution.Template);
        if (!_executionGates.TryGetValue(gateKey, out var gate) || gate.ActiveCount < 1)
        {
            throw new InvalidOperationException(
                $"Execution gate '{execution.Template.OwnerKey}/{execution.Template.JobKey}' has no active lease to release.");
        }

        gate.ActiveCount--;
    }

    private static void ClearExecutionLeaseUnsafe(StoredExecution execution)
    {
        execution.RunningWorkerInstanceId = null;
        execution.ExecutionLeaseToken = null;
        execution.LeaseExpiresAtUtc = null;
        if (execution.State == JobExecutionState.Queued)
        {
            execution.CancellationRequestedAtUtc = null;
        }
    }

    internal int ResolveCurrentMaxConcurrencyUnsafe(JobExecutionTemplate template)
    {
        return _executionGates.TryGetValue(GetExecutionGateKey(template), out var gate)
            ? gate.MaxConcurrency
            : throw new InvalidOperationException(
                $"Job '{template.OwnerKey}/{template.JobKey}' has no concurrency-gate projection.");
    }

    internal void ApplyEffectiveConcurrencyUnsafe(JobDefinition definition)
    {
        var key = new DefinitionKey(
            definition.SchedulerScopeKey,
            definition.OwnerKey,
            definition.Declaration.JobKey);
        if (!_executionGates.TryGetValue(key, out var gate))
        {
            gate = new StoredExecutionGate();
            _executionGates.Add(key, gate);
        }

        gate.MaxConcurrency = definition.EffectiveConfiguration.MaxConcurrency;
    }

    private void AddHistory(StoredExecution execution, JobExecutionHistoryEntry entry)
    {
        execution.History.Add(entry with { Message = _historyLimits.NormalizeMessage(entry.Message) });
        var excessCount = execution.History.Count - _historyLimits.MaxEntries;
        if (excessCount > 0)
        {
            execution.History.RemoveRange(0, excessCount);
        }
    }

    private static IOrderedEnumerable<StoredExecution> OrderExecutions(
        IEnumerable<StoredExecution> executions,
        JobExecutionSortField sortField,
        bool descending)
    {
        Func<StoredExecution, DateTimeOffset?> selector = sortField switch
        {
            JobExecutionSortField.CreatedAtUtc => execution => execution.CreatedAtUtc,
            JobExecutionSortField.AvailableAtUtc => execution => execution.AvailableAtUtc,
            JobExecutionSortField.StartedAtUtc => execution => execution.StartedAtUtc,
            JobExecutionSortField.CompletedAtUtc => execution => execution.CompletedAtUtc,
            _ => throw new ArgumentOutOfRangeException(nameof(sortField), sortField, "Unsupported sort field.")
        };
        return descending
            ? executions.OrderByDescending(selector).ThenByDescending(execution => execution.InstanceId, StringComparer.Ordinal)
            : executions.OrderBy(selector).ThenBy(execution => execution.InstanceId, StringComparer.Ordinal);
    }

    private static JobExecutionHistoryEntry Transition(
        DateTimeOffset timestampUtc,
        JobExecutionState previousState,
        JobExecutionState newState,
        string message,
        string? workerInstanceId,
        LogLevel logLevel = LogLevel.Information)
    {
        return new JobExecutionHistoryEntry
        {
            TimestampUtc = timestampUtc,
            Kind = JobExecutionHistoryKind.StateTransition,
            PreviousState = previousState,
            NewState = newState,
            LogLevel = logLevel,
            Message = message,
            WorkerInstanceId = workerInstanceId
        };
    }

    private static JobExecutionInstance ToSnapshot(StoredExecution execution) =>
        CreateSnapshot(execution, true);

    private static JobExecutionInstance ToSummary(StoredExecution execution) =>
        CreateSnapshot(execution, false);

    private static JobExecutionInstance CreateSnapshot(StoredExecution execution, bool includeHistory)
    {
        return new JobExecutionInstance
        {
            InstanceId = execution.InstanceId,
            Template = execution.Template,
            JobArgs = execution.JobArgs,
            Origin = execution.Origin,
            RecurringOccurrenceUtc = execution.RecurringOccurrenceUtc,
            SkipReason = execution.SkipReason,
            AvailableAtUtc = execution.AvailableAtUtc,
            State = execution.State,
            CreatedAtUtc = execution.CreatedAtUtc,
            StartedAtUtc = execution.StartedAtUtc,
            CompletedAtUtc = execution.CompletedAtUtc,
            ExecutionAttempt = execution.ExecutionAttempt,
            RetryAttempt = execution.RetryAttempt,
            LeaseLossCount = execution.LeaseLossCount,
            RunningWorkerInstanceId = execution.RunningWorkerInstanceId,
            LeaseExpiresAtUtc = execution.LeaseExpiresAtUtc,
            CancellationRequestedAtUtc = execution.CancellationRequestedAtUtc,
            History = includeHistory ? execution.History.ToArray() : []
        };
    }

    private static bool IsTerminal(JobExecutionState state)
    {
        return state is JobExecutionState.Succeeded
            or JobExecutionState.Failed
            or JobExecutionState.Cancelled
            or JobExecutionState.Skipped;
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();

    private static void ValidatePositiveDuration(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, duration, "Duration must be greater than zero.");
        }
    }

    private static DefinitionKey GetExecutionGateKey(JobExecutionTemplate template)
    {
        return new DefinitionKey(template.SchedulerScopeKey, template.OwnerKey, template.JobKey);
    }

    private static void MergeCleanupCandidatesUnsafe(
        Dictionary<string, StoredExecution> selected,
        IEnumerable<StoredExecution> additions,
        int maxDeletions)
    {
        foreach (var addition in additions)
        {
            selected[addition.InstanceId] = addition;
        }
        if (selected.Count <= maxDeletions)
        {
            return;
        }

        var retained = selected.Values
            .OrderBy(execution => execution.CompletedAtUtc ?? execution.CreatedAtUtc)
            .ThenBy(execution => execution.InstanceId, StringComparer.Ordinal)
            .Take(maxDeletions)
            .ToArray();
        selected.Clear();
        foreach (var execution in retained)
        {
            selected.Add(execution.InstanceId, execution);
        }
    }

    private readonly record struct ExecutionKey(string SchedulerScopeKey, string InstanceId);

    private sealed class StoredExecution
    {
        public required string InstanceId { get; init; }
        public required JobExecutionTemplate Template { get; init; }
        public string? JobArgs { get; init; }
        public JobExecutionOrigin Origin { get; init; }
        public DateTimeOffset? RecurringOccurrenceUtc { get; init; }
        public JobExecutionSkipReason? SkipReason { get; init; }
        public DateTimeOffset AvailableAtUtc { get; set; }
        public JobExecutionState State { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public int ExecutionAttempt { get; set; }
        public int RetryAttempt { get; set; }
        public int LeaseLossCount { get; set; }
        public string? RunningWorkerInstanceId { get; set; }
        public string? ExecutionLeaseToken { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
        public List<JobExecutionHistoryEntry> History { get; } = [];
    }

    private sealed class StoredExecutionGate
    {
        public int ActiveCount { get; set; }
        public int MaxConcurrency { get; set; }
    }
}
