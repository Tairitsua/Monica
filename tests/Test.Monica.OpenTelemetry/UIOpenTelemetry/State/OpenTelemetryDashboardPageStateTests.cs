using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.UI.UIOpenTelemetry.State;
using Test.Monica.OpenTelemetry.Support;
using Test.Monica.OpenTelemetry.UIOpenTelemetry.Support;
using Xunit;

namespace Test.Monica.OpenTelemetry.UIOpenTelemetry.State;

public sealed class OpenTelemetryDashboardPageStateTests
{
    [Fact]
    public async Task Refresh_WhenRegistryIsEmpty_ShouldAdoptSuccessAndSelectTheFirstLaterPublishedInstrument()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var clock = new TriggerOpenTelemetryTimeProvider();
        var facade = scope.Resolve<OpenTelemetryFacade>();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Read failed.", clock, facade.GetSnapshotAsync);
        AssertOk(await state.RefreshAsync());
        state.HasSnapshot.Should().BeTrue();
        state.Snapshot!.Instruments.Should().BeEmpty();
        state.SelectedInstrument.Should().BeNull();
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());

        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        var counter = meter.CreateCounter<long>("work.completed");
        counter.Add(3);
        AssertOk(await state.RefreshAsync());
        state.SelectedInstrument!.Name.Should().Be(counter.Name);
        state.SelectedSeries!.CurrentValue.Should().Be(3);
        var firstId = state.SelectedInstrument.InstrumentId;
        state.SelectInstrument(null);
        counter.Add(2);
        AssertOk(await state.RefreshAsync());
        state.SelectedInstrument.Should().BeNull();
        state.Snapshot.Instruments.Single().InstrumentId.Should().Be(firstId);
    }

    [Fact]
    public async Task Refresh_WhenDuplicateNamesAndTagSetsExist_ShouldRetainOnlyTheExactSelectedIdentity()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var firstMeter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName) { Version = "1" });
        using var secondMeter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName) { Version = "2" });
        var first = firstMeter.CreateCounter<long>("same.name");
        var second = secondMeter.CreateCounter<long>("same.name");
        first.Add(5, new KeyValuePair<string, object?>("route", "first"));
        second.Add(8, new KeyValuePair<string, object?>("route", "first"));
        second.Add(13, new KeyValuePair<string, object?>("route", "second"));
        var facade = scope.Resolve<OpenTelemetryFacade>();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Read failed.");
        AssertOk(await state.RefreshAsync());
        var instrument = state.Snapshot!.Instruments.Single(item => item.MeterVersion == "2");
        state.SelectInstrument(instrument);
        state.SelectSeries(instrument.TagSets.Single(series => series.Tags["route"] == "second"));
        var id = state.SelectedInstrument!.InstrumentId;
        var staleSeries = state.SelectedSeries!;
        var frozen = state.InspectSample(staleSeries, 0)!;
        second.Add(7, new KeyValuePair<string, object?>("route", "second"));
        AssertOk(await state.RefreshAsync());
        state.SelectedInstrument!.InstrumentId.Should().Be(id);
        state.SelectedSeries!.CurrentValue.Should().Be(20);
        state.InspectSample(staleSeries, 0).Should().BeNull();
        frozen.Point.Value.Should().Be(13);
        frozen.Series.Samples.Should().HaveCount(1);
        state.SelectSeries(staleSeries);
        state.SelectedSeries.CurrentValue.Should().Be(20);
        state.SelectInstrument(instrument);
        state.SelectedInstrument.Should().NotBeSameAs(instrument);
    }

    [Fact]
    public async Task Filters_ShouldUseExactMeterAndCaseInsensitiveNameWithoutAutomaticallySubstitutingSelection()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        using var other = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.OtherMeterName));
        meter.CreateCounter<long>("Work.Completed").Add(2);
        other.CreateCounter<long>("Work.Pending").Add(3);
        await using var state = scope.Resolve<OpenTelemetryDashboardPageStateFactory>().Create();
        AssertOk(await state.RefreshAsync());
        state.GetMeterNames().Should().HaveCount(2);
        state.MeterPrefixFilter = factory.MeterName;
        state.FilteredInstruments().Should().ContainSingle().Which.Name.Should().Be("Work.Completed");
        state.InstrumentNameFilter = "pEnDiNg";
        state.FilteredInstruments().Should().BeEmpty();
        state.SelectedInstrument.Should().BeNull();
        state.GetMeterNames().Should().HaveCount(2);
        state.MeterPrefixFilter = "";
        state.FilteredInstruments().Should().ContainSingle().Which.Name.Should().Be("Work.Pending");
        AssertOk(await state.RefreshAsync());
        state.SelectedInstrument.Should().BeNull();
        state.ExportJson().Should().Contain("Work.Completed").And.Contain("Work.Pending");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Actual source unavailable.")]
    public async Task Refresh_WhenReadFails_ShouldRetainGraphSelectionBothTimesAndOriginalFailureThenRecover(string message)
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        var counter = meter.CreateCounter<long>("work"); counter.Add(4);
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var control = new OpenTelemetryReadControl(facade);
        var clock = new TriggerOpenTelemetryTimeProvider();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Fallback.", clock, control.ReadAsync);
        AssertOk(await state.RefreshAsync());
        var view = state.Snapshot; var instrument = state.SelectedInstrument; var series = state.SelectedSeries; var adopted = state.SnapshotCapturedAt;
        control.Fail(message); counter.Add(5); clock.Advance(TimeSpan.FromSeconds(2));
        var failure = await state.RefreshAsync();
        failure.Status.Should().Be(ResultStatus.ServiceUnavailable);
        failure.Message.Should().Be(message);
        state.Error.Should().Be(message);
        state.Snapshot.Should().BeSameAs(view);
        state.SelectedInstrument.Should().BeSameAs(instrument);
        state.SelectedSeries.Should().BeSameAs(series);
        state.SnapshotCapturedAt.Should().Be(adopted);
        state.IsLoading.Should().BeFalse();
        control.Fail(null);
        AssertOk(await state.RefreshAsync());
        state.Error.Should().BeNull();
        state.SelectedSeries!.CurrentValue.Should().Be(9);
        state.SnapshotCapturedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task Refresh_WhenClockThrows_ShouldRetainTheWholePreviousAdoptionAtomically()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        var gauge = meter.CreateGauge<double>("level"); gauge.Record(2);
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var clock = new TriggerOpenTelemetryTimeProvider();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Fallback.", clock, facade.GetSnapshotAsync);
        AssertOk(await state.RefreshAsync());
        var view = state.Snapshot; var instrument = state.SelectedInstrument; var series = state.SelectedSeries; var adopted = state.SnapshotCapturedAt;
        gauge.Record(3); clock.ThrowOnRead = true;
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.InternalError);
        state.Error.Should().Be("Clock read unavailable.");
        state.Snapshot.Should().BeSameAs(view); state.SelectedInstrument.Should().BeSameAs(instrument); state.SelectedSeries.Should().BeSameAs(series);
        state.SnapshotCapturedAt.Should().Be(adopted); state.HasSnapshot.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_WhenBusy_ShouldRejectOverlapAndRetirementShouldDrainAdmittedReadWithoutPublication()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        meter.CreateCounter<long>("work").Add(1);
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var control = new OpenTelemetryReadControl(facade);
        await using var state = new OpenTelemetryDashboardPageState(facade, "Fallback.", TimeProvider.System, control.ReadAsync);
        AssertOk(await state.RefreshAsync());
        var view = state.Snapshot;
        var gate = control.HoldNext();
        var read = state.RefreshAsync(); await gate.Entered;
        try
        {
            (await state.RefreshAsync()).Status.Should().Be(ResultStatus.Conflict);
            control.Reads.Should().Be(2);
            var disposal = state.DisposeAsync().AsTask();
            state.DisposeAsync().AsTask().Should().BeSameAs(disposal);
            disposal.IsCompleted.Should().BeFalse();
            state.IsLoading.Should().BeFalse();
            (await state.RefreshAsync()).Status.Should().Be(ResultStatus.Conflict);
            gate.Release();
            (await read).Status.Should().Be(ResultStatus.Conflict);
            await disposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            state.Snapshot.Should().BeSameAs(view);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task Pause_ShouldDrainHeldTimerReadAndKeepHostCollectionIndependent()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var meter = scope.Resolve<IMeterFactory>().Create(new MeterOptions(factory.MeterName));
        var counter = meter.CreateCounter<long>("work"); counter.Add(1);
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var control = new OpenTelemetryReadControl(facade);
        var clock = new TriggerOpenTelemetryTimeProvider();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Fallback.", clock, control.ReadAsync);
        AssertOk(await state.LoadAsync());
        clock.ActiveTimers.Should().Be(1);
        var view = state.Snapshot; var adopted = state.SnapshotCapturedAt;
        counter.Add(2);
        var gate = control.HoldNext(); clock.Advance(TimeSpan.FromSeconds(5)); clock.Tick().Should().Be(1); await gate.Entered;
        try
        {
            var pause = state.SetAutoRefreshIntervalAsync(null);
            pause.IsCompleted.Should().BeFalse();
            gate.Release(); await pause.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            state.Snapshot.Should().BeSameAs(view); state.SnapshotCapturedAt.Should().Be(adopted);
            clock.ActiveTimers.Should().Be(0); clock.Tick().Should().Be(0);
            counter.Add(4);
            var actual = await facade.GetSnapshotAsync();
            actual.Data!.Instruments.Single().TagSets.Single().CurrentValue.Should().Be(7);
            AssertOk(await state.RefreshAsync());
            state.SelectedSeries!.CurrentValue.Should().Be(7);
            state.AutoRefreshInterval.Should().BeNull();
            await state.SetAutoRefreshIntervalAsync(TimeSpan.FromSeconds(1));
            await state.SetAutoRefreshIntervalAsync(TimeSpan.FromSeconds(5));
            clock.MaximumActiveTimers.Should().Be(1);
            await state.DisposeAsync(); clock.ActiveTimers.Should().Be(0);
            await state.SetAutoRefreshIntervalAsync(TimeSpan.FromSeconds(1)); clock.ActiveTimers.Should().Be(0);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task Notifications_WhenHandlerThrows_ShouldIsolateFailureAndRespectReentrantRetirement()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<OpenTelemetryFacade>();
        await using var state = new OpenTelemetryDashboardPageState(facade, "Fallback.");
        state.StateChanged += () => throw new InvalidOperationException("Render scheduling failed.");
        AssertOk(await state.RefreshAsync());
        state.HasSnapshot.Should().BeTrue(); state.Error.Should().BeNull();
        state.NotificationError!.Message.Should().Be("Render scheduling failed.");
        var laterNotifications = 0;
        state.StateChanged += () => _ = state.DisposeAsync();
        state.StateChanged += () => laterNotifications++;
        (await state.RefreshAsync()).Status.Should().Be(ResultStatus.Conflict);
        await state.DisposeAsync(); laterNotifications.Should().Be(0);
        state.InstrumentNameFilter = "late";
        laterNotifications.Should().Be(0);
    }

    private static void AssertOk(Result result) { result.Status.Should().Be(ResultStatus.Ok); result.Message.Should().BeNullOrEmpty(); }
}
