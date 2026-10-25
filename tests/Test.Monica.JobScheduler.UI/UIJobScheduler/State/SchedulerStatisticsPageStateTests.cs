using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Services.Support;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.Modules;
using Monica.Testing.Localization;
using NSubstitute;
using Test.Monica.JobScheduler.UI.Support;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class SchedulerStatisticsPageStateTests
{
    private static readonly TimeSpan OBSERVATION_TIMEOUT = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SetTimeRangeAsync_WhenAuthorizationIsDelayedAndSelectionReverts_ShouldFreezeBoundsAndAcceptOnlyNewestPreset()
    {
        var boundary = new StatisticsBoundary();
        var authorize = Signal<bool>();
        var secondEntered = Signal<bool>();
        var secondRelease = Signal<bool>();
        boundary.Runtime.Access.Authorize = _ => boundary.Runtime.Access.Calls == 1 ? authorize.Task : Task.FromResult(true);
        boundary.Load = async (query, token) =>
        {
            if (query.BucketSize == JobExecutionAnalyticsBucketSize.Hour)
            {
                secondEntered.TrySetResult(true);
                await ObserveAsync(secondRelease.Task);
            }
            return await boundary.Runtime.Store.GetExecutionAnalyticsAsync(ExecutionStateTestBoundary.Scope, query, token);
        };
        var state = boundary.Create();
        try
        {
            var admittedAt = boundary.Runtime.Clock.Now;
            var first = state.SetTimeRangeAsync(SchedulerAnalyticsTimeRange.Last7Days);
            Assert.Equal(SchedulerAnalyticsTimeRange.Last7Days, state.TimeRange);
            var second = state.SetTimeRangeAsync(SchedulerAnalyticsTimeRange.Last24Hours);
            Assert.Equal(SchedulerAnalyticsTimeRange.Last24Hours, state.TimeRange);
            boundary.Runtime.Clock.Now = admittedAt.AddMinutes(5);
            authorize.TrySetResult(true);
            await ObserveAsync(first);
            await ObserveAsync(secondEntered.Task);
            Assert.False(state.HasLoadedSnapshot);
            Assert.Null(state.Snapshot);
            secondRelease.TrySetResult(true);
            await ObserveAsync(second);

            Assert.True(state.HasLoadedSnapshot);
            Assert.Equal(SchedulerAnalyticsTimeRange.Last24Hours, state.LoadedTimeRange);
            Assert.Equal(admittedAt, state.LoadedQuery?.EndTimeUtc);
            Assert.Equal(boundary.Runtime.Clock.Now, state.ObservedAtUtc);
            Assert.Collection(boundary.Queries,
                query => Assert.Equal(admittedAt.AddDays(-7), query.StartTimeUtc),
                query => Assert.Equal(admittedAt.AddHours(-24), query.StartTimeUtc));
        }
        finally
        {
            authorize.TrySetResult(true);
            secondRelease.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenFirstReadFailsThenEmptyReadSucceeds_ShouldDistinguishUnavailableFromZero()
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        boundary.Load = (_, _) => Task.FromException<JobExecutionAnalyticsSnapshot>(new IOException("Analytics unavailable"));
        await state.InitializeAsync();
        Assert.False(state.HasLoadedSnapshot);
        Assert.True(state.IsQueryFailed);
        Assert.Contains("Analytics unavailable", state.Error);
        Assert.Null(state.LoadedQuery);
        Assert.Null(state.ObservedAtUtc);

        boundary.Load = null;
        await state.RefreshAsync();
        Assert.True(state.HasLoadedSnapshot);
        Assert.False(state.IsQueryFailed);
        Assert.NotNull(state.Snapshot);
        Assert.Equal(0, state.Snapshot.CompletedTerminalCount);
        Assert.All(state.Snapshot.StateTotals.Values, count => Assert.Equal(0, count));
        Assert.Null(state.Error);
        Assert.NotNull(state.LoadedQuery);
        Assert.Equal(boundary.Runtime.Clock.Now, state.ObservedAtUtc);
    }

    [Fact]
    public async Task RefreshAsync_WhenSamePresetFailsThenNewPresetFails_ShouldRetainOnlySamePresetEvidence()
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        await state.InitializeAsync();
        var snapshot = state.Snapshot;
        var loaded = state.LoadedQuery;
        var observed = state.ObservedAtUtc;
        boundary.Runtime.Clock.Now = boundary.Runtime.Clock.Now.AddMinutes(10);
        boundary.Load = (_, _) => Task.FromException<JobExecutionAnalyticsSnapshot>(new IOException("Read failed"));
        await state.RefreshAsync();
        Assert.True(state.IsQueryFailed);
        Assert.True(state.HasLoadedSnapshot);
        Assert.Same(snapshot, state.Snapshot);
        Assert.Same(loaded, state.LoadedQuery);
        Assert.Equal(observed, state.ObservedAtUtc);
        Assert.Equal(boundary.Runtime.Clock.Now, state.RequestedQuery?.EndTimeUtc);

        await state.SetTimeRangeAsync(SchedulerAnalyticsTimeRange.Last7Days);
        Assert.Equal(SchedulerAnalyticsTimeRange.Last7Days, state.TimeRange);
        Assert.True(state.IsQueryFailed);
        Assert.False(state.HasLoadedSnapshot);
        Assert.Null(state.Snapshot);
        Assert.Null(state.LoadedQuery);
        Assert.Null(state.LoadedTimeRange);
        Assert.Null(state.ObservedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_WhenAccessIsDeniedOrFaults_ShouldClearEvidenceAndNotRead(bool faults)
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        await state.InitializeAsync();
        boundary.Runtime.Access.Authorized = false;
        if (faults) boundary.Runtime.Access.Authorize = _ => throw new IOException("Authorization unavailable");
        await state.RefreshAsync();
        Assert.False(state.IsAuthorized);
        Assert.True(state.AccessChecked);
        Assert.False(state.HasLoadedSnapshot);
        Assert.Null(state.Snapshot);
        Assert.Null(state.LoadedQuery);
        Assert.Null(state.ObservedAtUtc);
        Assert.Single(boundary.Queries);
        Assert.Equal(faults, state.IsQueryFailed);
        if (faults) Assert.Equal("Authorization unavailable", state.Error);
        else Assert.Null(state.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefreshAsync_WhenAccessInvalidatesAnEarlierRead_ShouldNotRestoreItsEvidence(bool reauthorize, bool lateFailure)
    {
        var boundary = new StatisticsBoundary();
        var entered = Signal<bool>();
        var release = Signal<bool>();
        boundary.Load = async (query, token) =>
        {
            entered.TrySetResult(true);
            await ObserveAsync(release.Task);
            if (lateFailure) throw new IOException("Retired access read failed");
            return await boundary.Runtime.Store.GetExecutionAnalyticsAsync(ExecutionStateTestBoundary.Scope, query, token);
        };
        var state = boundary.Create();
        try
        {
            var read = state.RefreshAsync();
            await ObserveAsync(entered.Task);
            boundary.Runtime.Access.Authorized = false;
            Assert.False(await state.ReauthorizeAsync());
            if (reauthorize)
            {
                boundary.Runtime.Access.Authorized = true;
                Assert.True(await state.ReauthorizeAsync());
            }
            release.TrySetResult(true);
            await ObserveAsync(read);
            Assert.Equal(reauthorize, state.IsAuthorized);
            Assert.False(state.HasLoadedSnapshot);
            Assert.Null(state.LoadedQuery);
            Assert.Null(state.Snapshot);
            Assert.Null(state.Error);
            Assert.False(state.IsQueryFailed);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenAnOlderAuthorizationCompletesAfterAProviderFault_ShouldRemainInaccessible()
    {
        var boundary = new StatisticsBoundary();
        var release = Signal<bool>();
        boundary.Runtime.Access.Authorize = _ => boundary.Runtime.Access.Calls == 1
            ? release.Task : throw new IOException("Access provider fault");
        var state = boundary.Create();
        try
        {
            var read = state.RefreshAsync();
            Assert.False(await state.ReauthorizeAsync());
            release.TrySetResult(true);
            await ObserveAsync(read);
            Assert.False(state.IsAuthorized);
            Assert.True(state.IsQueryFailed);
            Assert.Equal("Access provider fault", state.Error);
            Assert.Empty(boundary.Queries);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenAuthorizationIgnoresCancellation_ShouldJoinActualWorkAndCancelQueuedAdmission()
    {
        var boundary = new StatisticsBoundary();
        var release = Signal<bool>();
        boundary.Runtime.Access.Authorize = _ => release.Task;
        var state = boundary.Create();
        var first = state.InitializeAsync();
        var queued = state.RefreshAsync();
        var retirement = state.DisposeAsync().AsTask();
        var repeated = state.DisposeAsync().AsTask();
        try
        {
            Assert.Same(retirement, repeated);
            Assert.False(retirement.IsCompleted);
            Assert.True(state.IsDisposed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.RefreshAsync());
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(first));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(queued));
            await ObserveAsync(retirement);
            Assert.Empty(boundary.Queries);
            Assert.False(state.AccessChecked);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(retirement);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenReadIgnoresCancellation_ShouldWaitForItsActualCompletion()
    {
        var boundary = new StatisticsBoundary();
        var entered = Signal<bool>();
        var release = Signal<bool>();
        boundary.Load = async (query, _) =>
        {
            entered.TrySetResult(true);
            await ObserveAsync(release.Task);
            return await boundary.Runtime.Store.GetExecutionAnalyticsAsync(
                ExecutionStateTestBoundary.Scope, query, TestContext.Current.CancellationToken);
        };
        var state = boundary.Create();
        var read = state.RefreshAsync();
        await ObserveAsync(entered.Task);
        var retirement = state.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(read));
            await ObserveAsync(retirement);
            Assert.False(state.HasLoadedSnapshot);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(retirement);
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenSubscriberReenters_ShouldNotifyOutsideReadSerialization()
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        var reentered = false;
        Func<Task>? subscriber = null;
        subscriber = async () =>
        {
            state.Changed -= subscriber;
            await state.RefreshAsync();
            reentered = true;
        };
        state.Changed += subscriber;
        await ObserveAsync(state.InitializeAsync());
        Assert.True(reentered);
        Assert.Equal(2, boundary.Queries.Count);
        Assert.True(state.HasLoadedSnapshot);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public async Task RefreshAsync_WhenCompletionSubscriberThrows_ShouldReleaseReadSerialization()
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        var notifications = 0;
        Func<Task>? subscriber = null;
        subscriber = () =>
        {
            if (++notifications != 2) return Task.CompletedTask;
            state.Changed -= subscriber;
            throw new IOException("Subscriber failed");
        };
        state.Changed += subscriber;
        var exception = await Assert.ThrowsAsync<IOException>(() => ObserveAsync(state.RefreshAsync()));
        Assert.Equal("Subscriber failed", exception.Message);
        await ObserveAsync(state.RefreshAsync());
        Assert.Equal(2, boundary.Queries.Count);
        Assert.True(state.HasLoadedSnapshot);
    }

    [Fact]
    public async Task SetTimeRangeAsync_WhenPresetIsUndefined_ShouldRejectItWithoutRead()
    {
        var boundary = new StatisticsBoundary();
        await using var state = boundary.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = state.SetTimeRangeAsync((SchedulerAnalyticsTimeRange)999); });
        Assert.Empty(boundary.Queries);
    }

    [Fact]
    public async Task RefreshAsync_WhenWindowCannotBeRepresented_ShouldReportFailureWithoutAnEmptySuccess()
    {
        var boundary = new StatisticsBoundary();
        boundary.Runtime.Clock.Now = DateTimeOffset.MinValue;
        await using var state = boundary.Create();
        await state.RefreshAsync();
        Assert.True(state.IsQueryFailed);
        Assert.False(state.IsLoading);
        Assert.False(state.HasLoadedSnapshot);
        Assert.NotNull(state.Error);
        Assert.Null(state.RequestedQuery);
        Assert.Empty(boundary.Queries);
    }

    [Fact]
    public void CreateQuery_WhenTodayUsesFixedUtcPlusEight_ShouldStartAtSchedulerMidnight()
    {
        var now = new DateTimeOffset(2026, 8, 14, 4, 15, 0, TimeSpan.Zero);
        var presentation = CreatePresentation(CreateFixedTimeZone());

        var query = SchedulerStatisticsPageState.CreateQuery(
            SchedulerAnalyticsTimeRange.Today,
            now,
            presentation,
            "Sample.Jobs.DailyReport");

        query.StartTimeUtc.Should().Be(new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero));
        query.EndTimeUtc.Should().Be(now);
        query.BucketSize.Should().Be(JobExecutionAnalyticsBucketSize.Hour);
        query.JobKey.Should().Be("Sample.Jobs.DailyReport");
    }

    [Fact]
    public void CreateQuery_WhenTodayContainsDaylightSavingTransition_ShouldStartAtSchedulerMidnight()
    {
        var now = new DateTimeOffset(2026, 3, 8, 16, 0, 0, TimeSpan.Zero);
        var presentation = CreatePresentation(CreateDaylightSavingTimeZone());

        var query = SchedulerStatisticsPageState.CreateQuery(
            SchedulerAnalyticsTimeRange.Today,
            now,
            presentation);

        query.StartTimeUtc.Should().Be(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero));
        query.EndTimeUtc.Should().Be(now);
        query.BucketSize.Should().Be(JobExecutionAnalyticsBucketSize.Hour);
    }

    [Fact]
    public void CreateQuery_WhenTodayStartsExactlyNow_ShouldKeepTheSchedulerDayBoundary()
    {
        var now = new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero);

        var query = SchedulerStatisticsPageState.CreateQuery(
            SchedulerAnalyticsTimeRange.Today,
            now,
            CreatePresentation(CreateFixedTimeZone()));

        query.StartTimeUtc.Should().Be(now);
        query.EndTimeUtc.Should().Be(now.AddTicks(1));
        query.BucketSize.Should().Be(JobExecutionAnalyticsBucketSize.Hour);
    }

    [Theory]
    [InlineData(SchedulerAnalyticsTimeRange.Last24Hours, 24, JobExecutionAnalyticsBucketSize.Hour)]
    [InlineData(SchedulerAnalyticsTimeRange.Last7Days, 168, JobExecutionAnalyticsBucketSize.Day)]
    [InlineData(SchedulerAnalyticsTimeRange.Last30Days, 720, JobExecutionAnalyticsBucketSize.Day)]
    public void CreateQuery_WhenRangeIsRolling_ShouldBeTimezoneInvariant(
        SchedulerAnalyticsTimeRange range,
        int hours,
        JobExecutionAnalyticsBucketSize expectedBucketSize)
    {
        var now = new DateTimeOffset(2026, 8, 14, 4, 15, 0, TimeSpan.Zero);

        var fixedZoneQuery = SchedulerStatisticsPageState.CreateQuery(
            range,
            now,
            CreatePresentation(CreateFixedTimeZone()));
        var daylightZoneQuery = SchedulerStatisticsPageState.CreateQuery(
            range,
            now,
            CreatePresentation(CreateDaylightSavingTimeZone()));

        fixedZoneQuery.StartTimeUtc.Should().Be(now.AddHours(-hours));
        fixedZoneQuery.EndTimeUtc.Should().Be(now);
        fixedZoneQuery.BucketSize.Should().Be(expectedBucketSize);
        daylightZoneQuery.StartTimeUtc.Should().Be(fixedZoneQuery.StartTimeUtc);
        daylightZoneQuery.EndTimeUtc.Should().Be(fixedZoneQuery.EndTimeUtc);
        daylightZoneQuery.BucketSize.Should().Be(fixedZoneQuery.BucketSize);
    }

    [Fact]
    public void CreateQuery_WhenBuildingAnalyticsRequest_ShouldRequestTopTenRankingsAndSlowExecutions()
    {
        var query = SchedulerStatisticsPageState.CreateQuery(
            SchedulerAnalyticsTimeRange.Last24Hours,
            new DateTimeOffset(2026, 8, 14, 4, 15, 0, TimeSpan.Zero),
            CreatePresentation(CreateFixedTimeZone()));

        query.TopJobLimit.Should().Be(10);
        query.SlowestExecutionLimit.Should().Be(10);
    }

    private static SchedulerTimePresentation CreatePresentation(TimeZoneInfo timeZone) => new(
        Options.Create(new ModuleJobSchedulerOption { CronTimeZone = timeZone }),
        new EchoStringLocalizer<JobSchedulerResource>(), SchedulerTimePresentationTests.CreateUiTimeFormatter());

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(OBSERVATION_TIMEOUT, TestContext.Current.CancellationToken);
    private static Task<T> ObserveAsync<T>(Task<T> task) => task.WaitAsync(OBSERVATION_TIMEOUT, TestContext.Current.CancellationToken);

    private sealed class StatisticsBoundary
    {
        internal ExecutionStateTestBoundary Runtime { get; } = new();
        internal List<JobExecutionAnalyticsQuery> Queries { get; } = [];
        internal Func<JobExecutionAnalyticsQuery, CancellationToken, Task<JobExecutionAnalyticsSnapshot>>? Load { get; set; }
        private readonly SchedulerStatisticsPageStateFactory _factory;

        internal StatisticsBoundary()
        {
            Runtime.Boundary.GetExecutionAnalyticsAsync(Arg.Any<string>(), Arg.Any<JobExecutionAnalyticsQuery>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var scope = call.ArgAt<string>(0);
                    var query = call.ArgAt<JobExecutionAnalyticsQuery>(1);
                    var token = call.ArgAt<CancellationToken>(2);
                    Assert.Equal(ExecutionStateTestBoundary.Scope, scope);
                    Queries.Add(query);
                    return Load?.Invoke(query, token) ?? Runtime.Store.GetExecutionAnalyticsAsync(scope, query, token);
                });
            var options = Options.Create(new ModuleJobSchedulerOption
            {
                SchedulerScopeKey = ExecutionStateTestBoundary.Scope, CronTimeZone = TimeZoneInfo.Utc
            });
            var facade = new JobSchedulerFacade(Runtime.Boundary, options, Runtime.Clock, new JobSchedulerRuntimeState(),
                NullLogger<JobSchedulerFacade>.Instance);
            _factory = new(facade, Runtime.Access, Runtime.Clock, CreatePresentation(TimeZoneInfo.Utc));
        }

        internal SchedulerStatisticsPageState Create() => _factory.Create();
    }

    private static TimeZoneInfo CreateFixedTimeZone() => TimeZoneInfo.CreateCustomTimeZone(
        "Scheduler/Statistics+08",
        TimeSpan.FromHours(8),
        "Scheduler statistics test time",
        "Scheduler statistics test time");

    private static TimeZoneInfo CreateDaylightSavingTimeZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            3,
            2,
            DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            11,
            1,
            DayOfWeek.Sunday);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Scheduler/Statistics-DST",
            TimeSpan.FromHours(-5),
            "Scheduler statistics daylight test time",
            "Scheduler statistics standard test time",
            "Scheduler statistics daylight test time",
            [adjustment]);
    }
}
