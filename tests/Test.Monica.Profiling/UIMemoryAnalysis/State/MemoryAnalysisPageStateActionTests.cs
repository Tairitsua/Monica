using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Profiling.UIMemoryAnalysis.State;
using Test.Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.UIMemoryAnalysis.State;

public sealed partial class MemoryAnalysisPageStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DiagnosticActions_WhenPolicyDeniesAnAction_ShouldRejectBeforeTheBoundary(bool allowGc, bool allowDump)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, allowGc, allowDump), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        if (!allowGc)
        {
            var result = await state.ForceGarbageCollectionAsync(2);
            result.Status.Should().Be(ResultStatus.Forbidden);
            result.Message.Should().BeEmpty();
        }
        if (!allowDump)
        {
            var result = await state.CreateGcDumpAsync();
            result.Status.Should().Be(ResultStatus.Forbidden);
            result.Message.Should().BeEmpty();
        }
        control.GcActions.Should().Be(0);
        control.DumpActions.Should().Be(0);
        control.ReadOrder.Should().BeEmpty();
        state.LastAction.Should().BeNull();
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task DiagnosticActions_WhenFollowUpEvidenceFails_ShouldKeepTheOriginalSuccessfulAction(bool dump, int stageIndex)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, true, true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        var snapshot = state.CurrentSnapshot;
        var capturedAt = state.SnapshotCapturedAt;
        control.Fail((MemoryReadStage)stageIndex, "Follow-up read failed.");
        using var gate = control.HoldNextAction();
        var action = dump ? RunDump(state) : RunGc(state, -1);
        var startedAt = clock.GetUtcNow();
        var kind = dump ? MemoryDiagnosticAction.GcDump : MemoryDiagnosticAction.GarbageCollection;
        try
        {
            await AwaitBounded(gate.Entered);
            state.IsActionRunning.Should().BeTrue();
            state.ActiveAction.Should().Be(kind);
            state.ActionStartedAt.Should().Be(startedAt);
            state.IsLoading.Should().BeTrue();
            AssertInactive(await state.RefreshAsync());
            AssertInactive(await state.ForceGarbageCollectionAsync(0));
            AssertInactive(await state.CreateGcDumpAsync());
            clock.Advance(TimeSpan.FromSeconds(3));
        }
        finally
        {
            gate.Release();
            await AwaitBounded(action);
        }
        var result = await action;
        result.Should().BeSameAs(dump ? (object)control.DumpResult : control.GcResult);
        if (dump) ((Result<string>)result).Data.Should().Be(control.DumpResult.Data);
        state.LastAction!.Action.Should().Be(kind);
        state.LastAction.StartedAt.Should().Be(startedAt);
        state.LastAction.CompletedAt.Should().Be(clock.GetUtcNow());
        state.LastAction.Status.Should().Be(result.Status);
        state.LastAction.Message.Should().Be(result.Message);
        state.LastAction.IsSuccessful.Should().BeTrue();
        state.LastAction.Generation.Should().Be(dump ? null : -1);
        state.LastAction.ServerDumpPath.Should().Be(dump ? control.DumpResult.Data : null);
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().Be("Follow-up read failed.");
        state.ActiveAction.Should().BeNull();
        state.ActionStartedAt.Should().BeNull();
        state.IsActionRunning.Should().BeFalse();
        state.IsLoading.Should().BeFalse();
        var actualAction = state.LastAction;
        control.ClearFailures();
        AssertSuccess(await state.RefreshAsync());
        state.LastAction.Should().BeSameAs(actualAction);
        state.LoadError.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticActions_WhenActualActionFails_ShouldKeepItsEnvelopeWithoutReadingOrReplacingLoadError(bool dump)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, true, true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        control.Fail(MemoryReadStage.Snapshot, "Prior read error.");
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.ServiceUnavailable);
        var reads = control.ReadOrder.Count;
        var snapshot = state.CurrentSnapshot;
        var capturedAt = state.SnapshotCapturedAt;
        control.GcResult = Result.Failure(string.Empty, ResultStatus.BadGateway);
        control.DumpResult = new Result<string> { Message = null, Status = ResultStatus.GatewayTimeout };

        IResultEnvelope result = dump ? await RunDump(state) : await state.ForceGarbageCollectionAsync(1);

        result.Should().BeSameAs(dump ? (object)control.DumpResult : control.GcResult);
        state.LastAction!.IsSuccessful.Should().BeFalse();
        state.LastAction.Status.Should().Be(result.Status);
        state.LastAction.Message.Should().Be(result.Message);
        state.LastAction.ServerDumpPath.Should().BeNull();
        state.LoadError.Should().Be("Prior read error.");
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        control.ReadOrder.Count.Should().Be(reads);
        state.IsActionRunning.Should().BeFalse();
        state.IsLoading.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhenAnActionIsHeld_ShouldDrainActualCompletionWithoutPublishingOrReading(bool dump)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(allowManualGc: true, allowGcDump: true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.CurrentSnapshot;
        var capturedAt = state.SnapshotCapturedAt;
        var notifications = 0;
        state.StateChanged += () => Interlocked.Increment(ref notifications);
        using var gate = control.HoldNextAction();
        var action = dump ? RunDump(state) : RunGc(state, 2);
        var retiredNotifications = 0;
        try
        {
            await AwaitBounded(gate.Entered);
            var first = state.DisposeAsync().AsTask();
            first.IsCompleted.Should().BeFalse();
            state.DisposeAsync().AsTask().Should().BeSameAs(first);
            retiredNotifications = Volatile.Read(ref notifications);
            AssertInactive(await state.RefreshAsync());
            AssertInactive(await state.CreateGcDumpAsync());
            AssertInactive(await state.ForceGarbageCollectionAsync(-1));
            var resume = state.SetAutoRefreshAsync(true);
            gate.Release();
            await AwaitBounded(first);
            await AwaitBounded(resume);
        }
        finally
        {
            gate.Release();
            await AwaitBounded(action);
            await AwaitBounded(state.DisposeAsync().AsTask());
        }
        AssertInactive(await action);
        state.LastAction.Should().BeNull();
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        state.IsActionRunning.Should().BeFalse();
        control.ReadOrder.Count.Should().Be(3);
        control.GcActions.Should().Be(dump ? 0 : 1);
        control.DumpActions.Should().Be(dump ? 1 : 0);
        Volatile.Read(ref notifications).Should().Be(retiredNotifications);
        clock.ActiveTimers.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticActions_WhenHeldDuringPauseAndResume_ShouldKeepOneAdmissionAndPublishTheSuccessfulRead(bool dump)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(allowManualGc: true, allowGcDump: true), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.CurrentSnapshot;
        using var gate = control.HoldNextAction();
        var action = dump ? RunDump(state) : RunGc(state, 0);
        try
        {
            await AwaitBounded(gate.Entered);
            clock.Tick().Should().Be(1);
            await AwaitBounded(state.SetAutoRefreshAsync(false));
            await AwaitBounded(state.SetAutoRefreshAsync(true));
            clock.Tick().Should().Be(1);
            control.ReadOrder.Count.Should().Be(3);
            state.CurrentSnapshot.Should().BeSameAs(snapshot);
            state.IsActionRunning.Should().BeTrue();
            clock.MaximumActiveTimers.Should().Be(1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        finally
        {
            gate.Release();
            await AwaitBounded(action);
        }
        (await action).IsSuccess().Should().BeTrue();
        state.LastAction!.IsSuccessful.Should().BeTrue();
        state.CurrentSnapshot.Should().NotBeSameAs(snapshot);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        state.AutoRefresh.Should().BeTrue();
        clock.ActiveTimers.Should().Be(1);
        control.ReadOrder.Count.Should().Be(6);
    }
}
