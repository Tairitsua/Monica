using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Providers;

/// <summary>
/// Implements the recurring-cursor half of the volatile scheduler store.
/// </summary>
public sealed partial class InMemoryJobSchedulerStore
{
    private readonly Dictionary<DefinitionKey, StoredRecurringCursor> _recurringCursors = [];

    /// <inheritdoc />
    public Task<RecurringScheduleSynchronizationResult> SynchronizeRecurringScheduleAsync(
        RecurringScheduleSynchronization synchronization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        synchronization.Validate();
        synchronization.CoordinatorLease?.ValidatePartition(synchronization.CursorKey.SchedulerScopeKey, "owner:" + synchronization.CursorKey.OwnerKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ValidateCoordinatorLeaseUnsafe(synchronization.CoordinatorLease);
            var key = new DefinitionKey(
                synchronization.CursorKey.SchedulerScopeKey,
                synchronization.CursorKey.OwnerKey,
                synchronization.CursorKey.JobKey);
            _recurringCursors.TryGetValue(key, out var existing);
            if (!_definitions.TryGetValue(key, out var definition)
                || !definition.IsPresent
                || definition.Declaration.JobType != JobType.Recurring)
            {
                if (existing is not null)
                {
                    _recurringCursors.Remove(key);
                }

                return Task.FromResult(new RecurringScheduleSynchronizationResult
                {
                    Status = RecurringScheduleSynchronizationStatus.DefinitionNotRecurring
                });
            }

            var template = definition.CreateExecutionTemplate();
            var schedule = definition.EffectiveConfiguration.Schedule
                           ?? throw new InvalidOperationException(
                               $"Recurring job '{definition.OwnerKey}/{definition.Declaration.JobKey}' has no effective schedule.");
            var suspensionReasons = synchronization.HostSuspensionReasons
                                    & ~JobRecurringScheduleSuspensionReason.OperatorPolicy;
            if (definition.IsDisabled)
            {
                suspensionReasons |= JobRecurringScheduleSuspensionReason.OperatorPolicy;
            }

            if (existing is not null)
            {
                if (existing.Template == template
                    && existing.Schedule == schedule
                    && existing.SuspensionReasons == suspensionReasons)
                {
                    return Task.FromResult(new RecurringScheduleSynchronizationResult
                    {
                        Status = RecurringScheduleSynchronizationStatus.Unchanged,
                        Cursor = ToSnapshot(key, existing)
                    });
                }

                var now = UtcNow;
                var scheduleChanged = existing.Schedule != schedule;
                var resumed = existing.IsSuspended
                              && suspensionReasons == JobRecurringScheduleSuspensionReason.None;
                existing.Template = template;
                existing.Schedule = schedule;
                if (suspensionReasons != JobRecurringScheduleSuspensionReason.None)
                {
                    existing.NextOccurrenceUtc = null;
                }
                else if (scheduleChanged || resumed)
                {
                    // A schedule replacement and a resume are both prospective: never replay suppressed time.
                    existing.NextOccurrenceUtc = schedule.GetNextOccurrence(now);
                }

                existing.SuspensionReasons = suspensionReasons;
                existing.Version++;
                existing.UpdatedAtUtc = now;
                return Task.FromResult(new RecurringScheduleSynchronizationResult
                {
                    Status = RecurringScheduleSynchronizationStatus.Updated,
                    Cursor = ToSnapshot(key, existing)
                });
            }

            var createdAtUtc = UtcNow;
            var stored = new StoredRecurringCursor
            {
                Template = template,
                Schedule = schedule,
                NextOccurrenceUtc = suspensionReasons != JobRecurringScheduleSuspensionReason.None
                    ? null
                    : schedule.GetNextOccurrence(createdAtUtc),
                SuspensionReasons = suspensionReasons,
                Version = 1,
                UpdatedAtUtc = createdAtUtc
            };
            _recurringCursors.Add(key, stored);
            return Task.FromResult(new RecurringScheduleSynchronizationResult
            {
                Status = RecurringScheduleSynchronizationStatus.Created,
                Cursor = ToSnapshot(key, stored)
            });
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecurringScheduleCursor>> GetDueRecurringSchedulesAsync(
        string schedulerScopeKey,
        string ownerKey,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        JobSchedulerIdentity.ValidateStandard(schedulerScopeKey, nameof(schedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(ownerKey, nameof(ownerKey));
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Result count must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = UtcNow;
            IReadOnlyList<RecurringScheduleCursor> result = _recurringCursors
                .Where(pair => string.Equals(pair.Key.SchedulerScopeKey, schedulerScopeKey, StringComparison.Ordinal)
                               && string.Equals(pair.Key.OwnerKey, ownerKey, StringComparison.Ordinal))
                .Where(pair => !pair.Value.IsSuspended && pair.Value.NextOccurrenceUtc <= now)
                .OrderBy(pair => pair.Value.NextOccurrenceUtc)
                .ThenBy(pair => pair.Key.JobKey, StringComparer.Ordinal)
                .Take(maxCount)
                .Select(pair => ToSnapshot(pair.Key, pair.Value))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<RecurringMaterializationResult> TryMaterializeRecurringOccurrenceAsync(
        RecurringOccurrenceMaterialization materialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        materialization.Validate();
        materialization.CoordinatorLease?.ValidatePartition(materialization.CursorKey.SchedulerScopeKey, "owner:" + materialization.CursorKey.OwnerKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ValidateCoordinatorLeaseUnsafe(materialization.CoordinatorLease);
            var expectedOccurrence = NormalizeUtc(materialization.ExpectedOccurrenceUtc);
            var key = new DefinitionKey(
                materialization.CursorKey.SchedulerScopeKey,
                materialization.CursorKey.OwnerKey,
                materialization.CursorKey.JobKey);
            if (!_recurringCursors.TryGetValue(key, out var cursor))
            {
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.CursorNotFound
                });
            }

            var definition = _definitions.GetValueOrDefault(key);
            if (definition is null
                || !definition.IsPresent
                || definition.Declaration.JobType != JobType.Recurring)
            {
                _recurringCursors.Remove(key);
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.CursorNotFound
                });
            }

            var currentTemplate = definition.CreateExecutionTemplate();
            var currentSchedule = definition.EffectiveConfiguration.Schedule
                                  ?? throw new InvalidOperationException(
                                      $"Recurring job '{definition.OwnerKey}/{definition.Declaration.JobKey}' has no effective schedule.");
            // Snapshot publication and cursor synchronization are separate operations so a replica can briefly observe
            // a new definition with the previous cursor. Never materialize with that mixed state; the next scheduling
            // pass will reconcile the cursor before trying again.
            if (cursor.Template != currentTemplate || cursor.Schedule != currentSchedule)
            {
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.StaleCursor,
                    Cursor = ToSnapshot(key, cursor)
                });
            }

            var template = cursor.Template;
            if (cursor.IsSuspended || definition.IsDisabled)
            {
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.Suspended,
                    Cursor = ToSnapshot(key, cursor)
                });
            }

            if (cursor.Version != materialization.ExpectedVersion
                || cursor.NextOccurrenceUtc != expectedOccurrence)
            {
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.StaleCursor,
                    Cursor = ToSnapshot(key, cursor)
                });
            }

            var now = UtcNow;
            if (expectedOccurrence > now)
            {
                return Task.FromResult(new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.NotDue,
                    Cursor = ToSnapshot(key, cursor)
                });
            }

            var request = new JobEnqueueRequest
            {
                InstanceId = materialization.InstanceId,
                SchedulerScopeKey = key.SchedulerScopeKey,
                OwnerKey = key.OwnerKey,
                JobKey = key.JobKey,
                AvailableAtUtc = expectedOccurrence,
                EnqueueReason = $"Recurring occurrence {expectedOccurrence:O} materialized"
            };
            // Use the store clock so a host with a skewed process clock cannot advance the cursor to an incorrect
            // occurrence or replay an already elapsed backlog one item at a time.
            var nextOccurrence = cursor.Schedule.GetNextOccurrence(now);
            var outstandingCount = CountOutstandingExecutionsUnsafe(template);
            var currentMaxConcurrency = ResolveCurrentMaxConcurrencyUnsafe(template);
            JobExecutionSkipReason? skipReason = outstandingCount >= currentMaxConcurrency
                ? JobExecutionSkipReason.RecurringCapacityUnavailable
                : null;
            var execution = EnqueueCapturedUnsafe(
                request,
                template,
                now,
                JobExecutionOrigin.RecurringSchedule,
                expectedOccurrence,
                skipReason,
                skipReason is null
                    ? request.EnqueueReason
                    : $"Recurring occurrence {expectedOccurrence:O} skipped because {outstandingCount} outstanding "
                      + $"execution(s) reached the configured capacity of {currentMaxConcurrency}");
            cursor.NextOccurrenceUtc = nextOccurrence;
            cursor.Version++;
            cursor.UpdatedAtUtc = now;

            return Task.FromResult(new RecurringMaterializationResult
            {
                Status = RecurringMaterializationStatus.Materialized,
                Execution = execution,
                Cursor = ToSnapshot(key, cursor)
            });
        }
    }

    private void ApplyPolicyToRecurringCursorUnsafe(JobDefinition definition, DateTimeOffset updatedAtUtc)
    {
        if (definition.Declaration.JobType != JobType.Recurring)
        {
            return;
        }

        var key = DefinitionKeyOf(definition);
        if (!_recurringCursors.TryGetValue(key, out var cursor))
        {
            return;
        }

        var effective = definition.EffectiveConfiguration;
        var schedule = effective.Schedule
                       ?? throw new InvalidOperationException(
                           $"Recurring job '{definition.OwnerKey}/{definition.Declaration.JobKey}' has no effective schedule.");
        var template = definition.CreateExecutionTemplate();
        var scheduleChanged = cursor.Schedule != schedule;
        var wasSuspended = cursor.IsSuspended;
        var hostSuspensionReasons = cursor.SuspensionReasons
                                    & ~JobRecurringScheduleSuspensionReason.OperatorPolicy;
        var suspensionReasons = effective.IsDisabled
            ? hostSuspensionReasons | JobRecurringScheduleSuspensionReason.OperatorPolicy
            : hostSuspensionReasons;

        cursor.Template = template;
        cursor.Schedule = schedule;
        if (suspensionReasons != JobRecurringScheduleSuspensionReason.None)
        {
            cursor.NextOccurrenceUtc = null;
        }
        else if (scheduleChanged || wasSuspended)
        {
            // A policy schedule replacement and a resume are both prospective: never replay suppressed time.
            cursor.NextOccurrenceUtc = schedule.GetNextOccurrence(updatedAtUtc);
        }

        cursor.SuspensionReasons = suspensionReasons;
        cursor.Version++;
        cursor.UpdatedAtUtc = updatedAtUtc;
    }

    private static RecurringScheduleCursor ToSnapshot(DefinitionKey key, StoredRecurringCursor cursor)
    {
        return new RecurringScheduleCursor
        {
            Key = new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = key.SchedulerScopeKey,
                OwnerKey = key.OwnerKey,
                JobKey = key.JobKey
            },
            Template = cursor.Template,
            Schedule = cursor.Schedule,
            NextOccurrenceUtc = cursor.NextOccurrenceUtc,
            SuspensionReasons = cursor.SuspensionReasons,
            Version = cursor.Version,
            UpdatedAtUtc = cursor.UpdatedAtUtc
        };
    }

    private sealed class StoredRecurringCursor
    {
        public required JobExecutionTemplate Template { get; set; }
        public required RecurringScheduleDefinition Schedule { get; set; }
        public DateTimeOffset? NextOccurrenceUtc { get; set; }
        public JobRecurringScheduleSuspensionReason SuspensionReasons { get; set; }

        public bool IsSuspended => SuspensionReasons != JobRecurringScheduleSuspensionReason.None;
        public long Version { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
