using Microsoft.Extensions.Logging;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore.Entities;

/// <summary>
/// Persists one owner-published job definition: the immutable declaration snapshot, the sticky operator policy, and
/// searchable projections of the effective configuration. Projection columns are refreshed on snapshot sync and
/// policy edits so definition queries never deserialize payloads.
/// </summary>
internal sealed class JobDefinitionEntity
{
    public string SchedulerScopeKey { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public string DeclarationJson { get; set; } = string.Empty;
    public string PolicyOverridesJson { get; set; } = string.Empty;
    public string PolicyConcurrencyStamp { get; set; } = string.Empty;
    public long PolicyUpdatedAtUtcTicks { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JobType JobType { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsPresent { get; set; }
    public long? AbsentSinceUtcTicks { get; set; }
    public long LastObservedAtUtcTicks { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

internal sealed class JobExecutionEntity
{
    public string SchedulerScopeKey { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string TemplateJson { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public string? JobArgs { get; set; }
    public JobExecutionOrigin Origin { get; set; }
    public long? RecurringOccurrenceUtcTicks { get; set; }
    public JobExecutionSkipReason? SkipReason { get; set; }
    public long AvailableAtUtcTicks { get; set; }
    public JobExecutionState State { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long? StartedAtUtcTicks { get; set; }
    public long? CompletedAtUtcTicks { get; set; }
    public int ExecutionAttempt { get; set; }
    public int RetryAttempt { get; set; }
    public int LeaseLossCount { get; set; }
    public string? RunningWorkerInstanceId { get; set; }
    public string? ExecutionLeaseToken { get; set; }
    public long? LeaseExpiresAtUtcTicks { get; set; }
    public long? CancellationRequestedAtUtcTicks { get; set; }
    public int NextHistorySequence { get; set; } = 1;
    public Guid ConcurrencyToken { get; set; }
    public List<JobExecutionHistoryEntity> History { get; set; } = [];
}

internal sealed class JobExecutionHistoryEntity
{
    public string SchedulerScopeKey { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public long TimestampUtcTicks { get; set; }
    public JobExecutionHistoryKind Kind { get; set; }
    public JobExecutionState? PreviousState { get; set; }
    public JobExecutionState? NewState { get; set; }
    public LogLevel LogLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? WorkerInstanceId { get; set; }
    public JobExecutionEntity Execution { get; set; } = null!;
}

/// <summary>
/// A gate belongs to one owner-scoped definition and owns both its live lease count and current admission capacity.
/// </summary>
internal sealed class JobExecutionGateEntity
{
    public string SchedulerScopeKey { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public int ActiveCount { get; set; }
    public int MaxConcurrency { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

internal sealed class JobRecurringCursorEntity
{
    public string SchedulerScopeKey { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public string TemplateJson { get; set; } = string.Empty;
    public string ScheduleJson { get; set; } = string.Empty;
    public long? NextOccurrenceUtcTicks { get; set; }
    public long CursorVersion { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public JobRecurringScheduleSuspensionReason SuspensionReasons { get; set; }
    public Guid ConcurrencyToken { get; set; }
}
