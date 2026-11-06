using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Profiling.ExecutionTiming.Abstractions;
using Monica.Profiling.ExecutionTiming.Facades;
using Monica.Profiling.ExecutionTiming.Models;
using Monica.Profiling.UIExecutionTiming.State;
using Test.Monica.Profiling.Support;
using Test.Monica.Profiling.UIExecutionTiming.Support;
using Xunit;

namespace Test.Monica.Profiling.UIExecutionTiming.State;

[Collection(BlockingProfilingQueryCollection.NAME)]
public sealed class ExecutionTimingPageStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InitializeAsync_WhenPollingIsDisabled_ShouldCaptureRealEvidenceWithoutCreatingATimer(int interval)
    {
        var clock = new TriggerTimingTimeProvider();
        var factory = new ExecutionTimingTestApplicationFactory(interval);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        Complete(recorder, "state.manual-only");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();

        AssertSuccess(await state.InitializeAsync());
        await state.SetAutoRefreshAsync(true);

        state.RefreshIntervalMs.Should().Be(interval);
        state.CanAutoRefresh.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
        state.Statistics.Should().ContainSingle().Which.ExecutionCount.Should().Be(1);
        state.Statistics[0].OperationKey.Should().Be("state.manual-only");
        clock.CreatedTimers.Should().Be(0);
        clock.Tick().Should().Be(0);

        Complete(recorder, "state.manual-only");
        clock.Advance(TimeSpan.FromMinutes(1));
        AssertSuccess(await state.RefreshAsync());
        state.Statistics.Single().ExecutionCount.Should().Be(2);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.AutoRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task SetAutoRefreshAsync_WhenPaused_ShouldStopPagePollingAndRetainCaptureWhileCollectionContinues()
    {
        var clock = new TriggerTimingTimeProvider();
        var factory = new ExecutionTimingTestApplicationFactory();
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        var facade = scope.Resolve<ExecutionTimingFacade>();
        Complete(recorder, "state.pause");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        clock.ActiveTimers.Should().Be(1);
        var capturedAt = state.SnapshotCapturedAt;
        var statistics = state.Statistics;

        await state.SetAutoRefreshAsync(false);
        Complete(recorder, "state.pause");
        clock.Advance(TimeSpan.FromMinutes(1));

        clock.ActiveTimers.Should().Be(0);
        clock.Tick().Should().Be(0);
        state.Statistics.Should().BeSameAs(statistics);
        state.Statistics.Single().ExecutionCount.Should().Be(1);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        var actual = facade.GetStatistics();
        actual.Status.Should().Be(ResultStatus.Ok);
        actual.Message.Should().BeNull();
        actual.Data.Should().ContainSingle().Which.ExecutionCount.Should().Be(2);

        AssertSuccess(await state.RefreshAsync());
        state.Statistics.Single().ExecutionCount.Should().Be(2);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.AutoRefresh.Should().BeFalse();
        clock.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenSameOperationHasTwoInvocations_ShouldRetainExactProvenanceUntilEachEnds()
    {
        var factory = new ExecutionTimingTestApplicationFactory(0);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        var firstId = Guid.Parse("719ca2f1-1f72-48b8-928a-c02fe9c50201");
        var secondId = Guid.Parse("719ca2f1-1f72-48b8-928a-c02fe9c50202");
        using var first = recorder.BeginInvocation("state.shared", "Shared name", firstId, "First invocation");
        using var second = recorder.BeginInvocation("state.shared", "Shared name", secondId, "Second invocation");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();

        AssertSuccess(await state.InitializeAsync());
        state.RunningOperations.Select(static operation => operation.InvocationId)
            .Should().BeEquivalentTo([firstId, secondId]);
        state.RunningOperations.Should().OnlyContain(static operation =>
            operation.OperationKey == "state.shared" && operation.DisplayName == "Shared name");
        state.RunningOperations.Single(operation => operation.InvocationId == firstId)
            .Description.Should().Be("First invocation");

        first.Dispose();
        AssertSuccess(await state.RefreshAsync());
        state.RunningOperations.Should().ContainSingle().Which.InvocationId.Should().Be(secondId);
        state.Statistics.Should().ContainSingle().Which.ExecutionCount.Should().Be(1);

        second.Dispose();
        AssertSuccess(await state.RefreshAsync());
        state.RunningOperations.Should().BeEmpty();
        state.Statistics.Should().ContainSingle().Which.ExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsync_WhenSecondReadFails_ShouldRetainBothListsAndCaptureThenPublishRecovery()
    {
        var clock = new TriggerTimingTimeProvider();
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(0, queryControl: control);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        Complete(recorder, "state.retained");
        using var running = recorder.BeginScope("state.running", "Running detail");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        var statistics = state.Statistics;
        var runningOperations = state.RunningOperations;
        var capturedAt = state.SnapshotCapturedAt;
        Complete(recorder, "state.retained");
        running.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        control.FailRunning("Controlled running read unavailable.");
        var originalFailure = scope.Resolve<ExecutionTimingFacade>().GetRunningOperations();
        originalFailure.Status.Should().Be(ResultStatus.BadRequest);
        originalFailure.Message.Should().Contain("Controlled running read unavailable.");
        originalFailure.Data.Should().BeNull();

        var failure = await state.RefreshAsync();

        failure.Status.Should().Be(originalFailure.Status);
        failure.Message.Should().Be(originalFailure.Message);
        state.LoadError.Should().Be(failure.Message);
        state.HasSnapshot.Should().BeTrue();
        state.Statistics.Should().BeSameAs(statistics);
        state.RunningOperations.Should().BeSameAs(runningOperations);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.IsLoading.Should().BeFalse();

        control.ClearFailures();
        AssertSuccess(await state.RefreshAsync());
        state.Statistics.Single(item => item.OperationKey == "state.retained").ExecutionCount.Should().Be(2);
        state.Statistics.Single(item => item.OperationKey == "state.running").ExecutionCount.Should().Be(1);
        state.RunningOperations.Should().BeEmpty();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task InitializeAsync_WhenFirstReadFails_ShouldDistinguishFailureFromGenuinelyEmptyRecovery()
    {
        var control = new TimingQueryControl();
        control.FailStatistics("Initial read unavailable.");
        var factory = new ExecutionTimingTestApplicationFactory(0, queryControl: control);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();

        var failure = await state.InitializeAsync();
        var originalFailure = scope.Resolve<ExecutionTimingFacade>().GetStatistics();
        originalFailure.Status.Should().Be(ResultStatus.BadRequest);
        originalFailure.Message.Should().Contain("Initial read unavailable.");
        originalFailure.Data.Should().BeNull();

        failure.Status.Should().Be(originalFailure.Status);
        failure.Message.Should().Be(originalFailure.Message);
        state.HasSnapshot.Should().BeFalse();
        state.SnapshotCapturedAt.Should().BeNull();
        state.LoadError.Should().Be(failure.Message);
        state.Statistics.Should().BeEmpty();
        state.RunningOperations.Should().BeEmpty();
        control.RunningReads.Should().Be(0);

        control.ClearFailures();
        AssertSuccess(await state.RefreshAsync());
        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().NotBeNull();
        state.LoadError.Should().BeNull();
        state.Statistics.Should().BeEmpty();
        state.RunningOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task Polling_WhenReadFails_ShouldKeepErrorAndPriorCaptureUntilNextSuccessfulTick()
    {
        var clock = new TriggerTimingTimeProvider();
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(queryControl: control);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        Complete(recorder, "state.periodic");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        var capturedAt = state.SnapshotCapturedAt;
        Complete(recorder, "state.periodic");
        control.FailRunning("Periodic read unavailable.");
        var originalFailure = scope.Resolve<ExecutionTimingFacade>().GetRunningOperations();
        originalFailure.Status.Should().Be(ResultStatus.BadRequest);
        originalFailure.Message.Should().Contain("Periodic read unavailable.");
        originalFailure.Data.Should().BeNull();
        var failed = NextSettledChange(state, () => state.LoadError is not null);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(failed);

        state.Statistics.Single().ExecutionCount.Should().Be(1);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().Be(originalFailure.Message);
        state.AutoRefresh.Should().BeTrue();
        clock.ActiveTimers.Should().Be(1);

        control.ClearFailures();
        var recovered = NextSettledChange(state, () => state.LoadError is null && state.SnapshotCapturedAt == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(recovered);
        state.Statistics.Single().ExecutionCount.Should().Be(2);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenAnotherReadIsHeld_ShouldRejectOverlapWithoutChangingRetainedError()
    {
        var clock = new TriggerTimingTimeProvider();
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(queryControl: control);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        control.FailRunning("Retained failure.");
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.BadRequest);
        var error = state.LoadError;
        var capturedAt = state.SnapshotCapturedAt;
        control.ClearFailures();
        using var readGate = control.HoldNextStatisticsRead();
        var held = Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        try
        {
            await AwaitBounded(readGate.Entered);
            state.IsLoading.Should().BeTrue();
            var busy = await state.RefreshAsync();
            busy.Status.Should().Be(ResultStatus.Conflict);
            busy.Message.Should().BeEmpty();
            state.LoadError.Should().Be(error);
            state.SnapshotCapturedAt.Should().Be(capturedAt);
            clock.Tick().Should().Be(1);
            await AwaitBounded(state.SetAutoRefreshAsync(false));
            control.MaximumConcurrentReads.Should().Be(1);
            control.StatisticsReads.Should().Be(3);
            control.RunningReads.Should().Be(2);
            clock.ActiveTimers.Should().Be(0);
        }
        finally
        {
            readGate.Release();
            await AwaitBounded(held);
        }

        AssertSuccess(await held);
        control.MaximumConcurrentReads.Should().Be(1);
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetAutoRefreshAsync_WhenTransitionsOverlap_ShouldDrainCancelledReadBeforeStartingOneSuccessor(bool holdRunningRead)
    {
        var clock = new TriggerTimingTimeProvider();
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(queryControl: control);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        Complete(recorder, "state.transitions");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        var capturedAt = state.SnapshotCapturedAt;
        using var readGate = holdRunningRead ? control.HoldNextRunningRead() : control.HoldNextStatisticsRead();
        clock.Advance(TimeSpan.FromSeconds(2));
        Task? pause = null;
        Task? resume = null;
        Task<int>? tick = null;
        try
        {
            // A real timer callback runs away from the test thread. Do not hold that thread in the query seam.
            tick = Task.Run(clock.Tick, TestContext.Current.CancellationToken);
            await AwaitBounded(readGate.Entered);
            pause = state.SetAutoRefreshAsync(false);
            resume = state.SetAutoRefreshAsync(true);
            pause.IsCompleted.Should().BeFalse();
            resume.IsCompleted.Should().BeFalse();
            state.AutoRefresh.Should().BeFalse();
            clock.CreatedTimers.Should().Be(1);
            Complete(recorder, "state.transitions");
        }
        finally
        {
            readGate.Release();
            if (tick is not null)
            {
                await AwaitBounded(tick);
            }
            await AwaitBounded(Task.WhenAll(pause ?? Task.CompletedTask, resume ?? Task.CompletedTask));
            if (pause is null)
            {
                await AwaitBounded(state.SetAutoRefreshAsync(false));
            }
        }

        (await tick!).Should().Be(1);
        state.AutoRefresh.Should().BeTrue();
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.Statistics.Single().ExecutionCount.Should().Be(1);
        control.RunningReads.Should().Be(holdRunningRead ? 2 : 1);
        clock.CreatedTimers.Should().Be(2);
        clock.ActiveTimers.Should().Be(1);
        clock.MaximumActiveTimers.Should().Be(1);
        var refreshed = NextSettledChange(state, () => state.SnapshotCapturedAt == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(refreshed);
        state.Statistics.Single().ExecutionCount.Should().Be(2);
        control.MaximumConcurrentReads.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeAsync_WhenReadIsHeld_ShouldShareDrainAndRejectLateWorkWithoutPublication(bool periodicRead, bool holdRunningRead)
    {
        var clock = new TriggerTimingTimeProvider();
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(queryControl: control);
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var recorder = scope.Resolve<IExecutionTimingFactory>();
        Complete(recorder, "state.dispose");
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        AssertSuccess(await state.InitializeAsync());
        var statistics = state.Statistics;
        var capturedAt = state.SnapshotCapturedAt;
        var notifications = 0;
        state.StateChanged += () => Interlocked.Increment(ref notifications);
        using var readGate = holdRunningRead ? control.HoldNextRunningRead() : control.HoldNextStatisticsRead();
        Task<Result>? manualRead = null;
        Task<int>? tick = null;
        if (periodicRead)
        {
            tick = Task.Run(clock.Tick, TestContext.Current.CancellationToken);
        }
        else
        {
            manualRead = Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        }

        var notificationCount = 0;
        try
        {
            await AwaitBounded(readGate.Entered);
            var firstDispose = state.DisposeAsync().AsTask();
            var secondDispose = state.DisposeAsync().AsTask();
            notificationCount = Volatile.Read(ref notifications);
            firstDispose.IsCompleted.Should().BeFalse();
            secondDispose.Should().BeSameAs(firstDispose);
            var lateRefresh = await state.RefreshAsync();
            lateRefresh.Status.Should().Be(ResultStatus.Conflict);
            lateRefresh.Message.Should().BeEmpty();
            var lateInitialize = state.InitializeAsync();
            var lateEnable = state.SetAutoRefreshAsync(true);
            Complete(recorder, "state.dispose");
            clock.Advance(TimeSpan.FromSeconds(2));
            control.StatisticsReads.Should().Be(2);
            control.RunningReads.Should().Be(holdRunningRead ? 2 : 1);
            state.AutoRefresh.Should().BeFalse();
            state.IsLoading.Should().BeFalse();
            readGate.Release();
            await AwaitBounded(firstDispose);
            await AwaitBounded(lateEnable);
            var initialized = await lateInitialize.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            initialized.Status.Should().Be(ResultStatus.Conflict);
            initialized.Message.Should().BeEmpty();
        }
        finally
        {
            readGate.Release();
            await AwaitBounded(state.DisposeAsync().AsTask());
            if (tick is not null)
            {
                await AwaitBounded(tick);
            }
            if (manualRead is not null)
            {
                await AwaitBounded(manualRead);
            }
        }

        state.Statistics.Should().BeSameAs(statistics);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().BeNull();
        Volatile.Read(ref notifications).Should().Be(notificationCount);
        clock.ActiveTimers.Should().Be(0);
        clock.Tick().Should().Be(0);
        control.StatisticsReads.Should().Be(2);
        if (tick is not null)
        {
            (await tick).Should().Be(1);
        }
        if (manualRead is not null)
        {
            (await manualRead).Status.Should().Be(ResultStatus.Conflict);
        }

        var actual = scope.Resolve<ExecutionTimingFacade>().GetStatistics();
        actual.Status.Should().Be(ResultStatus.Ok);
        actual.Message.Should().BeNull();
        actual.Data.Should().ContainSingle().Which.ExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsync_WhenLoadingSubscriberRetiresPage_ShouldDrainWithoutStartingAQuery()
    {
        var control = new TimingQueryControl();
        var factory = new ExecutionTimingTestApplicationFactory(0, queryControl: control);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        Task? disposal = null;
        state.StateChanged += () =>
        {
            if (state.IsLoading)
            {
                disposal = state.DisposeAsync().AsTask();
            }
        };

        var result = await state.RefreshAsync();

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Message.Should().BeEmpty();
        disposal.Should().NotBeNull();
        await AwaitBounded(disposal!);
        control.StatisticsReads.Should().Be(0);
        control.RunningReads.Should().Be(0);
        state.HasSnapshot.Should().BeFalse();
        state.SnapshotCapturedAt.Should().BeNull();
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenOnePageIsRetired_ShouldKeepOtherStateAndHostMeasurementsIndependent()
    {
        var clock = new TriggerTimingTimeProvider();
        var factory = new ExecutionTimingTestApplicationFactory();
        await using var application = await factory.CreateAsync(
            scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var stateFactory = scope.Resolve<ExecutionTimingPageStateFactory>();
        await using var first = stateFactory.Create();
        await using var second = stateFactory.Create();
        first.Should().NotBeSameAs(second);
        AssertSuccess(await first.InitializeAsync());
        AssertSuccess(await second.InitializeAsync());
        clock.ActiveTimers.Should().Be(2);

        await first.DisposeAsync();
        Complete(scope.Resolve<IExecutionTimingFactory>(), "state.other-page");
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertSuccess(await second.RefreshAsync());

        first.Statistics.Should().BeEmpty();
        first.AutoRefresh.Should().BeFalse();
        second.Statistics.Should().ContainSingle().Which.OperationKey.Should().Be("state.other-page");
        second.AutoRefresh.Should().BeTrue();
        second.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        clock.ActiveTimers.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_WhenBackgroundAggregationHasPendingCompletion_ShouldReadRealSampleWithoutWaitingForFlushInterval()
    {
        var factory = new ExecutionTimingTestApplicationFactory(0, ExecutionTimingAggregationMode.BackgroundBatch);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await using var state = scope.Resolve<ExecutionTimingPageStateFactory>().Create();
        Complete(scope.Resolve<IExecutionTimingFactory>(), "state.background");

        AssertSuccess(await state.RefreshAsync());

        state.Statistics.Should().ContainSingle().Which.OperationKey.Should().Be("state.background");
        state.Statistics[0].ExecutionCount.Should().Be(1);
        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().NotBeNull();
        state.LoadError.Should().BeNull();
    }

    private static void Complete(IExecutionTimingFactory recorder, string operationKey)
    {
        using var invocation = recorder.BeginScope(operationKey);
    }

    private static void AssertSuccess(Result result)
    {
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeEmpty();
    }

    private static Task AwaitBounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static Task NextSettledChange(ExecutionTimingPageState state, Func<bool> predicate)
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
