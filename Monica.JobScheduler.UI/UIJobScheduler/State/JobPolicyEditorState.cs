using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>
/// Identifies the three nullable-description intentions available to an operator.
/// </summary>
internal enum JobDescriptionOverrideMode
{
    Inherit,
    Clear,
    Value
}

/// <summary>
/// Owns one policy workbench draft independently from the active definition used as its optimistic baseline.
/// </summary>
internal sealed class JobPolicyEditorState
{
    private static readonly int MAX_TIMEOUT_HOURS =
        checked((int)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerHour));

    private static readonly int MAX_TIMEOUT_MINUTES_AT_LIMIT =
        checked((int)((TimeSpan.MaxValue.Ticks % TimeSpan.TicksPerHour) / TimeSpan.TicksPerMinute));

    private static readonly int MAX_TIMEOUT_SECONDS_AT_LIMIT =
        checked((int)((TimeSpan.MaxValue.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond));

    private static readonly long MAX_TIMEOUT_SUBSECOND_TICKS_AT_LIMIT =
        TimeSpan.MaxValue.Ticks % TimeSpan.TicksPerSecond;

    private long _timeoutSubsecondTicks;

    private JobPolicyEditorState(
        JobDefinition definition,
        JobOperationalSummary? operationalSummary,
        DateTimeOffset observedAtUtc)
    {
        Definition = definition;
        DisabledOverride = definition.Policy.Overrides.DisabledOverride;
        LoadIdentityDrafts(definition.Policy.Overrides);
        MaxConcurrencyOverride = definition.Policy.Overrides.MaxConcurrencyOverride;
        RetryCountOverride = definition.Policy.Overrides.RetryCountOverride;
        HasTimeoutOverride = definition.Policy.Overrides.MaxExecutionTimeoutOverride is not null;
        SetTimeoutParts(definition.Policy.Overrides.MaxExecutionTimeoutOverride
                        ?? definition.Declaration.MaxExecutionTimeout);
        MaxRetainedHistoryRecords = definition.Policy.Overrides.MaxRetainedHistoryRecords;
        MaxRetentionDays = definition.Policy.Overrides.MaxRetentionDays;
        Schedule = CreateScheduleDraft(definition, operationalSummary, observedAtUtc);
        DormantScheduleOverride = definition.Declaration.JobType == JobType.Triggered
            ? definition.Policy.Overrides.ScheduleOverride
            : null;
    }

    internal JobDefinition Definition { get; private set; }

    internal bool? DisabledOverride { get; set; }

    internal bool IsDisplayNameInherited { get; private set; }

    internal string DisplayNameDraft { get; private set; } = string.Empty;

    internal JobDescriptionOverrideMode DescriptionMode { get; set; }

    internal string DescriptionDraft { get; set; } = string.Empty;

    internal int? MaxConcurrencyOverride { get; set; }

    internal int? RetryCountOverride { get; set; }

    internal bool HasTimeoutOverride { get; private set; }

    internal int TimeoutHours { get; set; }

    internal int TimeoutMinutes { get; set; }

    internal int TimeoutSeconds { get; set; }

    internal bool HasSubsecondTimeoutPrecision => _timeoutSubsecondTicks != 0;

    internal int MaximumTimeoutHours => MAX_TIMEOUT_HOURS;

    internal int? MaxRetainedHistoryRecords { get; set; }

    internal int? MaxRetentionDays { get; set; }

    internal CronPolicyDraft? Schedule { get; private set; }

    internal JobScheduleOverride? DormantScheduleOverride { get; private set; }

    internal JobScheduleOverride? UnresolvedScheduleDraft { get; private set; }

    internal bool HasScheduleTypeConflict { get; private set; }

    internal bool HasConflict { get; private set; }

    internal bool HasSameFieldConflict { get; private set; }

    internal string? ConflictPreviousPolicyRevision { get; private set; }

    internal string ExpectedConcurrencyStamp => Definition.Policy.ConcurrencyStamp;

    internal string EffectiveDisplayName => IsDisplayNameInherited
        ? Definition.Declaration.JobName
        : DisplayNameDraft;

    internal string? EffectiveDescription => DescriptionMode switch
    {
        JobDescriptionOverrideMode.Inherit => Definition.Declaration.Description,
        JobDescriptionOverrideMode.Clear => null,
        JobDescriptionOverrideMode.Value => DescriptionDraft,
        _ => throw new ArgumentOutOfRangeException()
    };

    internal bool EffectiveDisabled => DisabledOverride ?? Definition.Declaration.IsDisabledByDefault;

    internal int EffectiveMaxConcurrency => MaxConcurrencyOverride ?? Definition.Declaration.MaxConcurrency;

    internal int EffectiveRetryCount => RetryCountOverride ?? Definition.Declaration.RetryCount;

    internal TimeSpan EffectiveTimeout => HasTimeoutOverride
        ? TryBuildTimeout(out var timeout) ? timeout : TimeSpan.Zero
        : Definition.Declaration.MaxExecutionTimeout;

    internal bool IsTimeoutValid => !HasTimeoutOverride
                                    || TryBuildTimeout(out var timeout) && timeout > TimeSpan.Zero;

    internal int EffectiveMaxRetainedHistoryRecords =>
        MaxRetainedHistoryRecords ?? JobPolicy.DEFAULT_MAX_RETAINED_HISTORY_RECORDS;

    internal int? EffectiveMaxRetentionDays => MaxRetentionDays;

    internal bool IsValid =>
        (IsDisplayNameInherited
         || !string.IsNullOrWhiteSpace(DisplayNameDraft)
         && DisplayNameDraft.Length <= JobDeclaration.JOB_NAME_MAX_LENGTH)
        && (DescriptionMode != JobDescriptionOverrideMode.Value
            || !string.IsNullOrWhiteSpace(DescriptionDraft)
            && DescriptionDraft.Length <= JobDeclaration.DESCRIPTION_MAX_LENGTH)
        && IsOperationalPolicyValid
        && (Schedule?.OverrideExpression is null
            || Schedule.OverrideExpression.Length <= JobDeclaration.CRON_EXPRESSION_MAX_LENGTH)
        && (Schedule is null || Schedule.AreBoundariesValid && Schedule.Evaluation.IsValid);

    // Preserved schedule intent counts as unsaved work.
    internal bool HasChanges => HasScheduleTypeConflict
                                || CreateOverrides() != Definition.Policy.Overrides;

    internal bool CanSave => IsValid && HasChanges && !HasScheduleTypeConflict;

    /// <summary>Counts changes to the six execution and retention fields exposed by the policy-only editor.</summary>
    internal int OperationalPolicyChangeCount
    {
        get
        {
            var baseline = Definition.Policy.Overrides;
            return (DisabledOverride != baseline.DisabledOverride ? 1 : 0)
                   + (MaxConcurrencyOverride != baseline.MaxConcurrencyOverride ? 1 : 0)
                   + (RetryCountOverride != baseline.RetryCountOverride ? 1 : 0)
                   + ((HasTimeoutOverride ? EffectiveTimeout : (TimeSpan?)null) != baseline.MaxExecutionTimeoutOverride ? 1 : 0)
                   + (MaxRetainedHistoryRecords != baseline.MaxRetainedHistoryRecords ? 1 : 0)
                   + (MaxRetentionDays != baseline.MaxRetentionDays ? 1 : 0);
        }
    }

    /// <summary>Ignores hidden identity and schedule drafts when deciding whether operational edits can be saved.</summary>
    internal bool CanSaveOperationalPolicy => IsOperationalPolicyValid && OperationalPolicyChangeCount > 0;

    private bool IsOperationalPolicyValid =>
        MaxConcurrencyOverride is null or >= 1
        && RetryCountOverride is null or >= 0
        && IsTimeoutValid
        && MaxRetainedHistoryRecords is null or >= 0
        && MaxRetentionDays is null or >= 1;

    internal static JobPolicyEditorState Create(
        JobDefinition definition,
        JobOperationalSummary? operationalSummary,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(definition, operationalSummary, observedAtUtc);
    }

    internal void SetDisplayName(string? value)
    {
        IsDisplayNameInherited = false;
        DisplayNameDraft = value ?? string.Empty;
    }

    internal void ResetDisplayName()
    {
        IsDisplayNameInherited = true;
        DisplayNameDraft = string.Empty;
    }

    internal void EnableTimeoutOverride()
    {
        HasTimeoutOverride = true;
        SetTimeoutParts(Definition.EffectiveConfiguration.MaxExecutionTimeout);
    }

    internal void ResetTimeout()
    {
        HasTimeoutOverride = false;
        SetTimeoutParts(Definition.Declaration.MaxExecutionTimeout);
    }

    /// <summary>
    /// Applies a nullable total-minute draft, retaining fractional seconds at TimeSpan tick precision.
    /// Null inherits the declaration; invalid or out-of-range input leaves the accepted draft unchanged.
    /// </summary>
    internal bool TrySetTimeoutMinutes(decimal? totalMinutes)
    {
        if (totalMinutes is null)
        {
            ResetTimeout();
            return true;
        }

        if (totalMinutes <= 0 || totalMinutes > TimeSpan.MaxValue.Ticks / (decimal)TimeSpan.TicksPerMinute)
        {
            return false;
        }

        var ticks = decimal.Round(totalMinutes.Value * TimeSpan.TicksPerMinute, 0, MidpointRounding.ToEven);
        if (ticks < 1 || ticks > long.MaxValue)
        {
            return false;
        }

        HasTimeoutOverride = true;
        SetTimeoutParts(TimeSpan.FromTicks((long)ticks));
        return true;
    }

    internal void ResetAll()
    {
        DisabledOverride = null;
        ResetDisplayName();
        DescriptionMode = JobDescriptionOverrideMode.Inherit;
        DescriptionDraft = string.Empty;
        MaxConcurrencyOverride = null;
        RetryCountOverride = null;
        ResetTimeout();
        MaxRetainedHistoryRecords = null;
        MaxRetentionDays = null;
        DormantScheduleOverride = null;
        DiscardScheduleDraft();
        Schedule?.ResetExpression();
        Schedule?.StartBoundary.Reset(Schedule.DeclaredStartTimeUtc);
        Schedule?.EndBoundary.Reset(Schedule.DeclaredEndTimeUtc);
        Schedule?.BoundaryChanged();
    }

    internal JobPolicyChange CreateChange()
    {
        if (!IsValid || HasScheduleTypeConflict)
        {
            throw new InvalidOperationException(
                "An invalid or unresolved operational-policy draft cannot be persisted.");
        }

        return new()
        {
            Overrides = CreateOverrides(),
            ExpectedConcurrencyStamp = ExpectedConcurrencyStamp
        };
    }

    /// <summary>
    /// Replaces only execution and retention overrides, retaining the exact stored identity and schedule values.
    /// Hidden schedule projection, validation, or conflict state does not participate in this change.
    /// </summary>
    internal JobPolicyChange CreateOperationalPolicyChange()
    {
        if (!IsOperationalPolicyValid)
        {
            throw new InvalidOperationException("An invalid operational-policy draft cannot be persisted.");
        }

        return new()
        {
            Overrides = Definition.Policy.Overrides with
            {
                DisabledOverride = DisabledOverride,
                MaxConcurrencyOverride = MaxConcurrencyOverride,
                RetryCountOverride = RetryCountOverride,
                MaxExecutionTimeoutOverride = HasTimeoutOverride ? EffectiveTimeout : null,
                MaxRetainedHistoryRecords = MaxRetainedHistoryRecords,
                MaxRetentionDays = MaxRetentionDays
            },
            ExpectedConcurrencyStamp = ExpectedConcurrencyStamp
        };
    }

    /// <summary>
    /// Rebases edited fields onto the latest policy. Policy-only editors adopt hidden identity and schedule values
    /// directly so their projected drafts do not create conflicts outside the edited fields.
    /// </summary>
    internal void RebaseAfterConflict(
        JobDefinition latestDefinition,
        JobOperationalSummary? operationalSummary,
        DateTimeOffset observedAtUtc,
        bool operationalPolicyOnly = false)
    {
        ArgumentNullException.ThrowIfNull(latestDefinition);
        var previousDefinition = Definition;
        var previousOverrides = previousDefinition.Policy.Overrides;
        var latestOverrides = latestDefinition.Policy.Overrides;
        ConflictPreviousPolicyRevision = previousDefinition.Policy.ConcurrencyStamp;
        HasSameFieldConflict = false;

        DisabledOverride = MergeOverride(
            DisabledOverride,
            previousOverrides.DisabledOverride,
            latestOverrides.DisabledOverride);
        if (!operationalPolicyOnly)
        {
            MergeDisplayName(previousOverrides.DisplayNameOverride, latestOverrides.DisplayNameOverride);
            MergeDescription(previousOverrides.DescriptionOverride, latestOverrides.DescriptionOverride);
        }
        MaxConcurrencyOverride = MergeOverride(
            MaxConcurrencyOverride,
            previousOverrides.MaxConcurrencyOverride,
            latestOverrides.MaxConcurrencyOverride);
        RetryCountOverride = MergeOverride(
            RetryCountOverride,
            previousOverrides.RetryCountOverride,
            latestOverrides.RetryCountOverride);
        MergeTimeout(
            previousOverrides.MaxExecutionTimeoutOverride,
            latestOverrides.MaxExecutionTimeoutOverride,
            latestDefinition.Declaration.MaxExecutionTimeout);
        MaxRetainedHistoryRecords = MergeOverride(
            MaxRetainedHistoryRecords,
            previousOverrides.MaxRetainedHistoryRecords,
            latestOverrides.MaxRetainedHistoryRecords);
        MaxRetentionDays = MergeOverride(
            MaxRetentionDays,
            previousOverrides.MaxRetentionDays,
            latestOverrides.MaxRetentionDays);
        Definition = latestDefinition;
        HasConflict = true;

        if (operationalPolicyOnly)
        {
            LoadIdentityDrafts(latestOverrides);
            DiscardScheduleDraft();
            Schedule = CreateScheduleDraft(latestDefinition, operationalSummary, observedAtUtc);
            DormantScheduleOverride = latestDefinition.Declaration.JobType == JobType.Triggered
                ? latestOverrides.ScheduleOverride
                : null;
            return;
        }

        if (latestDefinition.Declaration.JobType == JobType.Recurring)
        {
            DiscardScheduleDraft();
            var latestSchedule = latestDefinition.EffectiveConfiguration.Schedule!;
            if (Schedule is null)
            {
                Schedule = CreateScheduleDraft(latestDefinition, operationalSummary, observedAtUtc);
            }
            else
            {
                Schedule = Schedule.Rebase(
                    latestDefinition.Declaration.CronExpression!,
                    latestSchedule.TimeZoneId,
                    latestDefinition.Declaration.StartTimeUtc,
                    latestDefinition.Declaration.EndTimeUtc,
                    previousOverrides.ScheduleOverride,
                    latestOverrides.ScheduleOverride,
                    observedAtUtc,
                    operationalSummary?.NextOccurrenceUtc,
                    out var hasScheduleConflict);
                HasSameFieldConflict |= hasScheduleConflict;
            }

            DormantScheduleOverride = null;
        }
        else
        {
            var draftDormantOverride = Schedule is null
                ? DormantScheduleOverride
                : CreateScheduleOverride(Schedule);
            HasScheduleTypeConflict = draftDormantOverride != previousOverrides.ScheduleOverride;
            UnresolvedScheduleDraft = HasScheduleTypeConflict ? draftDormantOverride : null;
            DormantScheduleOverride = latestOverrides.ScheduleOverride;
            Schedule = null;
        }
    }

    internal void ClearConflict() => HasConflict = false;

    internal void DiscardScheduleDraft()
    {
        HasScheduleTypeConflict = false;
        UnresolvedScheduleDraft = null;
    }

    private JobPolicyOverrides CreateOverrides() => new()
    {
        DisabledOverride = DisabledOverride,
        DisplayNameOverride = IsDisplayNameInherited ? null : DisplayNameDraft,
        DescriptionOverride = DescriptionMode switch
        {
            JobDescriptionOverrideMode.Inherit => null,
            JobDescriptionOverrideMode.Clear => new JobDescriptionOverride(),
            JobDescriptionOverrideMode.Value => new JobDescriptionOverride { Value = DescriptionDraft },
            _ => throw new ArgumentOutOfRangeException()
        },
        MaxConcurrencyOverride = MaxConcurrencyOverride,
        RetryCountOverride = RetryCountOverride,
        MaxExecutionTimeoutOverride = HasTimeoutOverride ? EffectiveTimeout : null,
        ScheduleOverride = Schedule is null ? DormantScheduleOverride : CreateScheduleOverride(Schedule),
        MaxRetainedHistoryRecords = MaxRetainedHistoryRecords,
        MaxRetentionDays = MaxRetentionDays
    };

    private static JobScheduleOverride? CreateScheduleOverride(CronPolicyDraft draft)
    {
        var schedule = new JobScheduleOverride
        {
            CronExpression = draft.IsExpressionInherited
                ? null
                : CronPolicyEditor.NormalizeExpression(draft.DraftExpression),
            StartTimeUtc = draft.StartBoundary.HasOverride
                ? new JobScheduleBoundaryOverride { Value = draft.StartBoundary.OverrideUtc }
                : null,
            EndTimeUtc = draft.EndBoundary.HasOverride
                ? new JobScheduleBoundaryOverride { Value = draft.EndBoundary.OverrideUtc }
                : null
        };
        return schedule.HasAnyOverride ? schedule : null;
    }

    private static CronPolicyDraft? CreateScheduleDraft(
        JobDefinition definition,
        JobOperationalSummary? operationalSummary,
        DateTimeOffset observedAtUtc)
    {
        if (definition.Declaration.JobType != JobType.Recurring)
        {
            return null;
        }

        var scheduleOverride = definition.Policy.Overrides.ScheduleOverride;
        var effectiveSchedule = definition.EffectiveConfiguration.Schedule!;
        return CronPolicyDraft.Create(
            definition.Declaration.CronExpression!,
            scheduleOverride?.CronExpression,
            effectiveSchedule.TimeZoneId,
            definition.Declaration.StartTimeUtc,
            definition.Declaration.EndTimeUtc,
            scheduleOverride?.StartTimeUtc is not null,
            scheduleOverride?.StartTimeUtc?.Value,
            scheduleOverride?.EndTimeUtc is not null,
            scheduleOverride?.EndTimeUtc?.Value,
            observedAtUtc,
            operationalSummary?.NextOccurrenceUtc);
    }

    private void SetTimeoutParts(TimeSpan value)
    {
        TimeoutHours = (int)value.TotalHours;
        TimeoutMinutes = value.Minutes;
        TimeoutSeconds = value.Seconds;
        _timeoutSubsecondTicks = value.Ticks % TimeSpan.TicksPerSecond;
    }

    private void LoadIdentityDrafts(JobPolicyOverrides overrides)
    {
        IsDisplayNameInherited = overrides.DisplayNameOverride is null;
        DisplayNameDraft = overrides.DisplayNameOverride ?? string.Empty;
        DescriptionMode = overrides.DescriptionOverride switch
        {
            null => JobDescriptionOverrideMode.Inherit,
            { Value: null } => JobDescriptionOverrideMode.Clear,
            _ => JobDescriptionOverrideMode.Value
        };
        DescriptionDraft = overrides.DescriptionOverride?.Value ?? string.Empty;
    }

    private void MergeDisplayName(string? previousOverride, string? latestOverride)
    {
        var draftOverride = IsDisplayNameInherited ? null : DisplayNameDraft;
        var merged = MergeOverride(draftOverride, previousOverride, latestOverride);
        IsDisplayNameInherited = merged is null;
        DisplayNameDraft = merged ?? string.Empty;
    }

    private void MergeDescription(
        JobDescriptionOverride? previousOverride,
        JobDescriptionOverride? latestOverride)
    {
        var draftOverride = DescriptionMode switch
        {
            JobDescriptionOverrideMode.Inherit => null,
            JobDescriptionOverrideMode.Clear => new JobDescriptionOverride(),
            JobDescriptionOverrideMode.Value => new JobDescriptionOverride { Value = DescriptionDraft },
            _ => throw new ArgumentOutOfRangeException()
        };
        var merged = MergeOverride(draftOverride, previousOverride, latestOverride);
        DescriptionMode = merged switch
        {
            null => JobDescriptionOverrideMode.Inherit,
            { Value: null } => JobDescriptionOverrideMode.Clear,
            _ => JobDescriptionOverrideMode.Value
        };
        DescriptionDraft = merged?.Value ?? string.Empty;
    }

    private void MergeTimeout(
        TimeSpan? previousOverride,
        TimeSpan? latestOverride,
        TimeSpan latestDeclaredTimeout)
    {
        var draftMatchesPrevious = previousOverride is null
            ? !HasTimeoutOverride
            : HasTimeoutOverride
              && TryBuildTimeout(out var draftTimeout)
              && draftTimeout == previousOverride;
        if (!draftMatchesPrevious)
        {
            HasSameFieldConflict |= previousOverride != latestOverride
                                    && (!TryBuildTimeout(out var currentTimeout)
                                        || currentTimeout != latestOverride);
            return;
        }

        HasTimeoutOverride = latestOverride is not null;
        SetTimeoutParts(latestOverride ?? latestDeclaredTimeout);
    }

    private T? MergeOverride<T>(T? draft, T? previous, T? latest)
    {
        var comparer = EqualityComparer<T?>.Default;
        if (comparer.Equals(draft, previous))
        {
            return latest;
        }

        HasSameFieldConflict |= !comparer.Equals(previous, latest)
                                && !comparer.Equals(draft, latest);
        return draft;
    }

    private bool TryBuildTimeout(out TimeSpan timeout)
    {
        if (TimeoutHours < 0
            || TimeoutHours > MAX_TIMEOUT_HOURS
            || TimeoutMinutes is < 0 or > 59
            || TimeoutSeconds is < 0 or > 59
            || _timeoutSubsecondTicks is < 0 or >= TimeSpan.TicksPerSecond
            || TimeoutHours == MAX_TIMEOUT_HOURS
            && (TimeoutMinutes > MAX_TIMEOUT_MINUTES_AT_LIMIT
                || TimeoutMinutes == MAX_TIMEOUT_MINUTES_AT_LIMIT
                && (TimeoutSeconds > MAX_TIMEOUT_SECONDS_AT_LIMIT
                    || TimeoutSeconds == MAX_TIMEOUT_SECONDS_AT_LIMIT
                    && _timeoutSubsecondTicks > MAX_TIMEOUT_SUBSECOND_TICKS_AT_LIMIT)))
        {
            timeout = default;
            return false;
        }

        timeout = TimeSpan.FromTicks(
            (long)TimeoutHours * TimeSpan.TicksPerHour
            + (long)TimeoutMinutes * TimeSpan.TicksPerMinute
            + (long)TimeoutSeconds * TimeSpan.TicksPerSecond
            + _timeoutSubsecondTicks);
        return true;
    }
}
