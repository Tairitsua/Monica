using Monica.Configuration.Models;
using Monica.Configuration.UI.State;
using Monica.Core.Results;

namespace Test.Monica.Configuration.UI.State;

public sealed class ConfigurationStoragePageStateTests
{
    [Fact]
    public async Task Load_WhenOverviewFails_ShouldAcceptEmptyOperationEvidenceIndependently()
    {
        await using var state = new ConfigurationStoragePageState(
            () => Task.FromException<Res<ConfigurationStorageOverview>>(new InvalidOperationException("overview unavailable")),
            () => Task.FromResult(States()), "unavailable");

        await ObserveAsync(state.LoadAsync());

        Assert.False(state.Loading);
        Assert.False(state.Overview.HasLoaded);
        Assert.Equal("overview unavailable", state.Overview.Error);
        Assert.Null(state.Overview.ObservedAtUtc);
        Assert.True(state.StoreStates.HasLoaded);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationStoreState>>(state.StoreStates.Value));
        Assert.NotNull(state.StoreStates.ObservedAtUtc);
        Assert.True(state.HasAcceptedEvidence);
    }

    [Fact]
    public async Task Load_WhenOperationsFail_ShouldAcceptStorageCapabilitiesIndependently()
    {
        await using var state = new ConfigurationStoragePageState(
            () => Task.FromResult(Res.Ok(Overview("available"))),
            () => Task.FromResult<Res<IReadOnlyList<ConfigurationStoreState>>>(Res.Fail("operations unavailable")),
            "unavailable");

        await ObserveAsync(state.LoadAsync());

        Assert.False(state.Loading);
        Assert.True(state.HasAcceptedEvidence);
        Assert.Equal("available", Assert.IsType<ConfigurationStorageOverview>(state.Overview.Value)
            .EffectiveValueStore.StoreKey);
        Assert.NotNull(state.Overview.ObservedAtUtc);
        Assert.False(state.StoreStates.HasLoaded);
        Assert.Null(state.StoreStates.Value);
        Assert.Null(state.StoreStates.ObservedAtUtc);
        Assert.Equal("operations unavailable", state.StoreStates.Error);
    }

    [Fact]
    public async Task Refresh_ShouldRetainAcceptedEvidenceUntilEachNewestOutcomeIsAccepted()
    {
        var pendingOverview = NewCompletion<Res<ConfigurationStorageOverview>>();
        var pendingStates = NewCompletion<Res<IReadOnlyList<ConfigurationStoreState>>>();
        var overviewCalls = 0;
        var stateCalls = 0;
        var clock = new FixedTimeProvider(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var state = new ConfigurationStoragePageState(
            () => ++overviewCalls == 1 ? Task.FromResult(Res.Ok(Overview("original"))) : pendingOverview.Task,
            () => ++stateCalls == 1 ? Task.FromResult(States(new ConfigurationStoreState { StoreKey = "original" })) : pendingStates.Task,
            "unavailable", clock);
        await ObserveAsync(state.LoadAsync());
        var overview = state.Overview.Value;
        var states = state.StoreStates.Value;
        var observed = state.Overview.ObservedAtUtc;
        var refresh = state.LoadAsync();

        Assert.True(state.Loading);
        Assert.Same(overview, state.Overview.Value);
        Assert.Same(states, state.StoreStates.Value);
        Assert.Equal(observed, state.Overview.ObservedAtUtc);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        pendingOverview.SetResult(Res.Fail("new overview failed"));
        pendingStates.SetResult(States());
        await ObserveAsync(refresh);

        Assert.False(state.Overview.HasLoaded);
        Assert.Null(state.Overview.Value);
        Assert.Null(state.Overview.ObservedAtUtc);
        Assert.Equal("new overview failed", state.Overview.Error);
        Assert.True(state.StoreStates.HasLoaded);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationStoreState>>(state.StoreStates.Value));
        Assert.Equal(clock.UtcNow, state.StoreStates.ObservedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_WhenOlderReadCompletesLate_ShouldNotReplaceNewerEvidenceOrError(bool olderFails)
    {
        var pending = NewCompletion<Res<ConfigurationStorageOverview>>();
        var calls = 0;
        await using var state = new ConfigurationStoragePageState(
            () => ++calls == 1 ? pending.Task : Task.FromResult(Res.Ok(Overview("new"))),
            () => Task.FromResult(States()), "unavailable");
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());
        pending.SetResult(olderFails ? Res.Fail("old failure") : Res.Ok(Overview("old")));
        await ObserveAsync(older);

        Assert.Equal("new", Assert.IsType<ConfigurationStorageOverview>(state.Overview.Value).EffectiveValueStore.StoreKey);
        Assert.Null(state.Overview.Error);
        Assert.True(state.Overview.HasLoaded);
    }

    [Fact]
    public async Task Refresh_WhenProducerReentersSynchronously_ShouldReserveEveryReadBeforeCallingAnyProducer()
    {
        ConfigurationStoragePageState? state = null;
        Task? nestedRefresh = null;
        var overviewCalls = 0;
        var stateCalls = 0;
        state = new ConfigurationStoragePageState(() =>
        {
            if (++overviewCalls == 1)
            {
                nestedRefresh = state!.LoadAsync();
                return Task.FromResult(Res.Ok(Overview("old")));
            }
            return Task.FromResult(Res.Ok(Overview("new")));
        }, () => Task.FromResult(States(new ConfigurationStoreState { StoreKey = ++stateCalls == 1 ? "new" : "old" })), "unavailable");
        await using var owner = state;

        await ObserveAsync(state.LoadAsync());
        await ObserveAsync(Assert.IsAssignableFrom<Task>(nestedRefresh));

        Assert.Equal("new", Assert.IsType<ConfigurationStorageOverview>(state.Overview.Value).EffectiveValueStore.StoreKey);
        Assert.Equal("new", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationStoreState>>(
            state.StoreStates.Value)).StoreKey);
    }

    [Fact]
    public async Task Load_ShouldCopyMutableOperationCollectionsBeforeAcceptingThem()
    {
        var states = new List<ConfigurationStoreState> { new() { StoreKey = "accepted" } };
        await using var state = new ConfigurationStoragePageState(() => Task.FromResult(Res.Ok(Overview("accepted"))),
            () => Task.FromResult(Res.Ok<IReadOnlyList<ConfigurationStoreState>>(states)), "unavailable");
        await ObserveAsync(state.LoadAsync());

        states.Clear();

        Assert.Equal("accepted", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationStoreState>>(
            state.StoreStates.Value)).StoreKey);
    }

    [Fact]
    public async Task Dispose_ShouldJoinActualTokenlessReadsAndShareCompletionWithoutAdoptingLateResults()
    {
        var overview = NewCompletion<Res<ConfigurationStorageOverview>>();
        var states = NewCompletion<Res<IReadOnlyList<ConfigurationStoreState>>>();
        var calls = 0;
        var state = new ConfigurationStoragePageState(() => { calls++; return overview.Task; },
            () => states.Task, "unavailable");
        var load = state.LoadAsync();
        var first = state.DisposeAsync().AsTask();
        var repeated = state.DisposeAsync().AsTask();
        Assert.Same(first, repeated);
        Assert.False(first.IsCompleted);
        overview.SetResult(Res.Ok(Overview("late")));
        Assert.False(first.IsCompleted);
        states.SetResult(Res.Fail("late failure"));
        await ObserveAsync(load);
        await ObserveAsync(first);

        Assert.False(state.HasAcceptedEvidence);
        Assert.Null(state.Overview.Value);
        Assert.Null(state.StoreStates.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(state.LoadAsync()));
        Assert.Equal(1, calls);
    }

    private static ConfigurationStorageOverview Overview(string key)
    {
        var descriptor = new ConfigurationStoreDescriptor { StoreKey = key, DisplayName = key };
        return new() { EffectiveValueStore = descriptor, HistoryStore = descriptor, MetadataStore = descriptor };
    }

    private static Res<IReadOnlyList<ConfigurationStoreState>> States(params ConfigurationStoreState[] values) => Res.Ok<IReadOnlyList<ConfigurationStoreState>>(values);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
