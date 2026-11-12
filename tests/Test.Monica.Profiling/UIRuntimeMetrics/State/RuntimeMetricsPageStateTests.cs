using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;
using Monica.Profiling.UIRuntimeMetrics.State;
using Monica.Testing.Hosting;
using Test.Monica.Profiling.Support;
using Test.Monica.Profiling.UIRuntimeMetrics.Support;
using Xunit;

namespace Test.Monica.Profiling.UIRuntimeMetrics.State;

[Collection(BlockingProfilingQueryCollection.NAME)]
public sealed class RuntimeMetricsPageStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InitializeAsync_WhenPagePollingIsDisabled_ShouldReadRealHistoryWithoutCreatingAPageTimer(int interval)
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        var factory = new RuntimeMetricsTestApplicationFactory(interval);
        await using var application = await Start(factory, clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await NextRealPoint(scope.Resolve<RuntimeMetricsFacade>());
        await using var state = scope.Resolve<RuntimeMetricsPageStateFactory>().Create();

        AssertSuccess(await state.InitializeAsync());
        await state.SetAutoRefreshAsync(true);

        state.RefreshIntervalMs.Should().Be(interval);
        state.CanAutoRefresh.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
        state.CurrentDataPoint.Should().NotBeNull();
        state.CurrentDataPoint.Should().BeSameAs(state.TrendData!.DataPoints[^1]);
        state.PreviousDataPoint.Should().BeNull();
        clock.CreatedTimers.Should().Be(0);
        clock.Tick().Should().Be(0);

        var prior = state.CurrentDataPoint;
        await NextRealPoint(scope.Resolve<RuntimeMetricsFacade>(), prior);
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertSuccess(await state.RefreshAsync());
        state.PreviousDataPoint.Should().BeSameAs(prior);
        state.CurrentDataPoint.Should().NotBeSameAs(prior);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.AutoRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task SetAutoRefreshAsync_WhenPaused_ShouldRetainTheViewWhileTheRealCollectorContinues()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        await using var state = scope.Resolve<RuntimeMetricsPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        var trend = state.TrendData;
        var prior = state.CurrentDataPoint;
        var capturedAt = state.SnapshotCapturedAt;

        await state.SetAutoRefreshAsync(false);
        await NextRealPoint(facade, prior);
        clock.Advance(TimeSpan.FromSeconds(2));

        clock.ActiveTimers.Should().Be(0);
        clock.Tick().Should().Be(0);
        state.TrendData.Should().BeSameAs(trend);
        state.CurrentDataPoint.Should().BeSameAs(prior);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        var actual = facade.GetTrend();
        AssertGenuineSuccess(actual);
        actual.Data!.DataPoints[^1].Should().NotBeSameAs(prior);

        AssertSuccess(await state.RefreshAsync());
        state.PreviousDataPoint.Should().BeSameAs(prior);
        state.CurrentDataPoint.Should().NotBeSameAs(prior);
        state.AutoRefresh.Should().BeFalse();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task RefreshAsync_WhenSourceListChangesAfterPublication_ShouldKeepTheDetachedViewAndCollectorEvidence()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var control = new RuntimeMetricsReadControl(facade);
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        var source = control.LastGenuineTrend!;
        var retained = state.TrendData!;
        var points = retained.DataPoints.ToArray();

        retained.Should().NotBeSameAs(source);
        retained.DataPoints.Should().NotBeSameAs(source.DataPoints);
        retained.MaxHistoryPoints.Should().Be(source.MaxHistoryPoints);
        retained.SampleIntervalMs.Should().Be(source.SampleIntervalMs);
        source.DataPoints.Clear();

        retained.DataPoints.Should().Equal(points);
        state.CurrentDataPoint.Should().BeSameAs(points[^1]);
        var actual = facade.GetTrend();
        AssertGenuineSuccess(actual);
        actual.Data!.DataPoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenBoundaryReturnsTheSameGenuineSample_ShouldUseThePreviousSuccessfulViewForDelta()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var control = new RuntimeMetricsReadControl(facade);
        control.PinNextGenuineResult();
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        var point = state.CurrentDataPoint;
        clock.Advance(TimeSpan.FromSeconds(2));

        AssertSuccess(await state.RefreshAsync());

        state.CurrentDataPoint.Should().BeSameAs(point);
        state.PreviousDataPoint.Should().BeSameAs(point);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        control.Reads.Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsync_WhenRetainedHistoryIsEmpty_ShouldCaptureSuccessWithoutSubstitutingTheLatestCollectorPoint()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0, maxHistoryPoints: 0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        await using var state = scope.Resolve<RuntimeMetricsPageStateFactory>().Create();

        AssertSuccess(await state.RefreshAsync());

        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.TrendData.Should().NotBeNull();
        state.TrendData!.MaxHistoryPoints.Should().Be(0);
        state.TrendData.DataPoints.Should().BeEmpty();
        state.CurrentDataPoint.Should().BeNull();
        state.PreviousDataPoint.Should().BeNull();
        state.LoadError.Should().BeNull();
        // This read is test evidence: the page itself must not fall back to GetLatestPoint.
        var actual = facade.GetLatestPoint();
        actual.Status.Should().Be(ResultStatus.Ok);
        actual.Message.Should().BeNull();
        actual.Data.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Runtime view boundary unavailable.")]
    public async Task RefreshAsync_WhenPageReadBoundaryFails_ShouldRetainAllEvidenceAndOriginalFailureThenRecover(string message)
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var control = new RuntimeMetricsReadControl(facade);
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        AssertSuccess(await state.RefreshAsync());
        var trend = state.TrendData;
        var current = state.CurrentDataPoint;
        var previous = state.PreviousDataPoint;
        var capturedAt = state.SnapshotCapturedAt;
        var original = control.FailReads(message);
        clock.Advance(TimeSpan.FromSeconds(2));

        var failed = await state.RefreshAsync();

        failed.Status.Should().Be(original.Status);
        failed.Message.Should().Be(original.Message);
        state.LoadError.Should().Be(message);
        state.TrendData.Should().BeSameAs(trend);
        state.CurrentDataPoint.Should().BeSameAs(current);
        state.PreviousDataPoint.Should().BeSameAs(previous);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.HasSnapshot.Should().BeTrue();
        state.IsLoading.Should().BeFalse();

        control.ClearFailure();
        await NextRealPoint(facade, current);
        AssertSuccess(await state.RefreshAsync());
        state.PreviousDataPoint.Should().BeSameAs(current);
        state.CurrentDataPoint.Should().NotBeSameAs(current);
        state.LoadError.Should().BeNull();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task InitializeAsync_WhenFirstReadFails_ShouldDistinguishItFromSuccessfulEmptyHistory()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0, sampleIntervalMs: int.MaxValue), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = new RuntimeMetricsReadControl(scope.Resolve<RuntimeMetricsFacade>());
        var original = control.FailReads(string.Empty);
        await using var state = CreateDecorated(scope, clock, control);

        var failed = await state.InitializeAsync();

        failed.Status.Should().Be(original.Status);
        failed.Message.Should().BeEmpty();
        state.HasSnapshot.Should().BeFalse();
        state.TrendData.Should().BeNull();
        state.SnapshotCapturedAt.Should().BeNull();
        state.LoadError.Should().BeEmpty();
        control.ClearFailure();
        AssertSuccess(await state.RefreshAsync());
        state.HasSnapshot.Should().BeTrue();
        state.TrendData!.DataPoints.Should().BeEmpty();
        state.CurrentDataPoint.Should().BeNull();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task Polling_WhenReadFails_ShouldRetainErrorAndCaptureUntilSuccessfulTick()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var control = new RuntimeMetricsReadControl(facade);
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var trend = state.TrendData;
        var capturedAt = state.SnapshotCapturedAt;
        var failure = control.FailReads("Periodic view read failed.");
        var failed = NextSettledChange(state, () => state.LoadError is not null);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(failed);

        state.LoadError.Should().Be(failure.Message);
        state.TrendData.Should().BeSameAs(trend);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.AutoRefresh.Should().BeTrue();
        clock.ActiveTimers.Should().Be(1);

        control.ClearFailure();
        var recovered = NextSettledChange(state, () => state.LoadError is null && state.SnapshotCapturedAt == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(recovered);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenReadIsHeld_ShouldRejectOverlapWithoutClearingRetainedError()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await NextRealPoint(scope.Resolve<RuntimeMetricsFacade>());
        var control = new RuntimeMetricsReadControl(scope.Resolve<RuntimeMetricsFacade>());
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        control.FailReads("Retained page read error.");
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.ServiceUnavailable);
        var error = state.LoadError;
        var capturedAt = state.SnapshotCapturedAt;
        control.ClearFailure();
        using var gate = control.HoldNextRead();
        var held = Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        try
        {
            await AwaitBounded(gate.Entered);
            state.IsLoading.Should().BeTrue();
            var busy = await state.RefreshAsync();
            busy.Status.Should().Be(ResultStatus.Conflict);
            busy.Message.Should().BeEmpty();
            state.LoadError.Should().Be(error);
            state.SnapshotCapturedAt.Should().Be(capturedAt);
            clock.Tick().Should().Be(1);
            await AwaitBounded(state.SetAutoRefreshAsync(false));
            control.Reads.Should().Be(3);
            control.MaximumConcurrentReads.Should().Be(1);
        }
        finally
        {
            gate.Release();
            await AwaitBounded(held);
        }

        AssertSuccess(await held);
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task SetAutoRefreshAsync_WhenTransitionsOverlap_ShouldDrainCancelledReadBeforeStartingOneSuccessor()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await NextRealPoint(scope.Resolve<RuntimeMetricsFacade>());
        var control = new RuntimeMetricsReadControl(scope.Resolve<RuntimeMetricsFacade>());
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var trend = state.TrendData;
        var capturedAt = state.SnapshotCapturedAt;
        using var gate = control.HoldNextRead();
        Task? pause = null;
        Task? resume = null;
        Task<int>? tick = null;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            tick = Task.Run(clock.Tick, TestContext.Current.CancellationToken);
            await AwaitBounded(gate.Entered);
            pause = state.SetAutoRefreshAsync(false);
            resume = state.SetAutoRefreshAsync(true);
            pause.IsCompleted.Should().BeFalse();
            resume.IsCompleted.Should().BeFalse();
            state.AutoRefresh.Should().BeFalse();
            clock.CreatedTimers.Should().Be(1);
        }
        finally
        {
            gate.Release();
            if (tick is not null) await AwaitBounded(tick);
            await AwaitBounded(Task.WhenAll(pause ?? Task.CompletedTask, resume ?? Task.CompletedTask));
            if (pause is null) await AwaitBounded(state.SetAutoRefreshAsync(false));
        }

        (await tick!).Should().Be(1);
        state.TrendData.Should().BeSameAs(trend);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.AutoRefresh.Should().BeTrue();
        clock.CreatedTimers.Should().Be(2);
        clock.ActiveTimers.Should().Be(1);
        clock.MaximumActiveTimers.Should().Be(1);
        var settled = NextSettledChange(state, () => state.SnapshotCapturedAt == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(settled);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        control.MaximumConcurrentReads.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhenGenuineReadResultIsHeld_ShouldShareDrainAndRejectLatePublication(bool periodicRead)
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var control = new RuntimeMetricsReadControl(facade);
        await using var state = CreateDecorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var trend = state.TrendData;
        var current = state.CurrentDataPoint;
        var capturedAt = state.SnapshotCapturedAt;
        var notifications = 0;
        state.StateChanged += () => Interlocked.Increment(ref notifications);
        using var gate = control.HoldNextRead();
        Task<Result>? read = periodicRead ? null : Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        Task<int>? tick = periodicRead ? Task.Run(clock.Tick, TestContext.Current.CancellationToken) : null;
        var notifiedBeforeRetirement = 0;
        try
        {
            await AwaitBounded(gate.Entered);
            var firstDispose = state.DisposeAsync().AsTask();
            var secondDispose = state.DisposeAsync().AsTask();
            notifiedBeforeRetirement = Volatile.Read(ref notifications);
            firstDispose.IsCompleted.Should().BeFalse();
            secondDispose.Should().BeSameAs(firstDispose);
            var lateRead = await state.RefreshAsync();
            lateRead.Status.Should().Be(ResultStatus.Conflict);
            lateRead.Message.Should().BeEmpty();
            var lateInitialize = state.InitializeAsync();
            var lateEnable = state.SetAutoRefreshAsync(true);
            clock.Advance(TimeSpan.FromSeconds(2));
            state.AutoRefresh.Should().BeFalse();
            control.Reads.Should().Be(2);
            gate.Release();
            await AwaitBounded(firstDispose);
            await AwaitBounded(lateEnable);
            var initialized = await lateInitialize.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            initialized.Status.Should().Be(ResultStatus.Conflict);
            initialized.Message.Should().BeEmpty();
        }
        finally
        {
            gate.Release();
            await AwaitBounded(state.DisposeAsync().AsTask());
            if (read is not null) await AwaitBounded(read);
            if (tick is not null) await AwaitBounded(tick);
        }

        state.TrendData.Should().BeSameAs(trend);
        state.CurrentDataPoint.Should().BeSameAs(current);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        Volatile.Read(ref notifications).Should().Be(notifiedBeforeRetirement);
        clock.ActiveTimers.Should().Be(0);
        clock.Tick().Should().Be(0);
        control.Reads.Should().Be(2);
        if (read is not null) (await read).Status.Should().Be(ResultStatus.Conflict);
        if (tick is not null) (await tick).Should().Be(1);
        await NextRealPoint(facade, current);
        var actual = facade.GetTrend();
        AssertGenuineSuccess(actual);
        actual.Data!.DataPoints[^1].Should().NotBeSameAs(current);
    }

    [Fact]
    public async Task RefreshAsync_WhenLoadingSubscriberRetiresPage_ShouldDrainWithoutReading()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = new RuntimeMetricsReadControl(scope.Resolve<RuntimeMetricsFacade>());
        await using var state = CreateDecorated(scope, clock, control);
        Task? disposal = null;
        state.StateChanged += () => { if (state.IsLoading) disposal = state.DisposeAsync().AsTask(); };

        var result = await state.RefreshAsync();

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Message.Should().BeEmpty();
        disposal.Should().NotBeNull();
        await AwaitBounded(disposal!);
        control.Reads.Should().Be(0);
        state.HasSnapshot.Should().BeFalse();
        state.TrendData.Should().BeNull();
        state.SnapshotCapturedAt.Should().BeNull();
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenOneFactoryStateIsRetired_ShouldKeepTheOtherPageAndCollectorIndependent()
    {
        var clock = new TriggerRuntimeMetricsTimeProvider();
        await using var application = await Start(new RuntimeMetricsTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(facade);
        var factory = scope.Resolve<RuntimeMetricsPageStateFactory>();
        await using var first = factory.Create();
        await using var second = factory.Create();
        first.Should().NotBeSameAs(second);
        AssertSuccess(await first.InitializeAsync());
        AssertSuccess(await second.InitializeAsync());
        clock.ActiveTimers.Should().Be(2);
        var firstTrend = first.TrendData;
        var prior = second.CurrentDataPoint;

        await first.DisposeAsync();
        await NextRealPoint(facade, prior);
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertSuccess(await second.RefreshAsync());

        first.TrendData.Should().BeSameAs(firstTrend);
        first.AutoRefresh.Should().BeFalse();
        second.CurrentDataPoint.Should().NotBeSameAs(prior);
        second.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        second.AutoRefresh.Should().BeTrue();
        clock.ActiveTimers.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Construct_WhenUsingOriginalTwoArgumentSignature_ShouldKeepTheRealFacadeAndSystemClock(bool useFactory)
    {
        var factory = new RuntimeMetricsTestApplicationFactory(0, sampleIntervalMs: int.MaxValue);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<RuntimeMetricsFacade>();
        var options = scope.Resolve<IOptions<ModuleRuntimeMetricsUIOption>>();
        typeof(RuntimeMetricsPageState).GetConstructor([typeof(RuntimeMetricsFacade), typeof(IOptions<ModuleRuntimeMetricsUIOption>)])
            .Should().NotBeNull();
        typeof(RuntimeMetricsPageStateFactory).GetConstructor([typeof(RuntimeMetricsFacade), typeof(IOptions<ModuleRuntimeMetricsUIOption>)])
            .Should().NotBeNull();
        await using var state = useFactory
            ? new RuntimeMetricsPageStateFactory(facade, options).Create()
            : new RuntimeMetricsPageState(facade, options);
        var before = DateTimeOffset.UtcNow;

        AssertSuccess(await state.InitializeAsync());

        state.SnapshotCapturedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        state.SnapshotCapturedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        state.HasSnapshot.Should().BeTrue();
        state.TrendData!.DataPoints.Should().BeEmpty();
        state.AutoRefresh.Should().BeFalse();
    }

    private static Task<MonicaTestApplication> Start(RuntimeMetricsTestApplicationFactory factory, TimeProvider clock) =>
        factory.CreateAsync(scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);

    private static RuntimeMetricsPageState CreateDecorated(MonicaTestScope scope, TimeProvider clock, RuntimeMetricsReadControl control) =>
        new(scope.Resolve<RuntimeMetricsFacade>(), scope.Resolve<IOptions<ModuleRuntimeMetricsUIOption>>(), clock, control.Read);

    private static async Task<RuntimeMetricsPoint> NextRealPoint(RuntimeMetricsFacade facade, RuntimeMetricsPoint? differentFrom = null)
    {
        var captured = new TaskCompletionSource<RuntimeMetricsPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = facade.SubscribeToUpdates(point =>
        {
            if (!ReferenceEquals(point, differentFrom)) captured.TrySetResult(point);
        });
        try
        {
            var actual = facade.GetTrend();
            AssertGenuineSuccess(actual);
            var latest = actual.Data!.DataPoints.LastOrDefault();
            if (latest is not null && !ReferenceEquals(latest, differentFrom)) return latest;
            return await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { unsubscribe(); }
    }

    private static void AssertSuccess(Result result)
    {
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeEmpty();
    }

    private static void AssertGenuineSuccess(Result<RuntimeMetricsTrend> result)
    {
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    private static Task AwaitBounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static Task NextSettledChange(RuntimeMetricsPageState state, Func<bool> predicate)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.StateChanged += Handle;
        return changed.Task;

        void Handle()
        {
            if (!state.IsLoading && predicate())
            {
                state.StateChanged -= Handle;
                changed.TrySetResult();
            }
        }
    }
}
