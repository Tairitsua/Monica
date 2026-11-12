using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class JobPolicyEditorStateTests
{
    private static readonly DateTimeOffset OBSERVED_AT =
        new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RebaseAfterConflict_ShouldPreserveCronIntentAndAcceptConcurrentRetentionChange()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.Schedule!.SetExpression("0 */15 * * * *");

        var update = await context.Store.UpdatePolicyAsync(
            JobSchedulerUiTestContext.SCOPE,
            initial.Definition.OwnerKey,
            initial.Definition.Declaration.JobKey,
            new JobPolicyChange
            {
                Overrides = initial.Definition.Policy.Overrides with { MaxRetentionDays = 30 },
                ExpectedConcurrencyStamp = initial.Definition.Policy.ConcurrencyStamp
            },
            Xunit.TestContext.Current.CancellationToken);
        update.Overrides.MaxRetentionDays.Should().Be(30);
        var latest = await GetRecurringSummaryAsync(context);

        state.RebaseAfterConflict(latest.Definition, latest, OBSERVED_AT);

        var rebased = state.CreateChange().Overrides;
        rebased.ScheduleOverride!.CronExpression.Should().Be("0 */15 * * * *");
        rebased.MaxRetentionDays.Should().Be(30);
        state.HasSameFieldConflict.Should().BeFalse();
        state.ExpectedConcurrencyStamp.Should().Be(latest.Definition.Policy.ConcurrencyStamp);
    }

    [Fact]
    public async Task CreateChange_WhenTimeoutExceedsSupportedRange_ShouldRejectDraftWithoutOverflow()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.EnableTimeoutOverride();
        state.TimeoutHours = int.MaxValue;

        state.IsTimeoutValid.Should().BeFalse();
        state.EffectiveTimeout.Should().Be(TimeSpan.Zero);
        Action act = () => state.CreateChange();
        act.Should().Throw<InvalidOperationException>();
        state.CanSaveOperationalPolicy.Should().BeFalse();
        Action operationalSave = () => state.CreateOperationalPolicyChange();
        operationalSave.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(30, 0, 0, 30)]
    [InlineData(5445, 1, 30, 45)]
    public async Task EnableTimeoutOverride_ShouldPreserveWholeSecondPrecision(
        int totalSeconds,
        int expectedHours,
        int expectedMinutes,
        int expectedSeconds)
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var declaredTimeout = TimeSpan.FromSeconds(totalSeconds);
        var definition = initial.Definition with
        {
            Declaration = initial.Definition.Declaration with { MaxExecutionTimeout = declaredTimeout }
        };
        var state = JobPolicyEditorState.Create(definition, initial, OBSERVED_AT);

        state.EnableTimeoutOverride();

        state.TimeoutHours.Should().Be(expectedHours);
        state.TimeoutMinutes.Should().Be(expectedMinutes);
        state.TimeoutSeconds.Should().Be(expectedSeconds);
        state.IsTimeoutValid.Should().BeTrue();
        state.CreateChange().Overrides.MaxExecutionTimeoutOverride.Should().Be(declaredTimeout);
    }

    [Fact]
    public async Task EnableTimeoutOverride_WhenTimeoutHasFractionalTicks_ShouldPreserveThemExplicitly()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var declaredTimeout = TimeSpan.FromSeconds(30).Add(TimeSpan.FromTicks(1234));
        var definition = initial.Definition with
        {
            Declaration = initial.Definition.Declaration with { MaxExecutionTimeout = declaredTimeout }
        };
        var state = JobPolicyEditorState.Create(definition, initial, OBSERVED_AT);

        state.EnableTimeoutOverride();

        state.HasSubsecondTimeoutPrecision.Should().BeTrue();
        state.CreateChange().Overrides.MaxExecutionTimeoutOverride.Should().Be(declaredTimeout);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(30 * TimeSpan.TicksPerSecond + 1234)]
    [InlineData(90 * TimeSpan.TicksPerMinute + 45 * TimeSpan.TicksPerSecond)]
    [InlineData(long.MaxValue)]
    public async Task TrySetTimeoutMinutes_WhenWithinSupportedRange_ShouldRoundTripExactTicks(long ticks)
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        var timeout = TimeSpan.FromTicks(ticks);

        var accepted = state.TrySetTimeoutMinutes(ticks / (decimal)TimeSpan.TicksPerMinute);

        accepted.Should().BeTrue();
        state.HasTimeoutOverride.Should().BeTrue();
        state.IsTimeoutValid.Should().BeTrue();
        state.EffectiveTimeout.Ticks.Should().Be(ticks);
        state.CreateChange().Overrides.MaxExecutionTimeoutOverride.Should().Be(timeout);
    }

    [Fact]
    public async Task TrySetTimeoutMinutes_WhenInvalid_ShouldLeaveExistingOverrideUnchanged()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var existingTimeout = TimeSpan.FromSeconds(30).Add(TimeSpan.FromTicks(1234));
        var definition = initial.Definition with
        {
            Policy = initial.Definition.Policy with
            {
                Overrides = initial.Definition.Policy.Overrides with
                {
                    MaxExecutionTimeoutOverride = existingTimeout
                }
            }
        };
        var state = JobPolicyEditorState.Create(definition, initial, OBSERVED_AT);
        decimal[] invalidMinutes = [0m, -1m, decimal.MaxValue, 0.25m / TimeSpan.TicksPerMinute];

        foreach (var invalidMinutesValue in invalidMinutes)
        {
            state.TrySetTimeoutMinutes(invalidMinutesValue).Should().BeFalse();
            state.HasTimeoutOverride.Should().BeTrue();
            state.EffectiveTimeout.Should().Be(existingTimeout);
            state.CreateChange().Overrides.MaxExecutionTimeoutOverride.Should().Be(existingTimeout);
            state.HasChanges.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TrySetTimeoutMinutes_WhenNull_ShouldRestoreDeclaredTimeoutInheritance()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.TrySetTimeoutMinutes(90.75m).Should().BeTrue();

        var accepted = state.TrySetTimeoutMinutes(null);

        accepted.Should().BeTrue();
        state.HasTimeoutOverride.Should().BeFalse();
        state.EffectiveTimeout.Should().Be(initial.Definition.Declaration.MaxExecutionTimeout);
        state.CreateChange().Overrides.MaxExecutionTimeoutOverride.Should().BeNull();
        state.HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task CreateOperationalPolicyChange_WhenEditingExecutionAndRetention_ShouldPreserveLoadedIdentityAndScheduleOverrides()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var existingOverrides = initial.Definition.Policy.Overrides with
        {
            DisplayNameOverride = "Operations cleanup",
            DescriptionOverride = new JobDescriptionOverride { Value = "Keep operations-owned metadata" },
            ScheduleOverride = new JobScheduleOverride
            {
                CronExpression = "0 */15 * * * *",
                StartTimeUtc = new JobScheduleBoundaryOverride { Value = OBSERVED_AT.AddHours(1) },
                EndTimeUtc = new JobScheduleBoundaryOverride { Value = null }
            }
        };
        await context.Store.UpdatePolicyAsync(
            JobSchedulerUiTestContext.SCOPE,
            initial.Definition.OwnerKey,
            initial.Definition.Declaration.JobKey,
            new JobPolicyChange
            {
                Overrides = existingOverrides,
                ExpectedConcurrencyStamp = initial.Definition.Policy.ConcurrencyStamp
            },
            Xunit.TestContext.Current.CancellationToken);
        var loaded = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(loaded.Definition, loaded, OBSERVED_AT);
        state.DisabledOverride = true;
        state.MaxConcurrencyOverride = 3;
        state.RetryCountOverride = 2;
        state.TrySetTimeoutMinutes(90.75m).Should().BeTrue();
        state.MaxRetainedHistoryRecords = 250;
        state.MaxRetentionDays = 14;
        state.OperationalPolicyChangeCount.Should().Be(6);
        state.CanSaveOperationalPolicy.Should().BeTrue();
        var change = state.CreateOperationalPolicyChange();
        change.Overrides.DescriptionOverride.Should().BeSameAs(loaded.Definition.Policy.Overrides.DescriptionOverride);
        change.Overrides.ScheduleOverride.Should().BeSameAs(loaded.Definition.Policy.Overrides.ScheduleOverride);

        await context.Store.UpdatePolicyAsync(
            JobSchedulerUiTestContext.SCOPE,
            loaded.Definition.OwnerKey,
            loaded.Definition.Declaration.JobKey,
            change,
            Xunit.TestContext.Current.CancellationToken);

        var saved = await GetRecurringSummaryAsync(context);
        saved.Definition.Policy.Overrides.Should().Be(existingOverrides with
        {
            DisabledOverride = true,
            MaxConcurrencyOverride = 3,
            RetryCountOverride = 2,
            MaxExecutionTimeoutOverride = TimeSpan.FromMinutes(90).Add(TimeSpan.FromSeconds(45)),
            MaxRetainedHistoryRecords = 250,
            MaxRetentionDays = 14
        });
    }

    [Fact]
    public async Task CreateOperationalPolicyChange_WhenBoundaryIsInLaterDstOverlap_ShouldPreserveStoredInstant()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var context = new JobSchedulerUiTestContext(schedulerTimeZone: timeZone);
        var initial = await GetRecurringSummaryAsync(context);
        var laterOverlap = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var storedSchedule = new JobScheduleOverride
        {
            StartTimeUtc = new JobScheduleBoundaryOverride { Value = laterOverlap }
        };
        var definition = initial.Definition with
        {
            Policy = initial.Definition.Policy with
            {
                Overrides = initial.Definition.Policy.Overrides with { ScheduleOverride = storedSchedule }
            }
        };
        var state = JobPolicyEditorState.Create(definition, initial, OBSERVED_AT);

        state.Schedule!.StartBoundary.IsAmbiguousLocalTime.Should().BeTrue();
        state.OperationalPolicyChangeCount.Should().Be(0);
        state.CanSaveOperationalPolicy.Should().BeFalse();
        state.CreateOperationalPolicyChange().Overrides.Should().Be(definition.Policy.Overrides);

        state.RetryCountOverride = 2;
        var change = state.CreateOperationalPolicyChange();

        state.OperationalPolicyChangeCount.Should().Be(1);
        state.CanSaveOperationalPolicy.Should().BeTrue();
        change.Overrides.RetryCountOverride.Should().Be(2);
        change.Overrides.ScheduleOverride.Should().BeSameAs(storedSchedule);
        change.Overrides.ScheduleOverride!.StartTimeUtc!.Value.Should().Be(laterOverlap);
        change.ExpectedConcurrencyStamp.Should().Be(definition.Policy.ConcurrencyStamp);
    }

    [Fact]
    public async Task CreateOperationalPolicyChange_WhenHiddenDraftsAreInvalid_ShouldIgnoreThem()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.SetDisplayName(string.Empty);
        state.DescriptionMode = JobDescriptionOverrideMode.Value;
        state.DescriptionDraft = string.Empty;
        state.Schedule!.SetExpression("invalid cron");

        state.OperationalPolicyChangeCount.Should().Be(0);
        state.CanSaveOperationalPolicy.Should().BeFalse();
        state.RetryCountOverride = 2;

        state.CanSave.Should().BeFalse();
        state.OperationalPolicyChangeCount.Should().Be(1);
        state.CanSaveOperationalPolicy.Should().BeTrue();
        state.CreateOperationalPolicyChange().Overrides.Should().Be(
            initial.Definition.Policy.Overrides with { RetryCountOverride = 2 });
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, null, -1, null)]
    [InlineData(null, null, null, 0)]
    public async Task CreateOperationalPolicyChange_WhenNumericDraftIsInvalid_ShouldRejectSave(
        int? maxConcurrency,
        int? retryCount,
        int? retainedRecords,
        int? retentionDays)
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.MaxConcurrencyOverride = maxConcurrency;
        state.RetryCountOverride = retryCount;
        state.MaxRetainedHistoryRecords = retainedRecords;
        state.MaxRetentionDays = retentionDays;

        state.OperationalPolicyChangeCount.Should().Be(1);
        state.CanSaveOperationalPolicy.Should().BeFalse();
        Action save = () => state.CreateOperationalPolicyChange();
        save.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(3, false, true)]
    [InlineData(null, true, false)]
    [InlineData(3, true, true)]
    public async Task RebaseAfterConflict_WhenOperationalPolicyOnly_ShouldAdoptHiddenFieldsAndDetectOnlyOperationalConflicts(
        int? latestRetryCount,
        bool becomesTriggered,
        bool expectedSameFieldConflict)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var context = new JobSchedulerUiTestContext(schedulerTimeZone: timeZone);
        var initial = await GetRecurringSummaryAsync(context);
        var laterOverlap = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var definition = initial.Definition with
        {
            Policy = initial.Definition.Policy with
            {
                Overrides = initial.Definition.Policy.Overrides with
                {
                    DisplayNameOverride = "Original operational name",
                    DescriptionOverride = new JobDescriptionOverride { Value = "Original description" },
                    ScheduleOverride = new JobScheduleOverride
                    {
                        StartTimeUtc = new JobScheduleBoundaryOverride { Value = laterOverlap }
                    }
                }
            }
        };
        var state = JobPolicyEditorState.Create(definition, initial, OBSERVED_AT);
        state.RetryCountOverride = 2;
        var latestOverrides = definition.Policy.Overrides with
        {
            DisplayNameOverride = "Concurrent operational name",
            DescriptionOverride = new JobDescriptionOverride { Value = null },
            RetryCountOverride = latestRetryCount,
            ScheduleOverride = new JobScheduleOverride
            {
                CronExpression = "0 */15 * * * *",
                StartTimeUtc = new JobScheduleBoundaryOverride { Value = laterOverlap.AddMinutes(15) }
            }
        };
        var latestDefinition = definition with
        {
            Declaration = becomesTriggered
                ? definition.Declaration with
                {
                    JobType = JobType.Triggered,
                    JobArgsKey = "Sample.Jobs.RecurringCleanupArgs",
                    CronExpression = null,
                    TimeZoneId = null,
                    StartTimeUtc = null,
                    EndTimeUtc = null
                }
                : definition.Declaration,
            Policy = definition.Policy with
            {
                Overrides = latestOverrides,
                ConcurrencyStamp = "latest-policy-revision"
            }
        };

        state.RebaseAfterConflict(latestDefinition, null, OBSERVED_AT, operationalPolicyOnly: true);

        state.HasConflict.Should().BeTrue();
        state.HasSameFieldConflict.Should().Be(expectedSameFieldConflict);
        state.HasScheduleTypeConflict.Should().BeFalse();
        state.UnresolvedScheduleDraft.Should().BeNull();
        state.EffectiveDisplayName.Should().Be("Concurrent operational name");
        state.DescriptionMode.Should().Be(JobDescriptionOverrideMode.Clear);
        state.OperationalPolicyChangeCount.Should().Be(1);
        state.CanSaveOperationalPolicy.Should().BeTrue();
        var change = state.CreateOperationalPolicyChange();
        change.Overrides.Should().Be(latestOverrides with { RetryCountOverride = 2 });
        change.Overrides.ScheduleOverride.Should().BeSameAs(latestOverrides.ScheduleOverride);
        change.Overrides.DescriptionOverride.Should().BeSameAs(latestOverrides.DescriptionOverride);
        change.ExpectedConcurrencyStamp.Should().Be("latest-policy-revision");
        if (becomesTriggered)
        {
            state.Schedule.Should().BeNull();
            state.DormantScheduleOverride.Should().BeSameAs(latestOverrides.ScheduleOverride);
        }
        else
        {
            state.Schedule!.EffectiveExpression.Should().Be("0 */15 * * * *");
        }
    }

    [Fact]
    public async Task RebaseAfterConflict_WhenRecurringJobBecomesTriggered_ShouldRequireExplicitScheduleDiscard()
    {
        await using var context = new JobSchedulerUiTestContext();
        var initial = await GetRecurringSummaryAsync(context);
        var state = JobPolicyEditorState.Create(initial.Definition, initial, OBSERVED_AT);
        state.Schedule!.SetExpression("0 */15 * * * *");
        state.MaxRetentionDays = 45;
        var latestDefinition = initial.Definition with
        {
            Declaration = initial.Definition.Declaration with
            {
                JobType = JobType.Triggered,
                JobArgsKey = "Sample.Jobs.RecurringCleanupArgs",
                CronExpression = null,
                TimeZoneId = null,
                StartTimeUtc = null,
                EndTimeUtc = null
            },
            Policy = initial.Definition.Policy with { ConcurrencyStamp = "latest-policy-revision" }
        };

        state.RebaseAfterConflict(latestDefinition, null, OBSERVED_AT);

        state.Schedule.Should().BeNull();
        state.HasScheduleTypeConflict.Should().BeTrue();
        state.UnresolvedScheduleDraft!.CronExpression.Should().Be("0 */15 * * * *");
        state.CanSave.Should().BeFalse();
        Action blockedSave = () => state.CreateChange();
        blockedSave.Should().Throw<InvalidOperationException>();
        state.OperationalPolicyChangeCount.Should().Be(1);
        state.CanSaveOperationalPolicy.Should().BeTrue();
        var operationalChange = state.CreateOperationalPolicyChange();
        operationalChange.Overrides.Should().Be(
            latestDefinition.Policy.Overrides with { MaxRetentionDays = 45 });
        operationalChange.ExpectedConcurrencyStamp.Should().Be("latest-policy-revision");
        state.MaxRetentionDays = latestDefinition.Policy.Overrides.MaxRetentionDays;
        state.OperationalPolicyChangeCount.Should().Be(0);
        state.CanSaveOperationalPolicy.Should().BeFalse();
        state.MaxRetentionDays = 45;

        state.DiscardScheduleDraft();

        state.HasScheduleTypeConflict.Should().BeFalse();
        state.MaxRetentionDays.Should().Be(45);
        state.CanSave.Should().BeTrue();
        var recovered = state.CreateChange();
        recovered.Overrides.ScheduleOverride.Should().Be(latestDefinition.Policy.Overrides.ScheduleOverride);
        recovered.Overrides.MaxRetentionDays.Should().Be(45);
        recovered.ExpectedConcurrencyStamp.Should().Be("latest-policy-revision");
    }

    private static async Task<JobOperationalSummary> GetRecurringSummaryAsync(
        JobSchedulerUiTestContext context) =>
        (await context.Store.GetOperationalSummaryAsync(
            JobSchedulerUiTestContext.SCOPE,
            new JobId(
                JobSchedulerUiTestContext.OWNER,
                context.RecurringDefinition.Declaration.JobKey),
            Xunit.TestContext.Current.CancellationToken))!;
}
