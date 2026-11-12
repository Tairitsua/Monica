using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Profiling.TypeAllocation.Facades;
using Monica.Profiling.TypeAllocation.Models;
using Monica.Profiling.UIMemoryAnalysis.State;
using Monica.Testing.Hosting;
using Test.Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.UIMemoryAnalysis.State;

/// <summary>
/// Protects page ownership through real optional module/facade composition. Structural registry
/// fixtures exercise selection and detachment; they are not measurements or native integration proof.
/// </summary>
public sealed class TypeAllocationPanelStateTests
{
    [Fact]
    public async Task InitializeAsync_WhenModuleIsEnabled_ShouldReadIdleFacadeWithoutStartingItAndKeepIndependentPages()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<TypeAllocationFacade>();
        var factory = scope.Resolve<TypeAllocationPanelStateFactory>();
        await using var first = factory.Create();
        await using var second = factory.Create();

        AssertSuccess(await first.InitializeAsync());
        AssertSuccess(await second.InitializeAsync());
        facade.IsCollecting.Should().BeFalse();
        first.LiveSnapshot!.DataSource.Should().Be(TypeAllocationDataSource.None);
        first.LiveCapturedAt.Should().Be(clock.GetUtcNow());
        second.LiveSnapshot.Should().NotBeSameAs(first.LiveSnapshot);
        first.SearchQuery = "First page only";
        second.SearchQuery.Should().BeEmpty();

