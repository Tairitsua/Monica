using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Profiling.MemoryDiagnostics.Facades;
using Monica.Profiling.RuntimeMetrics.Facades;
using Monica.Profiling.RuntimeMetrics.Models;
using Monica.Profiling.UIMemoryAnalysis.State;
using Monica.Testing.Hosting;
using Test.Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.UIMemoryAnalysis.State;

public sealed partial class MemoryAnalysisPageStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InitializeAsync_WhenPollingIsManualOnly_ShouldCaptureRealEvidenceWithoutATimer(int interval)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(interval), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var runtime = scope.Resolve<RuntimeMetricsFacade>();
        await NextRealPoint(runtime);
        await using var state = scope.Resolve<MemoryAnalysisPageStateFactory>().Create();

        AssertSuccess(await state.InitializeAsync());
        await state.SetAutoRefreshAsync(true);

        state.HasSnapshot.Should().BeTrue();
        state.CurrentSnapshot.Should().NotBeNull();
        state.CurrentGcDetails.Should().NotBeNull();
        state.CurrentSnapshot!.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        state.TrendData!.DataPoints.Should().NotBeEmpty();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
        state.RefreshIntervalMs.Should().Be(interval);
        state.CanAutoRefresh.Should().BeFalse();
        state.AutoRefresh.Should().BeFalse();
        clock.CreatedTimers.Should().Be(0);
        var prior = state.TrendData.DataPoints[^1];
        await NextRealPoint(runtime, prior);
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertSuccess(await state.RefreshAsync());
        state.TrendData.DataPoints[^1].Should().NotBeSameAs(prior);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.AutoRefresh.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(8, int.MaxValue)]
    public async Task RefreshAsync_WhenHistoryIsEmpty_ShouldPublishRealMemoryAndGcEvidence(int capacity, int sampleInterval)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, maxHistoryPoints: capacity, sampleIntervalMs: sampleInterval), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        if (capacity == 0) await NextRealLatest(scope.Resolve<RuntimeMetricsFacade>());
        await using var state = scope.Resolve<MemoryAnalysisPageStateFactory>().Create();

        AssertSuccess(await state.InitializeAsync());

        state.HasSnapshot.Should().BeTrue();
        state.CurrentSnapshot.Should().NotBeNull();
        state.CurrentGcDetails.Should().NotBeNull();
        state.TrendData!.DataPoints.Should().BeEmpty();
        state.TrendData.MaxHistoryPoints.Should().Be(capacity);
        state.TrendData.SampleIntervalMs.Should().Be(sampleInterval);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenFacadeContainersChange_ShouldKeepTheDetachedThreeReadView()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await NextRealPoint(scope.Resolve<RuntimeMetricsFacade>());
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());

        control.ReadOrder.Should().Equal(MemoryReadStage.Snapshot, MemoryReadStage.GcDetails, MemoryReadStage.Trend);
        state.CurrentSnapshot.Should().NotBeSameAs(control.LastGenuineSnapshot).And.BeEquivalentTo(control.LastGenuineSnapshot);
        state.CurrentGcDetails.Should().NotBeSameAs(control.LastGenuineGcDetails).And.BeEquivalentTo(control.LastGenuineGcDetails);
        state.CurrentGcDetails!.GenerationDetails.Should().NotBeSameAs(control.LastGenuineGcDetails!.GenerationDetails);
        for (var i = 0; i < state.CurrentGcDetails.GenerationDetails.Length; i++)
            state.CurrentGcDetails.GenerationDetails[i].Should().NotBeSameAs(control.LastGenuineGcDetails.GenerationDetails[i]);
        state.TrendData!.DataPoints.Should().NotBeSameAs(control.LastGenuineTrend!.DataPoints).And.Equal(control.LastGenuineTrend.DataPoints);
        var points = state.TrendData.DataPoints.ToArray();
        var generations = state.CurrentGcDetails.GenerationDetails.ToArray();

        control.LastGenuineTrend.DataPoints.Clear();
        Array.Clear(control.LastGenuineGcDetails.GenerationDetails);

        state.TrendData.DataPoints.Should().Equal(points);
        state.CurrentGcDetails.GenerationDetails.Should().Equal(generations);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        scope.Resolve<RuntimeMetricsFacade>().GetTrend().Data!.DataPoints.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0, "Snapshot unavailable.")]
    [InlineData(1, "GC evidence unavailable.")]
    [InlineData(2, "History unavailable.")]
    [InlineData(0, "")]
    [InlineData(1, null)]
    [InlineData(2, "")]
    public async Task RefreshAsync_WhenAnyReadFails_ShouldRetainTheWholeViewAndOriginalFailure(int stageIndex, string? message)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        await using var state = Decorated(scope, clock, control);
        AssertSuccess(await state.RefreshAsync());
        var snapshot = state.CurrentSnapshot;
        var gc = state.CurrentGcDetails;
        var trend = state.TrendData;
        var capturedAt = state.SnapshotCapturedAt;
        var failure = control.Fail((MemoryReadStage)stageIndex, message, ResultStatus.GatewayTimeout);
        failure.SetMetadata("source", "page-boundary");
        var readsBefore = control.ReadOrder.Count;
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = await state.RefreshAsync();

        result.Status.Should().Be(failure.Status);
        result.Message.Should().Be(failure.Message);
        result.Metadata.Should().NotBeSameAs(failure.Metadata).And.BeEquivalentTo(failure.Metadata);
        state.LoadError.Should().Be(message ?? string.Empty);
        state.CurrentSnapshot.Should().BeSameAs(snapshot);
        state.CurrentGcDetails.Should().BeSameAs(gc);
        state.TrendData.Should().BeSameAs(trend);
        state.SnapshotCapturedAt.Should().Be(capturedAt);
        state.HasSnapshot.Should().BeTrue();
        state.IsLoading.Should().BeFalse();
        control.ReadOrder.Count.Should().Be(readsBefore + stageIndex + 1);
        control.ClearFailures();
        AssertSuccess(await state.RefreshAsync());
        state.CurrentSnapshot.Should().NotBeSameAs(snapshot);
        state.CurrentGcDetails.Should().NotBeSameAs(gc);
        state.TrendData.Should().NotBeSameAs(trend);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
        state.LoadError.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InitializeAsync_WhenTheFirstViewFails_ShouldNotPublishPartialEvidence(int stageIndex)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, sampleIntervalMs: int.MaxValue), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var control = Boundary(scope);
        control.Fail((MemoryReadStage)stageIndex, string.Empty);
        await using var state = Decorated(scope, clock, control);

        (await state.InitializeAsync()).Status.Should().Be(ResultStatus.ServiceUnavailable);

        state.HasSnapshot.Should().BeFalse();
        state.CurrentSnapshot.Should().BeNull();
        state.CurrentGcDetails.Should().BeNull();
        state.TrendData.Should().BeNull();
        state.SnapshotCapturedAt.Should().BeNull();
        state.LoadError.Should().BeEmpty();
        control.ClearFailures();
        AssertSuccess(await state.RefreshAsync());
        state.HasSnapshot.Should().BeTrue();
        state.TrendData!.DataPoints.Should().BeEmpty();
        state.LoadError.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Construct_WhenUsingOriginalSignatures_ShouldKeepRealFacadesAndTheSystemClock(bool useFactory)
    {
        await using var application = await new MemoryAnalysisTestApplicationFactory(0).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var memory = scope.Resolve<MemoryDiagnosticsFacade>();
        var runtime = scope.Resolve<RuntimeMetricsFacade>();
        var options = scope.Resolve<IOptions<ModuleMemoryAnalysisUIOption>>();
        Type[] signature = [typeof(MemoryDiagnosticsFacade), typeof(RuntimeMetricsFacade), typeof(IOptions<ModuleMemoryAnalysisUIOption>)];
        typeof(MemoryAnalysisPageState).GetConstructor(signature).Should().NotBeNull();
        typeof(MemoryAnalysisPageStateFactory).GetConstructor(signature).Should().NotBeNull();
        typeof(MemoryAnalysisPageStateFactory).GetMethod(nameof(MemoryAnalysisPageStateFactory.Create), Type.EmptyTypes).Should().NotBeNull();
        await using var state = useFactory
            ? new MemoryAnalysisPageStateFactory(memory, runtime, options).Create()
            : new MemoryAnalysisPageState(memory, runtime, options);
        var before = DateTimeOffset.UtcNow;

        AssertSuccess(await state.InitializeAsync());

        state.HasSnapshot.Should().BeTrue();
        state.SnapshotCapturedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        state.SnapshotCapturedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        state.Options.Should().BeSameAs(options.Value);
        state.AutoRefresh.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WhenAllocationIsOptional_ShouldReflectHostCompositionWithoutStartingIt(bool enabled)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(new MemoryAnalysisTestApplicationFactory(0, enableAllocation: enabled), clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await using var state = scope.Resolve<MemoryAnalysisPageStateFactory>().Create(false);

        AssertSuccess(await state.InitializeAsync());

        state.IsTypeAllocationTabEnabled.Should().Be(enabled);
        state.Options.EnableTypeAllocationTab.Should().Be(enabled);
        state.AutoRefresh.Should().BeFalse();
    }

    private static MemoryAnalysisBoundaryControl Boundary(MonicaTestScope scope) =>
        new(scope.Resolve<MemoryDiagnosticsFacade>(), scope.Resolve<RuntimeMetricsFacade>());

    private static MemoryAnalysisPageState Decorated(MonicaTestScope scope, TimeProvider clock, MemoryAnalysisBoundaryControl boundary, bool autoRefresh = true) =>
        new(scope.Resolve<MemoryDiagnosticsFacade>(), scope.Resolve<RuntimeMetricsFacade>(), scope.Resolve<IOptions<ModuleMemoryAnalysisUIOption>>(),
            clock, autoRefresh, boundary.ReadSnapshot, boundary.ReadGcDetails, boundary.ReadTrend, boundary.ForceGarbageCollection, boundary.CreateGcDumpAsync);

    private static Task<MonicaTestApplication> Start(MemoryAnalysisTestApplicationFactory factory, TimeProvider clock) =>
        factory.CreateAsync(scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);

    private static async Task<IResultEnvelope> RunDump(MemoryAnalysisPageState state) => await state.CreateGcDumpAsync();

    private static async Task<IResultEnvelope> RunGc(MemoryAnalysisPageState state, int generation) =>
        await Task.Run(() => state.ForceGarbageCollectionAsync(generation), TestContext.Current.CancellationToken);

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
            actual.IsSuccess().Should().BeTrue();
            var latest = actual.Data!.DataPoints.LastOrDefault();
            if (latest is not null && !ReferenceEquals(latest, differentFrom)) return latest;
            return await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { unsubscribe(); }
    }

    private static async Task<RuntimeMetricsPoint> NextRealLatest(RuntimeMetricsFacade facade)
    {
        var captured = new TaskCompletionSource<RuntimeMetricsPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = facade.SubscribeToUpdates(point => captured.TrySetResult(point));
        try
        {
            var actual = facade.GetLatestPoint();
            if (actual.IsSuccess() && actual.Data is not null) return actual.Data;
            return await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { unsubscribe(); }
    }

    private static void AssertSuccess(Result result)
    {
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeEmpty();
    }

    private static void AssertInactive(IResultEnvelope result)
    {
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Message.Should().BeEmpty();
    }

    private static Task AwaitBounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static Task Settled(MemoryAnalysisPageState state, Func<bool> predicate)
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
