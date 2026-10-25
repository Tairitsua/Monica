using Monica.Configuration.Models;
using Monica.Configuration.UI.State;
using Monica.Core.Results;

namespace Test.Monica.Configuration.UI.State;

public sealed class ConfigurationSourcesPageStateTests
{
    [Fact]
    public async Task Load_WhenInventoryIsEmpty_ShouldAcceptEmptyEvidenceWithItsObservationTime()
    {
        var clock = new FixedTimeProvider(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var state = new ConfigurationSourcesPageState(
            () => Task.FromResult(Inventories()), "unavailable", clock);

        await ObserveAsync(state.LoadAsync());

        Assert.False(state.Loading);
        Assert.True(state.HasAcceptedEvidence);
        Assert.True(state.SourceInventories.HasLoaded);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationSourceInventory>>(
            state.SourceInventories.Value));
        Assert.Equal(clock.UtcNow, state.SourceInventories.ObservedAtUtc);
        Assert.Null(state.SourceInventories.Error);
    }

    [Fact]
    public async Task Refresh_WhenNewestReadFails_ShouldRetainPendingEvidenceThenInvalidateItAndAllowRecovery()
    {
        var pending = NewCompletion<Res<IReadOnlyList<ConfigurationSourceInventory>>>();
        var calls = 0;
        var clock = new FixedTimeProvider(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var state = new ConfigurationSourcesPageState(() => (++calls) switch
        {
            1 => Task.FromResult(Inventories(Inventory("accepted"))),
            2 => pending.Task,
            _ => Task.FromResult(Inventories(Inventory("recovered")))
        }, "unavailable", clock);
        await ObserveAsync(state.LoadAsync());
        var accepted = state.SourceInventories.Value;
        var observed = state.SourceInventories.ObservedAtUtc;

        var refresh = state.LoadAsync();

        Assert.True(state.Loading);
        Assert.True(state.HasAcceptedEvidence);
        Assert.Same(accepted, state.SourceInventories.Value);
        Assert.Equal(observed, state.SourceInventories.ObservedAtUtc);
        pending.SetResult(Res.Fail("inventory unavailable"));
        await ObserveAsync(refresh);

        Assert.False(state.Loading);
        Assert.False(state.HasAcceptedEvidence);
        Assert.False(state.SourceInventories.HasLoaded);
        Assert.Null(state.SourceInventories.Value);
        Assert.Null(state.SourceInventories.ObservedAtUtc);
        Assert.Equal("inventory unavailable", state.SourceInventories.Error);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await ObserveAsync(state.LoadAsync());

        Assert.True(state.HasAcceptedEvidence);
        Assert.Equal("recovered", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationSourceInventory>>(
            state.SourceInventories.Value)).Source.SourceKey);
        Assert.Equal(clock.UtcNow, state.SourceInventories.ObservedAtUtc);
        Assert.Null(state.SourceInventories.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refresh_WhenOlderReadCompletesLate_ShouldPreserveNewestEvidenceOrError(bool olderFails, bool newerFails)
    {
        var pending = NewCompletion<Res<IReadOnlyList<ConfigurationSourceInventory>>>();
        var calls = 0;
        await using var state = new ConfigurationSourcesPageState(
            () => ++calls == 1 ? pending.Task
                : Task.FromResult<Res<IReadOnlyList<ConfigurationSourceInventory>>>(
                    newerFails ? Res.Fail("new failure") : Inventories(Inventory("new"))),
            "unavailable");
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());

        pending.SetResult(olderFails ? Res.Fail("old failure") : Inventories(Inventory("old")));
        await ObserveAsync(older);

        Assert.False(state.Loading);
        Assert.Equal(!newerFails, state.HasAcceptedEvidence);
        if (newerFails)
        {
            Assert.Equal("new failure", state.SourceInventories.Error);
            Assert.Null(state.SourceInventories.Value);
            Assert.Null(state.SourceInventories.ObservedAtUtc);
        }
        else
        {
            Assert.Equal("new", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationSourceInventory>>(
                state.SourceInventories.Value)).Source.SourceKey);
            Assert.Null(state.SourceInventories.Error);
        }
    }

    [Fact]
    public async Task Load_ShouldCopyInventoryAndItemCollectionsBeforeAcceptingThem()
    {
        var items = new List<ConfigurationSourceInventoryItem>
        {
            new() { ConfigurationPath = "Example:Value", RelativeConfigurationPath = "Value", DisplayValue = "accepted" }
        };
        var inventories = new List<ConfigurationSourceInventory> { Inventory("accepted") with { Items = items } };
        await using var state = new ConfigurationSourcesPageState(
            () => Task.FromResult(Res.Ok<IReadOnlyList<ConfigurationSourceInventory>>(inventories)), "unavailable");

        await ObserveAsync(state.LoadAsync());
        items.Clear();
        inventories.Clear();

        var accepted = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationSourceInventory>>(
            state.SourceInventories.Value));
        Assert.Equal("accepted", accepted.Source.SourceKey);
        Assert.Equal("accepted", Assert.Single(accepted.Items).DisplayValue);
    }

    [Fact]
    public async Task Dispose_ShouldDrainActualTokenlessReadAndRejectNewReadsWithoutAdoptingLateEvidence()
    {
        var pending = NewCompletion<Res<IReadOnlyList<ConfigurationSourceInventory>>>();
        var calls = 0;
        var state = new ConfigurationSourcesPageState(() => { calls++; return pending.Task; }, "unavailable");
        var load = state.LoadAsync();

        var first = state.DisposeAsync().AsTask();
        var repeated = state.DisposeAsync().AsTask();

        Assert.Same(first, repeated);
        Assert.False(first.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(state.LoadAsync()));
        Assert.Equal(1, calls);

        pending.SetResult(Inventories(Inventory("late")));
        await ObserveAsync(load);
        await ObserveAsync(first);

        Assert.False(state.HasAcceptedEvidence);
        Assert.Null(state.SourceInventories.Value);
        Assert.Null(state.SourceInventories.ObservedAtUtc);
        Assert.Null(state.SourceInventories.Error);
    }

    private static ConfigurationSourceInventory Inventory(string key) => new()
    {
        Source = new() { SourceKey = key, DisplayName = key, ProviderType = "ReviewProvider" }
    };

    private static Res<IReadOnlyList<ConfigurationSourceInventory>> Inventories(params ConfigurationSourceInventory[] values)
        => Res.Ok<IReadOnlyList<ConfigurationSourceInventory>>(values);

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