        await first.DisposeAsync();
        AssertSuccess(facade.ResetData());
        AssertSuccess(second.RefreshData());
        AssertSuccess(facade.GetCurrentSnapshot());
        facade.IsCollecting.Should().BeFalse();
        second.LiveSnapshot!.DataSource.Should().Be(TypeAllocationDataSource.None);
        second.LoadError.Should().BeNull();
    }

    [Fact]
    public async Task SelectedSource_WhenHeapHasNoEvidence_ShouldStayEmptyWithoutCapturingOrDiscardingLiveEvidence()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        var live = state.LiveSnapshot;

        state.SelectedSource = TypeAllocationDataSource.HeapSnapshot;

        state.Snapshot.Should().BeNull();
        state.CapturedAt.Should().BeNull();
        state.GetView().Count.Should().Be(0);
        boundary.HeapCaptureCount.Should().Be(0);
        state.SelectedSource = TypeAllocationDataSource.AllocationTracking;
        state.Snapshot.Should().BeSameAs(live);
    }

    [Fact]
    public async Task TakeHeapSnapshotAsync_WhenLiveEvidenceUpdates_ShouldKeepBothDetachedSourcesAndSelectedHeap()
    {
        RequireHeapPageAdmission();
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        var liveInput = Registry(TypeAllocationDataSource.AllocationTracking);
        var heapInput = Registry(TypeAllocationDataSource.HeapSnapshot, byteOffset: 5000);
        boundary.LiveResult = Result.Success(liveInput);
        boundary.HeapResult = Result.Success(heapInput);
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertSuccess(await state.TakeHeapSnapshotAsync());
        var selectedHeap = state.HeapSnapshot;
        var heapCapture = state.HeapCapturedAt;
        state.SelectedSource.Should().Be(TypeAllocationDataSource.HeapSnapshot);
        selectedHeap.Should().NotBeSameAs(heapInput).And.BeEquivalentTo(heapInput);
        state.LiveSnapshot.Should().NotBeSameAs(liveInput).And.BeEquivalentTo(liveInput);

        liveInput.Types[0].TotalBytes = -100;
        heapInput.Types[0].AllocationCount = -100;
        state.LiveSnapshot!.Types[0].TotalBytes.Should().Be(1300);
        selectedHeap!.Types[0].AllocationCount.Should().Be(10);
        clock.Advance(TimeSpan.FromSeconds(3));
        var update = Registry(TypeAllocationDataSource.AllocationTracking, byteOffset: 9000);
        boundary.Publish(update);

        state.LiveSnapshot.Should().NotBeSameAs(update).And.BeEquivalentTo(update);
        state.LiveCapturedAt.Should().Be(clock.GetUtcNow());
        state.HeapSnapshot.Should().BeSameAs(selectedHeap);
        state.HeapCapturedAt.Should().Be(heapCapture);
        state.Snapshot.Should().BeSameAs(selectedHeap);
        state.CapturedAt.Should().Be(heapCapture);
        state.TopN = 5;
        state.SetPage(2);
        state.GetView().Page.Should().Be(2);
        state.SelectedSource = TypeAllocationDataSource.HeapSnapshot;
        state.GetView().Page.Should().Be(2, "selecting the current source must not discard the operator's page");
        AssertSuccess(state.ResetData());
        state.HeapSnapshot.Should().BeSameAs(selectedHeap);
        state.GetView().Page.Should().Be(2);
        state.SelectedSource.Should().Be(TypeAllocationDataSource.HeapSnapshot);
        scope.Resolve<TypeAllocationFacade>().IsCollecting.Should().BeFalse();
    }

    [Theory]
    [InlineData(TypeAllocationSortOrder.BytesDescending, false)]
    [InlineData(TypeAllocationSortOrder.CountDescending, true)]
    [InlineData(TypeAllocationSortOrder.NameAscending, false)]
    public async Task GetView_WhenOrderingChanges_ShouldOrderEveryRetainedIdentityBeforePaging(
        TypeAllocationSortOrder order, bool reverse)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        state.TopN = 5;
        state.SortOrder = order;

        var expected = Identities();
        if (reverse) Array.Reverse(expected);
        var view = state.GetView();
        view.MatchingTypes.Select(row => row.TypeName).Should().Equal(expected);
        view.VisibleTypes.Select(row => row.TypeName).Should().Equal(expected.Take(5));
        view.Count.Should().Be(13);
        view.PageCount.Should().Be(3);
        view.Snapshot!.TotalAllocationCount.Should().Be(1410);
        view.Snapshot!.TotalBytes.Should().Be(9600);
    }

    [Fact]
    public async Task GetView_WhenPagesAndFullNameQueryChange_ShouldReachAllRowsAndClampOnlyThePage()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        state.TopN = 5;
        var reached = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            state.SetPage(page);
            var view = state.GetView();
            view.Page.Should().Be(page);
            view.FirstVisible.Should().Be((page - 1) * 5 + 1);
            view.LastVisible.Should().Be(Math.Min(page * 5, 13));
            reached.AddRange(view.VisibleTypes.Select(row => row.TypeName));
        }
        reached.Should().Equal(Identities());
        reached.Distinct(StringComparer.Ordinal).Should().HaveCount(13);
        state.SetPage(99);
        state.GetView().Page.Should().Be(3);

        state.ShowFullTypeName = false;
        state.SearchQuery = "  DOMAIN06  ";
        var match = state.GetView();
        match.Page.Should().Be(1);
        match.VisibleTypes.Should().ContainSingle().Which.TypeName.Should().Be("Fixture.Domain06.Widget");
        match.Snapshot!.Types.Should().HaveCount(13);
        state.SearchQuery = "No matching full identity";
        var empty = state.GetView();
        empty.Count.Should().Be(0);
        empty.PageCount.Should().Be(1);
        empty.FirstVisible.Should().Be(0);
        empty.LastVisible.Should().Be(0);
        state.SearchQuery = string.Empty;
        state.GetView().Count.Should().Be(13);
    }

    [Fact]
    public async Task Inspect_WhenShortNamesCollideAndSourcesChange_ShouldFreezeExactIdentityAndCompleteSourceEvidence()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        state.TopN = 5;
        state.SetPage(2);
        var view = state.GetView();
        var row = view.VisibleTypes[0];
        row.ShortTypeName.Should().Be("Widget");
        view.VisibleTypes[1].ShortTypeName.Should().Be("Widget");
        var evidence = view.Inspect(row)!;
        evidence.TypeName.Should().Be("Fixture.Domain06.Widget");
        evidence.Count.Should().Be(60);
        evidence.Bytes.Should().Be(800);
        evidence.Source.Should().Be(TypeAllocationDataSource.AllocationTracking);
        evidence.SourceCount.Should().Be(1410);
        evidence.SourceBytes.Should().Be(9600);
        evidence.DroppedEventCount.Should().Be(3);
        evidence.Duration.Should().Be(TimeSpan.FromTicks(12345));
        evidence.SnapshotTimestamp.Should().Be(RegistryTimestamp);
        evidence.LastSeenUtc.Should().Be(RegistryTimestamp.AddTicks(6));
        evidence.CapturedAt.Should().Be(clock.GetUtcNow());
        view.Inspect(state.LiveSnapshot!.Types[5]).Should().BeNull();

        row.AllocationCount = -1;
        row.TotalBytes = -1;
        row.AllocationPercentage = -1;
        state.LiveSnapshot!.Types[5].TotalBytes.Should().Be(800);
        state.GetView().VisibleTypes[0].TotalBytes.Should().Be(800);
        clock.Advance(TimeSpan.FromSeconds(9));
        boundary.Publish(Registry(TypeAllocationDataSource.AllocationTracking, byteOffset: 10000));
        state.SelectedSource = TypeAllocationDataSource.HeapSnapshot;

        evidence.Count.Should().Be(60);
        evidence.Bytes.Should().Be(800);
        evidence.CountShare.Should().Be(100d * 60 / 1410);
        evidence.ByteShare.Should().Be(100d * 800 / 9600);
        evidence.CapturedAt.Should().Be(view.CapturedAt);
        evidence.TypeName.Should().Be("Fixture.Domain06.Widget");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Actual read boundary failure")]
    public async Task RefreshData_WhenReadFails_ShouldPreserveEvidenceAndItsCaptureTime(string? message)
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        var live = state.LiveSnapshot;
        var capturedAt = state.LiveCapturedAt;
        clock.Advance(TimeSpan.FromSeconds(5));
        boundary.LiveResult = new() { Status = ResultStatus.BadGateway, Message = message };

        var failed = state.RefreshData();

        failed.Status.Should().Be(ResultStatus.BadGateway);
        state.LoadError.Should().Be(message ?? string.Empty);
        state.LiveSnapshot.Should().BeSameAs(live);
        state.LiveCapturedAt.Should().Be(capturedAt);
        state.LastAction.Should().BeNull();
        state.IsLoading.Should().BeFalse();
        boundary.LiveResult = null;
        AssertSuccess(state.RefreshData());
        state.LoadError.Should().BeNull();
        state.LiveSnapshot!.DataSource.Should().Be(TypeAllocationDataSource.None);
        state.LiveCapturedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task TakeHeapSnapshotAsync_WhenCaptureFails_ShouldKeepTheLastHeapAndIndependentReadFailure()
    {
        RequireHeapPageAdmission();
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.HeapResult = Result.Success(Registry(TypeAllocationDataSource.HeapSnapshot));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        AssertSuccess(await state.TakeHeapSnapshotAsync());
        var heap = state.HeapSnapshot;
        var capture = state.HeapCapturedAt;
        boundary.LiveResult = new() { Status = ResultStatus.ServiceUnavailable, Message = "Prior read failure" };
        state.RefreshData().Status.Should().Be(ResultStatus.ServiceUnavailable);
        boundary.HeapResult = new() { Status = ResultStatus.GatewayTimeout, Message = null };
        var startedAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await state.TakeHeapSnapshotAsync();

        result.Should().BeSameAs(boundary.HeapResult);
        var failedAction = state.LastAction!;
        failedAction.Operation.Should().Be(TypeAllocationOperation.HeapCapture);
        failedAction.IsSuccessful.Should().BeFalse();
        failedAction.Status.Should().Be(ResultStatus.GatewayTimeout);
        failedAction.Message.Should().BeNull();
        failedAction.StartedAt.Should().Be(startedAt.AddSeconds(1));
        state.HeapSnapshot.Should().BeSameAs(heap);
        state.HeapCapturedAt.Should().Be(capture);
        state.SelectedSource.Should().Be(TypeAllocationDataSource.HeapSnapshot);
        state.LoadError.Should().Be("Prior read failure");
    }

    [Fact]
    public async Task InitializeAndDispose_WhenRepeated_ShouldOwnOneSubscriptionAndIgnorePreviouslyCapturedCallbacks()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        AssertSuccess(await state.InitializeAsync());
        boundary.SubscriptionCount.Should().Be(1);
        var live = state.LiveSnapshot;
        var capture = state.LiveCapturedAt;
        var notifications = 0;
        state.StateChanged += () => notifications++;

        var first = state.DisposeAsync().AsTask();
        state.DisposeAsync().AsTask().Should().BeSameAs(first);
        await AwaitBounded(first);
        boundary.UnsubscriptionCount.Should().Be(1);
        boundary.Publish(Registry(TypeAllocationDataSource.AllocationTracking));
        AssertInactive(await state.InitializeAsync());
        AssertInactive(state.RefreshData());
        state.SearchQuery = "Late query";
        state.SelectedSource = TypeAllocationDataSource.HeapSnapshot;
        state.SetPage(99);

        state.LiveSnapshot.Should().BeSameAs(live);
        state.LiveCapturedAt.Should().Be(capture);
        state.SearchQuery.Should().BeEmpty();
        state.SelectedSource.Should().Be(TypeAllocationDataSource.AllocationTracking);
        notifications.Should().Be(0);
        boundary.ReadCount.Should().Be(2);
        boundary.SubscriptionCount.Should().Be(1);
        scope.Resolve<TypeAllocationFacade>().IsCollecting.Should().BeFalse();
        AssertSuccess(scope.Resolve<TypeAllocationFacade>().GetCurrentSnapshot());
    }

    [Fact]
    public async Task TakeHeapSnapshotAsync_WhenHeld_ShouldRejectOtherAdmissionsAndPublishOnlyItsCompletion()
    {
        RequireHeapPageAdmission();
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.HeapResult = Result.Success(Registry(TypeAllocationDataSource.HeapSnapshot));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        using var gate = boundary.HoldNextHeap();
        var startedAt = clock.GetUtcNow();
        var action = state.TakeHeapSnapshotAsync();
        try
        {
            await AwaitBounded(gate.Entered);
            state.IsLoading.Should().BeTrue();
            state.IsSnapshotting.Should().BeTrue();
            AssertInactive(state.RefreshData());
            AssertInactive(await state.TakeHeapSnapshotAsync());
            AssertInactive(state.StartCollection());
            AssertInactive(state.StopCollection());
            AssertInactive(state.ResetData());
            state.HeapSnapshot.Should().BeNull();
            state.LastAction.Should().BeNull();
            boundary.ReadCount.Should().Be(1);
            boundary.HeapCaptureCount.Should().Be(1);
            clock.Advance(TimeSpan.FromSeconds(4));
        }
        finally { gate.Release(); await AwaitBounded(action); }

        AssertSuccess(await action);
        var completedAction = state.LastAction!;
        completedAction.StartedAt.Should().Be(startedAt);
        completedAction.CompletedAt.Should().Be(clock.GetUtcNow());
        completedAction.IsSuccessful.Should().BeTrue();
        state.IsLoading.Should().BeFalse();
        state.IsSnapshotting.Should().BeFalse();
        state.HeapCapturedAt.Should().Be(clock.GetUtcNow());
        state.SelectedSource.Should().Be(TypeAllocationDataSource.HeapSnapshot);
    }

    [Fact]
    public async Task DisposeAsync_WhenHeapCompletionIsHeld_ShouldUnsubscribeDrainAndKeepLateResultInert()
    {
        RequireHeapPageAdmission();
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.HeapResult = Result.Success(Registry(TypeAllocationDataSource.HeapSnapshot));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        AssertSuccess(await state.TakeHeapSnapshotAsync());
        var live = state.LiveSnapshot;
        var heap = state.HeapSnapshot;
        var capturedAt = state.HeapCapturedAt;
        var lastAction = state.LastAction;
        var notifications = 0;
        state.StateChanged += () => notifications++;
        boundary.HeapResult = Result.Success(Registry(TypeAllocationDataSource.HeapSnapshot, byteOffset: 8000));
        using var gate = boundary.HoldNextHeap();
        var action = state.TakeHeapSnapshotAsync();
        Task? disposal = null;
        var retiredNotifications = 0;
        try
        {
            await AwaitBounded(gate.Entered);
            disposal = state.DisposeAsync().AsTask();
            disposal.IsCompleted.Should().BeFalse();
            state.DisposeAsync().AsTask().Should().BeSameAs(disposal);
            state.IsLoading.Should().BeFalse();
            state.IsSnapshotting.Should().BeFalse();
            boundary.UnsubscriptionCount.Should().Be(1);
            retiredNotifications = notifications;
            boundary.Publish(Registry(TypeAllocationDataSource.AllocationTracking, byteOffset: 9000));
            AssertInactive(state.RefreshData());
            AssertInactive(await state.TakeHeapSnapshotAsync());
            AssertInactive(state.ResetData());
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release();
            await AwaitBounded(action);
            await AwaitBounded(disposal ?? state.DisposeAsync().AsTask());
        }

        AssertInactive(await action);
        state.LiveSnapshot.Should().BeSameAs(live);
        state.HeapSnapshot.Should().BeSameAs(heap);
        state.HeapCapturedAt.Should().Be(capturedAt);
        state.LastAction.Should().BeSameAs(lastAction);
        state.SelectedSource.Should().Be(TypeAllocationDataSource.HeapSnapshot);
        notifications.Should().Be(retiredNotifications);
        boundary.HeapCaptureCount.Should().Be(2);
        AssertSuccess(scope.Resolve<TypeAllocationFacade>().GetCurrentSnapshot());
        scope.Resolve<TypeAllocationFacade>().IsCollecting.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WhenReadIsHeld_ShouldDrainItWithoutPublishingAfterRetirement()
    {
        var clock = new TriggerMemoryAnalysisTimeProvider();
        await using var application = await Start(clock);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var boundary = Boundary(scope);
        boundary.LiveResult = Result.Success(Registry(TypeAllocationDataSource.AllocationTracking));
        await using var state = Create(scope, clock, boundary);
        AssertSuccess(await state.InitializeAsync());
        var snapshot = state.LiveSnapshot;
        var capturedAt = state.LiveCapturedAt;
        using var gate = boundary.HoldNextRead();
        var read = Task.Run(state.RefreshData, TestContext.Current.CancellationToken);
        Task? disposal = null;
        try
        {
            await AwaitBounded(gate.Entered);
            disposal = state.DisposeAsync().AsTask();
            disposal.IsCompleted.Should().BeFalse();
            AssertInactive(state.RefreshData());
            boundary.UnsubscriptionCount.Should().Be(1);
        }
        finally
        {
            gate.Release();
            await AwaitBounded(read);
            await AwaitBounded(disposal ?? state.DisposeAsync().AsTask());
        }
        AssertInactive(await read);
        state.LiveSnapshot.Should().BeSameAs(snapshot);
        state.LiveCapturedAt.Should().Be(capturedAt);
        state.IsLoading.Should().BeFalse();
    }

    private static readonly DateTime RegistryTimestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static TypeAllocationSnapshot Registry(TypeAllocationDataSource source, long byteOffset = 0)
    {
        var rows = Enumerable.Range(1, 13).Select(index => new TypeAllocationInfo
        {
            TypeName = $"Fixture.Domain{index:D2}.Widget",
            AllocationCount = index * 10,
            TotalBytes = (14 - index) * 100 + byteOffset,
            LastSeenUtc = RegistryTimestamp.AddTicks(index)
        }).ToArray();
        var totalCount = rows.Sum(row => row.AllocationCount) + 500;
        var totalBytes = rows.Sum(row => row.TotalBytes) + 500;
        foreach (var row in rows)
        {
            row.AllocationPercentage = 100d * row.AllocationCount / totalCount;
            row.BytesPercentage = 100d * row.TotalBytes / totalBytes;
        }
        return new()
        {
            DataSource = source,
            Timestamp = RegistryTimestamp,
            Duration = TimeSpan.FromTicks(12345),
            Types = rows,
            TotalAllocationCount = totalCount,
            TotalBytes = totalBytes,
            DroppedEventCount = 3,
            SamplingMode = source == TypeAllocationDataSource.HeapSnapshot ? AllocationSamplingMode.Disabled : AllocationSamplingMode.High
        };
    }

    private static string[] Identities() => Enumerable.Range(1, 13).Select(index => $"Fixture.Domain{index:D2}.Widget").ToArray();
    private static TypeAllocationBoundaryControl Boundary(MonicaTestScope scope) => new(scope.Resolve<TypeAllocationFacade>());
    private static TypeAllocationPanelState Create(MonicaTestScope scope, TimeProvider clock, TypeAllocationBoundaryControl boundary) =>
        new(scope.Resolve<TypeAllocationFacade>(), clock, boundary.ReadLive, boundary.CaptureHeapAsync, boundary.Subscribe);
    private static Task<MonicaTestApplication> Start(TimeProvider clock) =>
        new MemoryAnalysisTestApplicationFactory(0, sampleIntervalMs: int.MaxValue, enableAllocation: true)
            .CreateAsync(scenario => scenario.With<TimeProvider>(clock), TestContext.Current.CancellationToken);
    private static void RequireHeapPageAdmission() => Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 3),
        "The production page admits heap capture only on Windows with the PSS capability. The controlled boundary never invokes the native provider.");
    private static Task AwaitBounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private static void AssertSuccess(IResultEnvelope result) => result.IsSuccess().Should().BeTrue($"status={result.Status}, message={result.Message}");
    private static void AssertInactive(IResultEnvelope result)
    {
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Message.Should().BeEmpty();
    }
}
