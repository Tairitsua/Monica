using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Execution;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

public sealed class JobSchedulerAnalyticsStoreTests
{
    private static readonly DateTimeOffset RANGE_START = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
    private const string WORKER = "analytics-worker";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenCompletionsBorderBuckets_ShouldKeepLateRecordsInTheirContainingInterval(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.alpha"));
        var width = TimeSpan.TicksPerHour;
        long[] offsets = [0, width / 2 + 1, width - 1, width, 2 * width - 1];
        foreach (var offset in offsets)
        {
            fixture.Time.Advance(RANGE_START.AddTicks(offset) - fixture.Time.GetUtcNow());
            await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", $"at-{offset}",
                JobAttemptOutcome.Succeeded, TimeSpan.Zero);
        }

        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START,
            EndTimeUtc = RANGE_START.AddHours(2)
        }, TestContext.Current.CancellationToken);

        snapshot.Trend.Select(bucket => bucket.SucceededCount).Should().Equal(3, 2);
        snapshot.Trend.Sum(bucket => bucket.ExecutedTerminalCount).Should().Be(offsets.Length);
        snapshot.CompletedTerminalCount.Should().Be(offsets.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenLongestRecordsTie_ShouldChooseOneExactJobWinnerBeforeLimiting(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"),
            StoreFixture.TriggeredDeclaration("jobs.gamma"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.alpha"));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "z-alpha", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(5));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "a-alpha", JobAttemptOutcome.Failed, TimeSpan.FromMinutes(5));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.beta", "b-beta", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(5));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_B, "jobs.alpha", "b-owner-alpha", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(5));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.gamma", "c-short", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(1));
        var query = new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START, EndTimeUtc = RANGE_START.AddHours(1), SlowestExecutionLimit = 2,
            OwnerKey = StoreFixture.OWNER_A
        };

        var owner = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, query, TestContext.Current.CancellationToken);
        owner.SlowestExecutions.Select(item => item.InstanceId).Should().Equal("a-alpha", "b-beta");
        var allOwners = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope,
            query with { OwnerKey = null, SlowestExecutionLimit = 3 }, TestContext.Current.CancellationToken);
        allOwners.SlowestExecutions.Select(item => item.InstanceId).Should().Equal("a-alpha", "b-beta", "b-owner-alpha");
        allOwners.SlowestExecutions.Select(item => new JobId(item.OwnerKey, item.JobKey)).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenCompletionsMeetWindowEdges_ShouldUseHalfOpenBucketsAndKeepEmptyIntervals(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START.AddMinutes(-1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.alpha"));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "at-start", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(1));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "at-third-bucket", JobAttemptOutcome.Succeeded, TimeSpan.FromHours(3));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "at-exclusive-end", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(30));
        var end = RANGE_START.AddHours(3).AddMinutes(30);
        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START, EndTimeUtc = end
        }, TestContext.Current.CancellationToken);

        snapshot.CompletedTerminalCount.Should().Be(2);
        snapshot.StateTotals[JobExecutionState.Succeeded].Should().Be(2);
        snapshot.ExecutedThroughputPerHour.Should().BeApproximately(2.0 / 3.5, 0.0001);
        snapshot.Trend.Select(bucket => bucket.SucceededCount).Should().Equal(1, 0, 0, 1);
        snapshot.Trend.Select(bucket => bucket.StartTimeUtc).Should().Equal(
            RANGE_START, RANGE_START.AddHours(1), RANGE_START.AddHours(2), RANGE_START.AddHours(3));
        snapshot.Trend[^1].EndTimeUtc.Should().Be(end);
        snapshot.Trend[^1].EndTimeUtc.Subtract(snapshot.Trend[^1].StartTimeUtc).Should().Be(TimeSpan.FromMinutes(30));
        snapshot.Duration.Count.Should().Be(2);
        snapshot.Duration.Minimum.Should().Be(TimeSpan.FromMinutes(1));
        snapshot.Duration.Maximum.Should().Be(TimeSpan.FromHours(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenRecurringDispositionsMixWithOtherOrigins_ShouldUseOnlyScheduledDispatchDenominator(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.recurring", cron: "0 0 * * * *"),
            StoreFixture.TriggeredDeclaration("jobs.triggered"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.recurring");
        var query = new JobExecutionAnalyticsQuery { StartTimeUtc = RANGE_START, EndTimeUtc = RANGE_START.AddHours(4) };
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var first = await MaterializeAsync(fixture, cursor.Key, "scheduled-failed");
        first.State.Should().Be(JobExecutionState.Queued);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var skipped = await MaterializeAsync(fixture, cursor.Key, "scheduled-skipped");
        skipped.State.Should().Be(JobExecutionState.Skipped);

        var onlySkipped = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, query, TestContext.Current.CancellationToken);
        onlySkipped.RecurringScheduleDispositionCount.Should().Be(1);
        onlySkipped.RecurringScheduleFulfillment.Should().Be(0);
        onlySkipped.Reliability.Should().Be(0);
        onlySkipped.Duration.Count.Should().Be(0);
        var failedLease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, WORKER, ["jobs.recurring"])).Single();
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = failedLease.LeaseKey, Outcome = JobAttemptOutcome.Failed
        }, TestContext.Current.CancellationToken);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        await MaterializeAsync(fixture, cursor.Key, "scheduled-cancelled");
        await fixture.Store.RequestCancellationAsync(fixture.Scope, "scheduled-cancelled", cancellationToken: TestContext.Current.CancellationToken);
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.triggered", "triggered-success", JobAttemptOutcome.Succeeded, TimeSpan.Zero);
        await fixture.RunRecurringNowAsync(StoreFixture.OWNER_A, "jobs.recurring", "run-now-success");
        var runNowLease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, WORKER, ["jobs.recurring"])).Single();
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = runNowLease.LeaseKey, Outcome = JobAttemptOutcome.Succeeded
        }, TestContext.Current.CancellationToken);

        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, query, TestContext.Current.CancellationToken);
        snapshot.CompletedTerminalCount.Should().Be(5);
        snapshot.RecurringScheduleDispositionCount.Should().Be(2);
        snapshot.RecurringScheduleFulfillment.Should().Be(0.5);
        snapshot.Reliability.Should().BeApproximately(2.0 / 3, 0.0001);
        snapshot.ExecutedTerminalCount.Should().Be(3);
        snapshot.Duration.Count.Should().Be(3);
        snapshot.Duration.Minimum.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenFiveDurationsAreRecorded_ShouldUseNearestRankPercentiles(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.alpha"));
        for (var minutes = 1; minutes <= 5; minutes++)
            await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", $"duration-{minutes}",
                JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(minutes));
        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START, EndTimeUtc = RANGE_START.AddHours(1)
        }, TestContext.Current.CancellationToken);
        snapshot.Duration.Count.Should().Be(5);
        snapshot.Duration.Minimum.Should().Be(TimeSpan.FromMinutes(1));
        snapshot.Duration.Average.Should().Be(TimeSpan.FromMinutes(3));
        snapshot.Duration.P50.Should().Be(TimeSpan.FromMinutes(3));
        snapshot.Duration.P90.Should().Be(TimeSpan.FromMinutes(5));
        snapshot.Duration.P95.Should().Be(TimeSpan.FromMinutes(5));
        snapshot.Duration.P99.Should().Be(TimeSpan.FromMinutes(5));
        snapshot.Duration.Maximum.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionAnalyticsAsync_WhenQueuedRetryIsCancelled_ShouldIncludeRecordedRetryWaitingInDuration(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.retry") with { RetryCount = 1 });
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.retry", instanceId: "cancelled-retry");
        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, WORKER, ["jobs.retry"], leaseDuration: TimeSpan.FromHours(1))).Single();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey, Outcome = JobAttemptOutcome.Failed
        }, TestContext.Current.CancellationToken);
        var waiting = await fixture.Store.GetExecutionAsync(fixture.Scope, "cancelled-retry", TestContext.Current.CancellationToken);
        Assert.NotNull(waiting);
        waiting.State.Should().Be(JobExecutionState.Queued);
        waiting.StartedAtUtc.Should().Be(RANGE_START);
        waiting.CompletedAtUtc.Should().BeNull();
        fixture.Time.Advance(TimeSpan.FromMinutes(9));
        await fixture.Store.RequestCancellationAsync(fixture.Scope, "cancelled-retry", cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START, EndTimeUtc = RANGE_START.AddHours(1)
        }, TestContext.Current.CancellationToken);
        snapshot.CompletedTerminalCount.Should().Be(1);
        snapshot.ExecutedTerminalCount.Should().Be(1);
        snapshot.Duration.Count.Should().Be(1);
        snapshot.Duration.Maximum.Should().Be(TimeSpan.FromMinutes(10));
        snapshot.SlowestExecutions.Should().ContainSingle().Which.Duration.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analytics_ShouldSeparateCreatedCohortFromCompletionWindowAndCalculateDurationDistribution(
        bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START.AddMinutes(-5));
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));

        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "before-range",
            JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(10));
        // Both never-completed executions enter the durable ledger inside the analytics cohort window.
        await fixture.EnqueueTriggeredAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            jobArgs: "{\"v\":\"still-queued\"}",
            instanceId: "still-queued",
            availableAtUtc: RANGE_START.AddHours(2));
        await fixture.EnqueueTriggeredAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            jobArgs: "{\"v\":\"cancelled-beta\"}",
            instanceId: "cancelled-beta",
            availableAtUtc: RANGE_START.AddMinutes(30));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "failed-alpha",
            JobAttemptOutcome.Failed, TimeSpan.FromMinutes(20));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.beta", "succeeded-beta",
            JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(25));
        var cancelled = (await fixture.ClaimAsync(StoreFixture.OWNER_A, WORKER, ["jobs.beta"]))
            .Single(lease => lease.Execution.InstanceId == "cancelled-beta");
        await fixture.Store.RequestCancellationAsync(fixture.Scope, "cancelled-beta", cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = cancelled.LeaseKey,
            Outcome = JobAttemptOutcome.Cancelled
        }, TestContext.Current.CancellationToken);

        var snapshot = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START,
            EndTimeUtc = RANGE_START.AddHours(1)
        }, TestContext.Current.CancellationToken);

        snapshot.StateTotals[JobExecutionState.Queued].Should().Be(1);
        snapshot.CompletedTerminalCount.Should().Be(4);
        snapshot.ExecutedTerminalCount.Should().Be(4);
        snapshot.Reliability.Should().BeApproximately(2.0 / 3, 0.0001);
        snapshot.Duration.Count.Should().Be(4);
        snapshot.Duration.Minimum.Should().Be(TimeSpan.Zero);
        snapshot.Duration.Maximum.Should().Be(TimeSpan.FromMinutes(25));
        snapshot.Trend.Sum(bucket => bucket.SucceededCount).Should().Be(2);
        snapshot.Trend.Sum(bucket => bucket.FailedCount).Should().Be(1);
        snapshot.Trend.Sum(bucket => bucket.CancelledCount).Should().Be(1);
        snapshot.TopJobsByVolume.Should().NotBeEmpty();
        snapshot.TopJobsByVolume.First().JobKey.Should().Be("jobs.alpha");
        snapshot.SlowestExecutions
            .Select(execution => execution.CompletedAtUtc - execution.StartedAtUtc)
            .Should().BeInDescendingOrder(duration => duration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analytics_ShouldHonorOwnerAndJobFilters(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, RANGE_START);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.alpha"));

        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.alpha", "a-1",
            JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(5));
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_A, "jobs.beta", "a-2",
            JobAttemptOutcome.Failed, TimeSpan.FromMinutes(5));
        // The same JobKey under another owner must stay separated by the owner filter.
        await RunAndCompleteAsync(fixture, StoreFixture.OWNER_B, "jobs.alpha", "b-1",
            JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(5));

        var aggregate = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START,
            EndTimeUtc = RANGE_START.AddHours(1)
        }, TestContext.Current.CancellationToken);
        aggregate.TopJobsByVolume
            .Where(item => item.JobKey == "jobs.alpha")
            .Select(item => item.OwnerKey)
            .Should().BeEquivalentTo([StoreFixture.OWNER_A, StoreFixture.OWNER_B]);
        aggregate.TopJobsByVolume
            .Where(item => item.JobKey == "jobs.alpha")
            .Should().OnlyContain(item => item.CompletedTerminalCount == 1);
        aggregate.TopJobsByFailures
            .Should().ContainSingle()
            .Which.OwnerKey.Should().Be(StoreFixture.OWNER_A);
        aggregate.SlowestExecutions
            .Select(item => new JobId(item.OwnerKey, item.JobKey))
            .Should().BeEquivalentTo(
                [
                    new JobId(StoreFixture.OWNER_A, "jobs.alpha"),
                    new JobId(StoreFixture.OWNER_A, "jobs.beta"),
                    new JobId(StoreFixture.OWNER_B, "jobs.alpha")
                ]);

        var ownerScoped = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START,
            EndTimeUtc = RANGE_START.AddHours(1),
            OwnerKey = StoreFixture.OWNER_A
        }, TestContext.Current.CancellationToken);
        ownerScoped.CompletedTerminalCount.Should().Be(2);

        var ownerJobScoped = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope,
            new JobExecutionAnalyticsQuery
            {
                StartTimeUtc = RANGE_START,
                EndTimeUtc = RANGE_START.AddHours(1),
                OwnerKey = StoreFixture.OWNER_B,
                JobKey = "jobs.alpha"
        }, TestContext.Current.CancellationToken);
        ownerJobScoped.CompletedTerminalCount.Should().Be(1);

        var jobAcrossOwners = await fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope,
            new JobExecutionAnalyticsQuery
            {
                StartTimeUtc = RANGE_START,
                EndTimeUtc = RANGE_START.AddHours(1),
                JobKey = "jobs.alpha"
            }, TestContext.Current.CancellationToken);
        jobAcrossOwners.TopJobsByVolume.Should().HaveCount(2);
        jobAcrossOwners.TopJobsByVolume.Select(item => item.OwnerKey)
            .Should().BeEquivalentTo([StoreFixture.OWNER_A, StoreFixture.OWNER_B]);
        jobAcrossOwners.SlowestExecutions.Select(item => item.OwnerKey)
            .Should().BeEquivalentTo([StoreFixture.OWNER_A, StoreFixture.OWNER_B]);
    }

    [Fact]
    public async Task Analytics_InvalidBoundsShouldBeRejected()
    {
        await using var fixture = await StoreFixture.CreateAsync(false, RANGE_START);
        Func<Task> act = () => fixture.Store.GetExecutionAnalyticsAsync(fixture.Scope, new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = RANGE_START,
            EndTimeUtc = RANGE_START
        }, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static async Task<JobExecutionInstance> MaterializeAsync(
        StoreFixture fixture, RecurringScheduleCursorKey key, string instanceId)
    {
        var due = (await fixture.Store.GetDueRecurringSchedulesAsync(
            fixture.Scope, key.OwnerKey, 10, TestContext.Current.CancellationToken)).Single(item => item.Key.JobKey == key.JobKey);
        var result = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(new RecurringOccurrenceMaterialization
        {
            CursorKey = due.Key, ExpectedVersion = due.Version,
            ExpectedOccurrenceUtc = due.NextOccurrenceUtc!.Value, InstanceId = instanceId
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Execution);
        return result.Execution;
    }

    private static async Task RunAndCompleteAsync(
        StoreFixture fixture,
        string ownerKey,
        string jobKey,
        string instanceId,
        JobAttemptOutcome outcome,
        TimeSpan duration)
    {
        await fixture.EnqueueTriggeredAsync(
            ownerKey,
            jobKey,
            jobArgs: "{\"v\":\"payload\"}",
            instanceId: instanceId);
        var lease = (await fixture.ClaimAsync(
                ownerKey,
                WORKER,
                [jobKey],
                leaseDuration: duration + TimeSpan.FromHours(1)))
            .Single(item => item.Execution.InstanceId == instanceId);
        fixture.Time.Advance(duration);
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = outcome
        }, TestContext.Current.CancellationToken);
    }
}
