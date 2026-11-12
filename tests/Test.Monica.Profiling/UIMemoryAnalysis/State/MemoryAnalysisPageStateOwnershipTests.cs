using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Profiling.MemoryDiagnostics.Facades;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.UIMemoryAnalysis.State;
using Test.Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.UIMemoryAnalysis.State;

public sealed partial class MemoryAnalysisPageStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RefreshAsync_WhenAReadIsHeld_ShouldRejectOverlappingReadsAndActions(int stageIndex)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(allowManualGc: true, allowGcDump: true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        control.Fail(MemoryReadStage.Trend, "Retained read error.");
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.ServiceUnavailable);
        control.ClearFailures();
        var error = state.LoadError;
        var snapshot = state.CurrentSnapshot;
        using var gate = control.HoldNextRead((MemoryReadStage)stageIndex);
        var read = Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        try
        {
            await AwaitBounded(gate.Entered);
            state.IsLoading.Should().BeTrue();
            AssertInactive(await state.RefreshAsync());
            AssertInactive(await state.ForceGarbageCollectionAsync(2));
            AssertInactive(await state.CreateGcDumpAsync());
            clock.Tick().Should().Be(1);
            await AwaitBounded(state.SetAutoRefreshAsync(false));
            state.CurrentSnapshot.Should().BeSameAs(snapshot);
            state.LoadError.Should().Be(error);
            control.GcActions.Should().Be(0);
            control.DumpActions.Should().Be(0);
            control.MaximumConcurrentReads.Should().Be(1);
        }
        finally
        {
            gate.Release();
            await AwaitBounded(read);
        }
        AssertSuccess(await read);
        state.LoadError.Should().BeNull();
        state.AutoRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Polling_WhenAReadFails_ShouldRetainFailureUntilASuccessfulTick()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.CurrentSnapshot;
        var capturedAt = state.SnapshotCapturedAt;
        control.Fail(MemoryReadStage.GcDetails, "Periodic GC read failed.");
        var failed = Settled(state, () => state.LoadError is not null);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(failed);
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().Be("Periodic GC read failed.");
        state.AutoRefresh.Should().BeTrue();
        control.ClearFailures();
        var recovered = Settled(state, () => state.LoadError is null);
        clock.Tick().Should().Be(1);
        await AwaitBounded(recovered);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        clock.ActiveTimers.Should().Be(1);
    }

    [Fact]
    public async Task SetAutoRefreshAsync_WhenTransitionsOverlap_ShouldDrainTheCancelledReadBeforeOneSuccessorLoop()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.CurrentSnapshot;
        var capturedAt = state.SnapshotCapturedAt;
        using var gate = control.HoldNextRead(MemoryReadStage.Snapshot);
        var tick = Task.Run(clock.Tick, TestContext.Current.CancellationToken);
        Task? pause = null;
        Task? resume = null;
        try
        {
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
            await AwaitBounded(tick);
            await AwaitBounded(Task.WhenAll(pause ?? Task.CompletedTask, resume ?? Task.CompletedTask));
        }
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        control.ReadOrder.Should().Equal(MemoryReadStage.Snapshot, MemoryReadStage.GcDetails, MemoryReadStage.Trend, MemoryReadStage.Snapshot);
        state.AutoRefresh.Should().BeTrue();
        clock.CreatedTimers.Should().Be(2);
        clock.ActiveTimers.Should().Be(1);
        clock.MaximumActiveTimers.Should().Be(1);
        var captured = Settled(state, () => state.SnapshotCapturedAt == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(1);
        await AwaitBounded(captured);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task DisposeAsync_WhenAnyGenuineReadIsHeld_ShouldShareDrainAndRejectLatePublication(int stageIndex, bool periodic)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(allowManualGc: true, allowGcDump: true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.CurrentSnapshot;
        var gc = state.CurrentGcDetails;
        var trend = state.TrendData;
        var capturedAt = state.SnapshotCapturedAt;
        var notifications = 0;
        state.StateChanged += () => Interlocked.Increment(ref notifications);
        using var gate = control.HoldNextRead((MemoryReadStage)stageIndex);
        Task<Result>? read = periodic ? null : Task.Run(state.RefreshAsync, TestContext.Current.CancellationToken);
        Task<int>? tick = periodic ? Task.Run(clock.Tick, TestContext.Current.CancellationToken) : null;
        var notifiedBeforeRetirement = 0;
        try
        {
            await AwaitBounded(gate.Entered);
            var first = state.DisposeAsync().AsTask();
            var second = state.DisposeAsync().AsTask();
            second.Should().BeSameAs(first);
            first.IsCompleted.Should().BeFalse();
            notifiedBeforeRetirement = Volatile.Read(ref notifications);
            AssertInactive(await state.RefreshAsync());
            AssertInactive(await state.ForceGarbageCollectionAsync(-1));
            AssertInactive(await state.CreateGcDumpAsync());
            var initialize = state.InitializeAsync();
            var resume = state.SetAutoRefreshAsync(true);
            gate.Release();
            await AwaitBounded(first);
            await AwaitBounded(resume);
            AssertInactive(await initialize);
        }
        finally
        {
            gate.Release();
            await AwaitBounded(state.DisposeAsync().AsTask());
            if (read is not null) await AwaitBounded(read);
            if (tick is not null) await AwaitBounded(tick);
        }
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.CurrentGcDetails.Should().BeSameAs(gc);
        state.TrendData.Should().BeSameAs(trend);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().BeNull();
        state.LastAction.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
        Volatile.Read(ref notifications).Should().Be(notifiedBeforeRetirement);
        control.ReadOrder.Count.Should().Be(3 + stageIndex + 1);
        control.GcActions.Should().Be(0);
        control.DumpActions.Should().Be(0);
        clock.ActiveTimers.Should().Be(0);
        clock.Tick().Should().Be(0);
        if (read is not null) AssertInactive(await read);
        scope.Resolve<MemoryDiagnosticsFacade>().GetCurrentSnapshot().IsSuccess().Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Operation_WhenItsLoadingSubscriberRetiresThePage_ShouldNotInvokeTheBoundary(int operation)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, true, true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        Task? disposal = null;
        state.StateChanged += () => { if (state.IsLoading) disposal = state.DisposeAsync().AsTask(); };

        IResultEnvelope result = operation switch
        {
            1 => await state.ForceGarbageCollectionAsync(0),
            2 => await RunDump(state),
            _ => await state.RefreshAsync()
        };

        AssertInactive(result);
        disposal.Should().NotBeNull();
        await AwaitBounded(disposal!);
        control.ReadOrder.Should().BeEmpty();
        control.GcActions.Should().Be(0);
        control.DumpActions.Should().Be(0);
        state.HasSnapshot.Should().BeFalse();
        state.LastAction.Should().BeNull();
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenDiagnosticsStartsWithoutPolling_ShouldKeepPageAndCollectorOwnershipIndependent()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var factory = scope.Resolve<MemoryAnalysisPageStateFactory>();
        await using var overview = factory.Create();
        await using var diagnostics = factory.Create(false);
        overview.Should().NotBeSameAs(diagnostics);
        AssertSuccess(await overview.InitializeAsync());
        AssertSuccess(await diagnostics.InitializeAsync());
        diagnostics.AutoRefresh.Should().BeFalse();
        diagnostics.CanAutoRefresh.Should().BeTrue();
        clock.ActiveTimers.Should().Be(1);
        var snapshot = diagnostics.CurrentSnapshot;
        var capturedAt = diagnostics.SnapshotCapturedAt;
        var runtime = scope.Resolve<RuntimeMetricsFacade>();
        var firstPoint = await NextRealPoint(runtime);

        await overview.DisposeAsync();
        await NextRealPoint(runtime, firstPoint);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Tick().Should().Be(0);
        diagnostics.CurrentSnapshot.Should().BeSameAs(snapshot);
        diagnostics.SnapshotCapturedAt.Should().Be(capturedAt);
        AssertSuccess(await diagnostics.RefreshAsync());
        diagnostics.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        diagnostics.AutoRefresh.Should().BeFalse();
        scope.Resolve<MemoryDiagnosticsFacade>().GetGcDetails().IsSuccess().Should().BeTrue();
    }
}
