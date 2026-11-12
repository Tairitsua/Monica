using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>Exercises bounded maintenance and continuation through both existing store fixtures.</summary>
public sealed class JobSchedulerStoreCleanupTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHistoryAsync_WhenJobLimitIsReached_ShouldResumeAcrossOwnerAndJobIdentities(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, _now);
        foreach (var owner in new[] { StoreFixture.OWNER_A, StoreFixture.OWNER_B })
        {
            await fixture.SyncAsync(owner,
                StoreFixture.TriggeredDeclaration("jobs.a"), StoreFixture.TriggeredDeclaration("jobs.b"));
            foreach (var job in new[] { "jobs.a", "jobs.b" })
            {
                await CompleteExecutionsAsync(fixture, owner, job, 2);
                await SetRetentionAsync(fixture, owner, job, 1);
            }
        }

        var request = CleanupRequest(fixture) with { MaxJobs = 2 };
        var first = await fixture.Store.CleanupHistoryAsync(request, TestContext.Current.CancellationToken);
        first.DeletedCount.Should().Be(2);
        first.ScannedJobs.Should().Be(2);
        first.LastScannedJob.Should().Be(new JobId(StoreFixture.OWNER_A, "jobs.b"));
        first.CompletedScan.Should().BeFalse();
        (await QueryJobAsync(fixture, StoreFixture.OWNER_B, "jobs.a")).Should().HaveCount(2);
        (await QueryJobAsync(fixture, StoreFixture.OWNER_B, "jobs.b")).Should().HaveCount(2);

        var second = await fixture.Store.CleanupHistoryAsync(
            request with { AfterJob = first.LastScannedJob }, TestContext.Current.CancellationToken);
        second.DeletedCount.Should().Be(2);
        second.ScannedJobs.Should().Be(2);
        second.LastScannedJob.Should().Be(new JobId(StoreFixture.OWNER_B, "jobs.b"));
        second.CompletedScan.Should().BeTrue();
        foreach (var owner in new[] { StoreFixture.OWNER_A, StoreFixture.OWNER_B })
        {
            foreach (var job in new[] { "jobs.a", "jobs.b" })
            {
                (await QueryJobAsync(fixture, owner, job)).Should().ContainSingle()
                    .Which.InstanceId.Should().Be($"{owner}-{job}-1");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHistoryAsync_WhenJobBacklogExceedsDeletionBudget_ShouldRevisitItBeforeAdvancing(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, _now);
        await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.a"), StoreFixture.TriggeredDeclaration("jobs.b"));
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_A, "jobs.a", 6);
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_A, "jobs.b", 2);
        await SetRetentionAsync(fixture, StoreFixture.OWNER_A, "jobs.a", 1);
        await SetRetentionAsync(fixture, StoreFixture.OWNER_A, "jobs.b", 1);
        var before = new JobId(StoreFixture.OWNER_A, "jobs.0");
        var request = CleanupRequest(fixture) with { AfterJob = before, MaxJobs = 2, MaxDeletions = 2 };

        for (var pass = 0; pass < 2; pass++)
        {
            var partial = await fixture.Store.CleanupHistoryAsync(request, TestContext.Current.CancellationToken);
            partial.DeletedCount.Should().Be(2);
            partial.ScannedJobs.Should().Be(0);
            partial.LastScannedJob.Should().Be(before);
            partial.CompletedScan.Should().BeFalse();
            request = request with { AfterJob = partial.LastScannedJob };
            (await QueryJobAsync(fixture, StoreFixture.OWNER_A, "jobs.b")).Should().HaveCount(2);
        }

        var final = await fixture.Store.CleanupHistoryAsync(request, TestContext.Current.CancellationToken);
        final.DeletedCount.Should().Be(2);
        final.ScannedJobs.Should().Be(2);
        final.LastScannedJob.Should().Be(new JobId(StoreFixture.OWNER_A, "jobs.b"));
        final.CompletedScan.Should().BeTrue();
        (await QueryJobAsync(fixture, StoreFixture.OWNER_A, "jobs.a")).Should().ContainSingle()
            .Which.InstanceId.Should().Be($"{StoreFixture.OWNER_A}-jobs.a-5");
        (await QueryJobAsync(fixture, StoreFixture.OWNER_A, "jobs.b")).Should().ContainSingle()
            .Which.InstanceId.Should().Be($"{StoreFixture.OWNER_A}-jobs.b-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHistoryAsync_WhenOwnerIsAbsent_ShouldUseItsPersistedPolicyAndKeepQueuedWork(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, _now);
        var publication = await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.absent"));
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_B, "jobs.absent", 3);
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_B, "jobs.absent", instanceId: "absent-queued");
        await SetRetentionAsync(fixture, StoreFixture.OWNER_B, "jobs.absent", 1);
        await fixture.Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(fixture.Scope, StoreFixture.OWNER_B, [])
        {
            ExpectedPreviousManifestHash = publication.ManifestHash
        }, TestContext.Current.CancellationToken);

        var result = await fixture.Store.CleanupHistoryAsync(
            CleanupRequest(fixture) with { MaxRetainedOrphanedExecutions = 0 }, TestContext.Current.CancellationToken);
        result.DeletedCount.Should().Be(2);
        result.ScannedJobs.Should().Be(1);
        result.CompletedScan.Should().BeTrue();
        var remaining = await QueryJobAsync(fixture, StoreFixture.OWNER_B, "jobs.absent");
        remaining.Should().HaveCount(2);
        remaining.Should().ContainSingle(item => item.InstanceId == "absent-queued" && item.State == JobExecutionState.Queued);
        remaining.Should().ContainSingle(item => item.InstanceId == $"{StoreFixture.OWNER_B}-jobs.absent-2"
            && item.State == JobExecutionState.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHistoryAsync_WhenCountRetentionIsDisabled_ShouldApplyAgeAndProtectActiveExecutions(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, _now);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.age"));
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_A, "jobs.age", 2);
        fixture.Time.Advance(TimeSpan.FromDays(2));
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_A, "jobs.age", 1, "recent");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.age", instanceId: "age-running");
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-age", ["jobs.age"])).Should().ContainSingle();
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.age", instanceId: "age-queued");
        await SetRetentionAsync(fixture, StoreFixture.OWNER_A, "jobs.age", 0, 1);

        var result = await fixture.Store.CleanupHistoryAsync(CleanupRequest(fixture), TestContext.Current.CancellationToken);
        result.DeletedCount.Should().Be(2);
        result.CompletedScan.Should().BeTrue();
        var remaining = await QueryJobAsync(fixture, StoreFixture.OWNER_A, "jobs.age");
        remaining.Should().HaveCount(3);
        remaining.Should().ContainSingle(item => item.InstanceId == "recent-0" && item.State == JobExecutionState.Succeeded);
        remaining.Should().ContainSingle(item => item.InstanceId == "age-running" && item.State == JobExecutionState.Running);
        remaining.Should().ContainSingle(item => item.InstanceId == "age-queued" && item.State == JobExecutionState.Queued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHistoryAsync_WhenMaintenanceGrantIsReplaced_ShouldFenceTheOldCoordinator(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, _now);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.fenced"));
        await CompleteExecutionsAsync(fixture, StoreFixture.OWNER_A, "jobs.fenced", 3);
        await SetRetentionAsync(fixture, StoreFixture.OWNER_A, "jobs.fenced", 1);
        var original = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:maintenance"), "original-maintainer", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The original maintenance grant was not acquired.");
        fixture.Time.Advance(TimeSpan.FromSeconds(31));
        var replacement = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:maintenance"), "replacement-maintainer", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The replacement maintenance grant was not acquired.");

        Func<Task> staleCleanup = () => fixture.Store.CleanupHistoryAsync(
            CleanupRequest(fixture) with { CoordinatorLease = JobCoordinatorLeaseAdapter.ToKey(original.Identity) }, TestContext.Current.CancellationToken);
        await staleCleanup.Should().ThrowAsync<InvalidOperationException>();
        (await QueryJobAsync(fixture, StoreFixture.OWNER_A, "jobs.fenced")).Should().HaveCount(3);

        var result = await fixture.Store.CleanupHistoryAsync(
            CleanupRequest(fixture) with { CoordinatorLease = JobCoordinatorLeaseAdapter.ToKey(replacement.Identity) }, TestContext.Current.CancellationToken);
        result.DeletedCount.Should().Be(2);
        result.CompletedScan.Should().BeTrue();
    }

    private static JobHistoryCleanupRequest CleanupRequest(StoreFixture fixture) => new()
    {
        SchedulerScopeKey = fixture.Scope,
        MaxJobs = 32,
        MaxDeletions = 5000,
        TimeBudget = TimeSpan.FromMinutes(1)
    };

    private static async Task CompleteExecutionsAsync(
        StoreFixture fixture, string owner, string job, int count, string? prefix = null)
    {
        var instancePrefix = prefix ?? $"{owner}-{job}";
        for (var index = 0; index < count; index++)
        {
            await fixture.EnqueueTriggeredAsync(owner, job, instanceId: $"{instancePrefix}-{index}");
            var lease = (await fixture.ClaimAsync(owner, "cleanup-worker", [job])).Single();
            await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
            {
                LeaseKey = lease.LeaseKey,
                Outcome = JobAttemptOutcome.Succeeded
            }, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private static async Task SetRetentionAsync(
        StoreFixture fixture, string owner, string job, int maxRecords, int? maxDays = null)
    {
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, owner, job, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The cleanup scenario definition was not published.");
        await fixture.UpdatePolicyAsync(owner, job, definition.Policy, new JobPolicyOverrides
        {
            MaxRetainedHistoryRecords = maxRecords,
            MaxRetentionDays = maxDays
        });
    }

    private static async Task<IReadOnlyList<JobExecutionInstance>> QueryJobAsync(
        StoreFixture fixture, string owner, string job)
    {
        var page = await fixture.Store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            OwnerKey = owner,
            JobKey = job
        }, TestContext.Current.CancellationToken);
        return page.Items;
    }
}
