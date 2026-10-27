using AwesomeAssertions;
using Monica.JobScheduler.Models.Execution;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

public sealed class JobExecutionDistributionStoreTests
{
    private static readonly DateTimeOffset START = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryExecutionsAsync_WhenCompletionBoundsCrossCreationWindow_ShouldFilterBeforeCountingAndPaging(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, START.AddMinutes(-1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.match"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.match"));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "start", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(1));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "inside", JobAttemptOutcome.Failed, TimeSpan.FromMilliseconds(123));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "end", JobAttemptOutcome.Succeeded, TimeSpan.FromMilliseconds(877));
        await CompleteAsync(fixture, StoreFixture.OWNER_B, "other-owner", JobAttemptOutcome.Succeeded, TimeSpan.Zero);
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.match", instanceId: "queued");
        var query = new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.match",
            CompletedAfterUtc = START.ToOffset(TimeSpan.FromHours(8)),
            CompletedBeforeUtc = START.AddSeconds(1).AddTicks(-1),
            PageSize = 1,
            SortField = JobExecutionSortField.CompletedAtUtc,
            SortDescending = false
        };
        var first = await fixture.Store.QueryExecutionsAsync(query, TestContext.Current.CancellationToken);
        first.TotalCount.Should().Be(2);
        first.Items.Select(item => item.InstanceId).Should().Equal("start");
        var second = await fixture.Store.QueryExecutionsAsync(query with { PageNumber = 2 }, TestContext.Current.CancellationToken);
        second.TotalCount.Should().Be(2);
        second.Items.Select(item => item.InstanceId).Should().Equal("inside");
        var inclusiveEnd = await fixture.Store.QueryExecutionsAsync(query with { CompletedBeforeUtc = START.AddSeconds(1), PageSize = 10 }, TestContext.Current.CancellationToken);
        inclusiveEnd.TotalCount.Should().Be(3);
        var otherScope = await fixture.Store.QueryExecutionsAsync(query with { SchedulerScopeKey = "different-scope" }, TestContext.Current.CancellationToken);
        otherScope.TotalCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionDistributionAsync_WhenFacetsAndPagesAreSelected_ShouldAggregateEveryMatchingStateInHalfOpenBuckets(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, START.AddMinutes(-1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.match"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.match"));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "match-start", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(1));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "match-middle", JobAttemptOutcome.Failed, TimeSpan.FromMinutes(2));
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "excluded-search", JobAttemptOutcome.Failed, TimeSpan.Zero);
        await CompleteAsync(fixture, StoreFixture.OWNER_B, "match-owner-b", JobAttemptOutcome.Succeeded, TimeSpan.Zero);
        await CompleteAsync(fixture, StoreFixture.OWNER_A, "match-last", JobAttemptOutcome.Succeeded, TimeSpan.FromMinutes(2));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.match", instanceId: "match-queued");
        var query = new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.match",
            SearchText = "MATCH-",
            CompletedAfterUtc = START,
            CompletedBeforeUtc = START.AddMinutes(4).AddTicks(-1),
            States = [JobExecutionState.Failed],
            PageSize = 1,
            PageNumber = 2
        };
        var snapshot = await fixture.Store.GetExecutionDistributionAsync(query, 4, TestContext.Current.CancellationToken);
        snapshot.UsesCompletionTime.Should().BeTrue();
        snapshot.StartTimeUtc.Should().Be(START);
        snapshot.EndTimeUtc.Should().Be(START.AddMinutes(4));
        snapshot.Buckets.Should().HaveCount(4);
        snapshot.StateTotals[JobExecutionState.Succeeded].Should().Be(1);
        snapshot.StateTotals[JobExecutionState.Failed].Should().Be(1);
        snapshot.StateTotals[JobExecutionState.Queued].Should().Be(0);
        snapshot.Buckets.Select(bucket => bucket.Counts[JobExecutionState.Succeeded]).Should().Equal(1, 0, 0, 0);
        snapshot.Buckets.Select(bucket => bucket.Counts[JobExecutionState.Failed]).Should().Equal(0, 0, 1, 0);
        var recorded = await fixture.Store.GetExecutionDistributionAsync(query with { CompletedAfterUtc = null, CompletedBeforeUtc = null, CreatedBeforeUtc = START.AddMinutes(5) }, 4, TestContext.Current.CancellationToken);
        recorded.UsesCompletionTime.Should().BeFalse();
        recorded.StateTotals.Values.Sum().Should().Be(4);
        recorded.StateTotals[JobExecutionState.Queued].Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionDistributionAsync_WhenNoRowsMatch_ShouldKeepEmptyIntervalsAndRejectUnboundedBucketCounts(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, START);
        var query = new JobExecutionQuery { SchedulerScopeKey = fixture.Scope, CreatedAfterUtc = START, CreatedBeforeUtc = START.AddHours(1).AddTicks(-1) };
        var snapshot = await fixture.Store.GetExecutionDistributionAsync(query, 6, TestContext.Current.CancellationToken);
        snapshot.Buckets.Should().HaveCount(6);
        snapshot.Buckets.Should().OnlyContain(bucket => bucket.Counts.Values.Sum() == 0);
        snapshot.StateTotals.Should().HaveCount(Enum.GetValues<JobExecutionState>().Length);
        Func<Task> invalid = () => fixture.Store.GetExecutionDistributionAsync(query, 201, TestContext.Current.CancellationToken);
        await invalid.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static async Task CompleteAsync(StoreFixture fixture, string owner, string id, JobAttemptOutcome outcome, TimeSpan duration)
    {
        await fixture.EnqueueTriggeredAsync(owner, "jobs.match", instanceId: id);
        var lease = (await fixture.ClaimAsync(owner, "distribution-worker", ["jobs.match"], leaseDuration: duration + TimeSpan.FromHours(1))).Single(item => item.Execution.InstanceId == id);
        fixture.Time.Advance(duration);
        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion { LeaseKey = lease.LeaseKey, Outcome = outcome }, TestContext.Current.CancellationToken);
    }
}
