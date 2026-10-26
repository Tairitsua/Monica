using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

/// <summary>
/// Implements the recurring-cursor half of the relational scheduler store.
/// </summary>
public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<RecurringScheduleSynchronizationResult> SynchronizeRecurringScheduleAsync(
        RecurringScheduleSynchronization synchronization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        synchronization.Validate();
        synchronization.CoordinatorLease?.ValidatePartition(synchronization.CursorKey.SchedulerScopeKey, "owner:" + synchronization.CursorKey.OwnerKey);
        return WriteJobAsync(synchronization.CursorKey.SchedulerScopeKey, synchronization.CursorKey.OwnerKey,
            synchronization.CursorKey.JobKey, async (dbContext, token) =>
        {
            var now = await GetUtcNowAsync(dbContext, token);
            var cursorEntity = await LoadCursorAsync(dbContext, synchronization.CursorKey, true, token);
            var definition = await dbContext.Definitions.SingleOrDefaultAsync(
                item => item.SchedulerScopeKey == synchronization.CursorKey.SchedulerScopeKey
                        && item.OwnerKey == synchronization.CursorKey.OwnerKey
                        && item.JobKey == synchronization.CursorKey.JobKey,
                token);
            if (definition is null
                || !definition.IsPresent
                || definition.JobType != JobType.Recurring)
            {
                if (cursorEntity is not null)
                {
                    dbContext.RecurringCursors.Remove(cursorEntity);
                }

                return new RecurringScheduleSynchronizationResult
                {
                    Status = RecurringScheduleSynchronizationStatus.DefinitionNotRecurring
                };
            }

            var jobDefinition = ToDefinition(definition);
            var template = jobDefinition.CreateExecutionTemplate();
            var schedule = jobDefinition.EffectiveConfiguration.Schedule
                           ?? throw new InvalidOperationException(
                               $"Recurring job '{jobDefinition.OwnerKey}/{jobDefinition.Declaration.JobKey}' has no effective schedule.");
            var suspensionReasons = synchronization.HostSuspensionReasons
                                    & ~JobRecurringScheduleSuspensionReason.OperatorPolicy;
            if (jobDefinition.IsDisabled)
            {
                suspensionReasons |= JobRecurringScheduleSuspensionReason.OperatorPolicy;
            }

            if (cursorEntity is not null)
            {
                var currentTemplate = Deserialize<JobExecutionTemplate>(cursorEntity.TemplateJson);
                var currentSchedule = Deserialize<RecurringScheduleDefinition>(cursorEntity.ScheduleJson);
                if (currentTemplate == template
                    && currentSchedule == schedule
                    && cursorEntity.SuspensionReasons == suspensionReasons)
                {
                    return new RecurringScheduleSynchronizationResult
                    {
                        Status = RecurringScheduleSynchronizationStatus.Unchanged,
                        Cursor = ToCursor(cursorEntity, currentTemplate, currentSchedule)
                    };
                }

                var scheduleChanged = currentSchedule != schedule;
                var resumed = cursorEntity.SuspensionReasons != JobRecurringScheduleSuspensionReason.None
                              && suspensionReasons == JobRecurringScheduleSuspensionReason.None;
                cursorEntity.TemplateJson = Serialize(template);
                cursorEntity.ScheduleJson = Serialize(schedule);
                if (suspensionReasons != JobRecurringScheduleSuspensionReason.None)
                {
                    cursorEntity.NextOccurrenceUtcTicks = null;
                }
                else if (scheduleChanged || resumed)
                {
                    // A schedule replacement and a resume are both prospective: never replay suppressed time.
                    cursorEntity.NextOccurrenceUtcTicks = ToTicks(schedule.GetNextOccurrence(now));
                }

                cursorEntity.SuspensionReasons = suspensionReasons;
                cursorEntity.CursorVersion++;
                cursorEntity.UpdatedAtUtcTicks = ToTicks(now);
                cursorEntity.ConcurrencyToken = NewVersion();
                return new RecurringScheduleSynchronizationResult
                {
                    Status = RecurringScheduleSynchronizationStatus.Updated,
                    Cursor = ToCursor(cursorEntity, template, schedule)
                };
            }

            var created = new JobRecurringCursorEntity
            {
                SchedulerScopeKey = synchronization.CursorKey.SchedulerScopeKey,
                OwnerKey = synchronization.CursorKey.OwnerKey,
                JobKey = synchronization.CursorKey.JobKey,
                TemplateJson = Serialize(template),
                ScheduleJson = Serialize(schedule),
                NextOccurrenceUtcTicks = suspensionReasons != JobRecurringScheduleSuspensionReason.None
                    ? null
                    : ToTicks(schedule.GetNextOccurrence(now)),
                CursorVersion = 1,
                UpdatedAtUtcTicks = ToTicks(now),
                SuspensionReasons = suspensionReasons,
                ConcurrencyToken = NewVersion()
            };
            dbContext.RecurringCursors.Add(created);
            return new RecurringScheduleSynchronizationResult
            {
                Status = RecurringScheduleSynchronizationStatus.Created,
                Cursor = ToCursor(created, template, schedule)
            };
        }, cancellationToken, synchronization.CoordinatorLease);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecurringScheduleCursor>> GetDueRecurringSchedulesAsync(
        string schedulerScopeKey,
        string ownerKey,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Result count must be greater than zero.");
        }
        return ReadAsync(async (dbContext, token) =>
        {
            var nowTicks = ToTicks(await GetUtcNowAsync(dbContext, token));
            var entities = await dbContext.RecurringCursors.AsNoTracking()
                .Where(item => item.SchedulerScopeKey == schedulerScopeKey
                               && item.OwnerKey == ownerKey
                               && item.SuspensionReasons == JobRecurringScheduleSuspensionReason.None
                               && item.NextOccurrenceUtcTicks != null
                               && item.NextOccurrenceUtcTicks <= nowTicks)
                .OrderBy(item => item.NextOccurrenceUtcTicks)
                .ThenBy(item => item.JobKey)
                .Take(maxCount)
                .ToListAsync(token);
            return (IReadOnlyList<RecurringScheduleCursor>)entities.Select(ToCursor).ToList();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RecurringMaterializationResult> TryMaterializeRecurringOccurrenceAsync(
        RecurringOccurrenceMaterialization materialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        materialization.Validate();
        materialization.CoordinatorLease?.ValidatePartition(materialization.CursorKey.SchedulerScopeKey, "owner:" + materialization.CursorKey.OwnerKey);
        return WriteJobAsync(materialization.CursorKey.SchedulerScopeKey, materialization.CursorKey.OwnerKey,
            materialization.CursorKey.JobKey, async (dbContext, token) =>
        {
            var now = await GetUtcNowAsync(dbContext, token);
            var cursor = await LoadCursorAsync(dbContext, materialization.CursorKey, true, token);
            if (cursor is null)
            {
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.CursorNotFound
                };
            }

            var template = Deserialize<JobExecutionTemplate>(cursor.TemplateJson);
            var cursorSchedule = Deserialize<RecurringScheduleDefinition>(cursor.ScheduleJson);
            var definitionEntity = await dbContext.Definitions.AsNoTracking().SingleOrDefaultAsync(
                item => item.SchedulerScopeKey == materialization.CursorKey.SchedulerScopeKey
                        && item.OwnerKey == materialization.CursorKey.OwnerKey
                        && item.JobKey == materialization.CursorKey.JobKey,
                token);
            if (definitionEntity is null
                || !definitionEntity.IsPresent
                || definitionEntity.JobType != JobType.Recurring)
            {
                dbContext.RecurringCursors.Remove(cursor);
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.CursorNotFound
                };
            }

            var definition = ToDefinition(definitionEntity);
            var currentTemplate = definition.CreateExecutionTemplate();
            var currentSchedule = definition.EffectiveConfiguration.Schedule
                                  ?? throw new InvalidOperationException(
                                      $"Recurring job '{definition.OwnerKey}/{definition.Declaration.JobKey}' has no effective schedule.");
            // Snapshot publication and cursor synchronization are separate transactions. A materializer can therefore
            // briefly see a new definition beside the previous cursor; reject that mixed snapshot and let the next
            // scheduling cycle repair the cursor before admission.
            if (template != currentTemplate || cursorSchedule != currentSchedule)
            {
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.StaleCursor,
                    Cursor = ToCursor(cursor, template, cursorSchedule)
                };
            }

            if (cursor.SuspensionReasons != JobRecurringScheduleSuspensionReason.None
                || definition.IsDisabled)
            {
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.Suspended,
                    Cursor = ToCursor(cursor, template)
                };
            }
            var expectedOccurrenceTicks = ToTicks(materialization.ExpectedOccurrenceUtc);
            if (cursor.CursorVersion != materialization.ExpectedVersion
                || cursor.NextOccurrenceUtcTicks != expectedOccurrenceTicks)
            {
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.StaleCursor,
                    Cursor = ToCursor(cursor, template)
                };
            }

            if (expectedOccurrenceTicks > ToTicks(now))
            {
                return new RecurringMaterializationResult
                {
                    Status = RecurringMaterializationStatus.NotDue,
                    Cursor = ToCursor(cursor, template)
                };
            }

            var request = new JobEnqueueRequest
            {
                InstanceId = materialization.InstanceId,
                SchedulerScopeKey = cursor.SchedulerScopeKey,
                OwnerKey = cursor.OwnerKey,
                JobKey = cursor.JobKey,
                AvailableAtUtc = materialization.ExpectedOccurrenceUtc,
                EnqueueReason = $"Recurring occurrence {materialization.ExpectedOccurrenceUtc:O} materialized"
            };
            // Use the database-authoritative clock so a host with a skewed process clock cannot advance the cursor to
            // an incorrect occurrence or replay an already elapsed backlog one item at a time.
            var nextOccurrence = cursorSchedule.GetNextOccurrence(now);
            var outstandingCount = await CountOutstandingExecutionsAsync(dbContext, template, token);
            var currentMaxConcurrency = await ResolveCurrentMaxConcurrencyAsync(dbContext, template, token);
            JobExecutionSkipReason? skipReason = outstandingCount >= currentMaxConcurrency
                ? JobExecutionSkipReason.RecurringCapacityUnavailable
                : null;
            var execution = await EnqueueCapturedExecutionAsync(
                dbContext,
                request,
                template,
                now,
                token,
                JobExecutionOrigin.RecurringSchedule,
                materialization.ExpectedOccurrenceUtc,
                skipReason,
                skipReason is null
                    ? request.EnqueueReason
                    : $"Recurring occurrence {materialization.ExpectedOccurrenceUtc:O} skipped because " +
                      $"{outstandingCount} outstanding execution(s) reached the configured capacity of " +
                      $"{currentMaxConcurrency}");
            cursor.NextOccurrenceUtcTicks = ToTicks(nextOccurrence);
            cursor.CursorVersion++;
            cursor.UpdatedAtUtcTicks = ToTicks(now);
            cursor.ConcurrencyToken = NewVersion();

            return new RecurringMaterializationResult
            {
                Status = RecurringMaterializationStatus.Materialized,
                Execution = execution,
                Cursor = ToCursor(cursor, template)
            };
        }, cancellationToken, materialization.CoordinatorLease);
    }

    private static Task<JobRecurringCursorEntity?> LoadCursorAsync(
        JobSchedulerDbContext dbContext,
        RecurringScheduleCursorKey key,
        bool tracked,
        CancellationToken cancellationToken)
    {
        IQueryable<JobRecurringCursorEntity> query = dbContext.RecurringCursors;
        if (!tracked)
        {
            query = query.AsNoTracking();
        }
        return query.SingleOrDefaultAsync(item =>
            item.SchedulerScopeKey == key.SchedulerScopeKey
            && item.OwnerKey == key.OwnerKey
            && item.JobKey == key.JobKey, cancellationToken);
    }

    private async Task ApplyPolicyToRecurringCursorAsync(
        JobSchedulerDbContext dbContext,
        JobDefinition definition,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (definition.Declaration.JobType != JobType.Recurring)
        {
            return;
        }

        var key = new RecurringScheduleCursorKey
        {
            SchedulerScopeKey = definition.SchedulerScopeKey,
            OwnerKey = definition.OwnerKey,
            JobKey = definition.Declaration.JobKey
        };
        var cursor = await LoadCursorAsync(dbContext, key, true, cancellationToken);
        if (cursor is null)
        {
            return;
        }

        var effective = definition.EffectiveConfiguration;
        var schedule = effective.Schedule
                       ?? throw new InvalidOperationException(
                           $"Recurring job '{definition.OwnerKey}/{definition.Declaration.JobKey}' has no effective schedule.");
        var template = definition.CreateExecutionTemplate();
        var currentSchedule = Deserialize<RecurringScheduleDefinition>(cursor.ScheduleJson);
        var scheduleChanged = currentSchedule != schedule;
        var wasSuspended = cursor.SuspensionReasons != JobRecurringScheduleSuspensionReason.None;
        var hostSuspensionReasons = cursor.SuspensionReasons
                                    & ~JobRecurringScheduleSuspensionReason.OperatorPolicy;
        var suspensionReasons = effective.IsDisabled
            ? hostSuspensionReasons | JobRecurringScheduleSuspensionReason.OperatorPolicy
            : hostSuspensionReasons;

        cursor.TemplateJson = Serialize(template);
        cursor.ScheduleJson = Serialize(schedule);
        if (suspensionReasons != JobRecurringScheduleSuspensionReason.None)
        {
            cursor.NextOccurrenceUtcTicks = null;
        }
        else if (scheduleChanged || wasSuspended)
        {
            // A policy schedule replacement and a resume are both prospective: never replay suppressed time.
            cursor.NextOccurrenceUtcTicks = ToTicks(schedule.GetNextOccurrence(updatedAtUtc));
        }

        cursor.SuspensionReasons = suspensionReasons;
        cursor.CursorVersion++;
        cursor.UpdatedAtUtcTicks = ToTicks(updatedAtUtc);
        cursor.ConcurrencyToken = NewVersion();
    }

    private static RecurringScheduleCursor ToCursor(JobRecurringCursorEntity entity) =>
        ToCursor(
            entity,
            Deserialize<JobExecutionTemplate>(entity.TemplateJson),
            Deserialize<RecurringScheduleDefinition>(entity.ScheduleJson));

    private static RecurringScheduleCursor ToCursor(
        JobRecurringCursorEntity entity,
        JobExecutionTemplate template,
        RecurringScheduleDefinition? schedule = null) => new()
    {
        Key = new RecurringScheduleCursorKey
        {
            SchedulerScopeKey = entity.SchedulerScopeKey,
            OwnerKey = entity.OwnerKey,
            JobKey = entity.JobKey
        },
        Template = template,
        Schedule = schedule ?? Deserialize<RecurringScheduleDefinition>(entity.ScheduleJson),
        NextOccurrenceUtc = FromTicks(entity.NextOccurrenceUtcTicks),
        SuspensionReasons = entity.SuspensionReasons,
        Version = entity.CursorVersion,
        UpdatedAtUtc = FromTicks(entity.UpdatedAtUtcTicks)
    };
}
