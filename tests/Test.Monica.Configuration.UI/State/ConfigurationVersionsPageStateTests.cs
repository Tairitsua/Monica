using Microsoft.Extensions.Localization;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Localization;
using Monica.Configuration.UI.State;
using Monica.Core.Results;
using NSubstitute;

namespace Test.Monica.Configuration.UI.State;

public sealed class ConfigurationVersionsPageStateTests
{
    [Fact]
    public async Task Selection_WhenCachedPairSupersedesPendingRead_ShouldKeepCachedPair()
    {
        var pending = NewCompletion<Result<ConfigurationUnifiedVersionComparison>>();
        var calls = new List<(long, long)>();
        await using var state = State((origin, target) =>
        {
            calls.Add((origin, target));
            return origin == 1 ? pending.Task : Task.FromResult(Result.Success(Comparison(origin, target)));
        });
        await ObserveAsync(state.LoadAsync());
        var cached = Assert.IsType<ConfigurationUnifiedVersionComparison>(state.SelectedComparison);
        var older = state.SelectComparisonAsync(1, 3);
        Assert.True(state.ComparisonLoading);

        await ObserveAsync(state.SelectComparisonAsync(2, 3));
        Assert.Same(cached, state.SelectedComparison);
        Assert.False(state.ComparisonLoading);
        pending.SetResult(Result.Success(Comparison(1, 3)));
        await ObserveAsync(older);
        Assert.Same(cached, state.SelectedComparison);
        Assert.Equal(2, state.OriginVersion);
        Assert.Equal(3, state.TargetVersion);
        Assert.Equal(new[] { (2L, 3L), (1L, 3L) }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_WhenClearedOrRejected_ShouldRetirePendingComparison(bool invalidPair)
    {
        var pending = NewCompletion<Result<ConfigurationUnifiedVersionComparison>>();
        await using var state = State((origin, target) => origin == 1
            ? pending.Task : Task.FromResult(Result.Success(Comparison(origin, target))));
        await ObserveAsync(state.LoadAsync());
        var older = state.SelectComparisonAsync(1, 3);
        await ObserveAsync(invalidPair ? state.SelectComparisonAsync(99, 3)
            : state.CompareWithPreviousAsync(new() { Version = 1 }));
        pending.SetResult(Result.Success(Comparison(1, 3)));
        await ObserveAsync(older);
        Assert.Null(state.SelectedComparison);
        Assert.False(state.ComparisonLoading);
        if (invalidPair) Assert.NotNull(state.ComparisonError);
        else Assert.Null(state.OriginVersion);
    }

    [Fact]
    public async Task LatestSelection_ShouldCompareAnEqualPairRatherThanClearSelection()
    {
        await using var state = State();
        await ObserveAsync(state.LoadAsync());
        await ObserveAsync(state.SelectVersionAsync(3));
        var comparison = Assert.IsType<ConfigurationUnifiedVersionComparison>(state.SelectedComparison);
        Assert.Equal(3, comparison.Origin.Summary.Version);
        Assert.Equal(3, comparison.Target.Summary.Version);
        Assert.Empty(comparison.Changes);
    }

    [Fact]
    public async Task Reload_ShouldPreserveExplicitReversedPair()
    {
        await using var state = State();
        await ObserveAsync(state.LoadAsync());
        await ObserveAsync(state.SelectComparisonAsync(3, 1));
        await ObserveAsync(state.LoadAsync());
        Assert.Equal(3, state.OriginVersion);
        Assert.Equal(1, state.TargetVersion);
        var comparison = Assert.IsType<ConfigurationUnifiedVersionComparison>(state.SelectedComparison);
        Assert.Equal(3, comparison.Origin.Summary.Version);
        Assert.Equal(1, comparison.Target.Summary.Version);
    }

    [Fact]
    public async Task Reload_WhenNewerEmptyLoadCompletesFirst_ShouldRejectOlderList()
    {
        var oldList = NewCompletion<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>();
        var calls = 0;
        await using var state = State(load: () => ++calls == 1 ? oldList.Task : Task.FromResult(Versions([])));
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());
        oldList.SetResult(Versions([new() { Version = 99 }]));
        await ObserveAsync(older);
        Assert.True(state.HasLoadedVersions);
        Assert.False(state.HasLoadFailure);
        Assert.False(state.HasVersions);
        Assert.Empty(state.Versions);
        Assert.Null(state.SelectedComparison);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("empty")]
    [InlineData("failure")]
    public async Task Reload_WhenAcceptedListExists_ShouldRetainItUntilNewestOutcomeIsAccepted(string outcome)
    {
        var pending = NewCompletion<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>();
        var calls = 0;
        await using var state = State(load: () => ++calls == 1
            ? Task.FromResult(Versions([new() { Version = 3 }, new() { Version = 2 }])) : pending.Task);
        await ObserveAsync(state.LoadAsync());
        var accepted = state.Versions;

        var refresh = state.LoadAsync();

        Assert.True(state.Loading);
        Assert.True(state.HasLoadedVersions);
        Assert.Same(accepted, state.Versions);
        Assert.Equal(new long[] { 3, 2 }, state.Versions.Select(version => version.Version));
        pending.SetResult(outcome switch
        {
            "failure" => Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("refresh unavailable"),
            "empty" => Versions([]),
            _ => Versions([new() { Version = 4 }])
        });
        await ObserveAsync(refresh);

        Assert.False(state.Loading);
        Assert.Equal(outcome != "failure", state.HasLoadedVersions);
        Assert.Equal(outcome == "failure", state.HasLoadFailure);
        Assert.Equal(outcome == "failure" ? "refresh unavailable" : null, state.ErrorText);
        if (outcome == "success")
        {
            Assert.Equal(4, Assert.Single(state.Versions).Version);
            Assert.Equal(4, state.OriginVersion);
            Assert.Equal(4, state.TargetVersion);
        }
        else
        {
            Assert.Empty(state.Versions);
            Assert.Null(state.SelectedComparison);
            Assert.Null(state.OriginVersion);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_WhenNewestRefreshFails_ShouldNotRestoreEvidenceFromLateOlderOutcome(bool olderFails)
    {
        var pending = NewCompletion<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>();
        var calls = 0;
        var reports = new List<string>();
        await using var state = State(load: () => ++calls switch
        {
            1 => Task.FromResult(Versions([new() { Version = 3 }])),
            2 => pending.Task,
            _ => Task.FromResult<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>(Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("newest unavailable"))
        }, reports: reports);
        await ObserveAsync(state.LoadAsync());
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());
        pending.SetResult(olderFails ? Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("older unavailable") : Versions([new() { Version = 99 }]));
        await ObserveAsync(older);

        Assert.False(state.Loading);
        Assert.False(state.HasLoadedVersions);
        Assert.Empty(state.Versions);
        Assert.Equal("newest unavailable", state.ErrorText);
        Assert.Null(state.SelectedComparison);
        Assert.Single(reports);
    }

    [Fact]
    public async Task Reload_WhenNewestRefreshSucceeds_ShouldRejectLateOlderEmptyList()
    {
        var pending = NewCompletion<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>();
        var calls = 0;
        await using var state = State(load: () => ++calls switch
        {
            1 => Task.FromResult(Versions([new() { Version = 3 }])),
            2 => pending.Task,
            _ => Task.FromResult(Versions([new() { Version = 4 }]))
        });
        await ObserveAsync(state.LoadAsync());
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());
        var accepted = state.Versions;
        pending.SetResult(Versions([]));
        await ObserveAsync(older);

        Assert.True(state.HasLoadedVersions);
        Assert.False(state.HasLoadFailure);
        Assert.Same(accepted, state.Versions);
        Assert.Equal(4, Assert.Single(state.Versions).Version);
        Assert.Equal(4, state.OriginVersion);
        Assert.Equal(4, state.TargetVersion);
    }

    [Fact]
    public async Task Load_WhenUnavailableThenEmpty_ShouldKeepThoseEvidenceStatesDistinct()
    {
        var calls = 0;
        await using var state = State(load: () => Task.FromResult(++calls == 1
            ? Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("version store unavailable") : Versions([])));
        await ObserveAsync(state.LoadAsync());
        Assert.False(state.HasLoadedVersions);
        Assert.True(state.HasLoadFailure);
        Assert.Equal("version store unavailable", state.ErrorText);
        Assert.False(state.Loading);
        await ObserveAsync(state.LoadAsync());
        Assert.True(state.HasLoadedVersions);
        Assert.False(state.HasLoadFailure);
        Assert.Null(state.ErrorText);
        Assert.Empty(state.Versions);
    }

    [Fact]
    public async Task Comparison_WhenUnavailable_ShouldNotBeAnEmptySuccessfulComparison()
    {
        await using var state = State((_, _) => Task.FromResult<Result<ConfigurationUnifiedVersionComparison>>(Result.Failure<ConfigurationUnifiedVersionComparison>("snapshot deleted")));
        await ObserveAsync(state.LoadAsync());
        Assert.True(state.HasLoadedVersions);
        Assert.Null(state.SelectedComparison);
        Assert.Equal("snapshot deleted", state.ComparisonError);
        Assert.False(state.ComparisonLoading);
    }

    [Fact]
    public async Task Load_WhenSuccessfulResponseHasNoList_ShouldRemainUnavailable()
    {
        var missing = new Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>> { Status = ResultStatus.Ok };
        await using var state = State(load: () => Task.FromResult(missing));

        await ObserveAsync(state.LoadAsync());

        Assert.False(state.HasLoadedVersions);
        Assert.True(state.HasLoadFailure);
        Assert.Equal("Common:Errors:LoadFailed", state.ErrorText);
        Assert.Empty(state.Versions);
    }

    [Fact]
    public async Task Comparison_WhenSuccessfulResponseHasNoData_ShouldRemainUnavailable()
    {
        var missing = new Result<ConfigurationUnifiedVersionComparison> { Status = ResultStatus.Ok };
        await using var state = State((_, _) => Task.FromResult(missing));

        await ObserveAsync(state.LoadAsync());

        Assert.True(state.HasLoadedVersions);
        Assert.Null(state.SelectedComparison);
        Assert.Equal("Common:Errors:LoadFailed", state.ComparisonError);
        Assert.False(state.ComparisonLoading);
    }

    [Fact]
    public async Task Retirement_ShouldJoinActualTokenlessReadAndRejectLateAdoption()
    {
        var pending = NewCompletion<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>();
        var reports = new List<string>();
        var calls = 0;
        var state = State(load: () => { calls++; return pending.Task; }, reports: reports);
        var load = state.LoadAsync();
        var retirement = state.DisposeAsync().AsTask();
        Assert.Same(retirement, state.DisposeAsync().AsTask());
        Assert.False(retirement.IsCompleted);
        pending.SetResult(Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("late failure"));
        await ObserveAsync(load);
        await ObserveAsync(retirement);
        Assert.Empty(reports);
        Assert.False(state.HasLoadedVersions);
        Assert.Null(state.ErrorText);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(state.LoadAsync()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ErrorReporter_ShouldRunOutsideAdmissionGateAndPermitRetirement()
    {
        ConfigurationVersionsPageState? state = null;
        Task? retirement = null;
        state = State(load: () => Task.FromResult<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>(Result.Failure<IReadOnlyList<ConfigurationUnifiedVersionSummary>>("failed")),
            report: _ => retirement = state!.DisposeAsync().AsTask());
        await ObserveAsync(state.LoadAsync());
        Assert.NotNull(retirement);
        await ObserveAsync(retirement);
        Assert.True(state.IsDisposed);
    }

    private static ConfigurationVersionsPageState State(
        Func<long, long, Task<Result<ConfigurationUnifiedVersionComparison>>>? compare = null,
        Func<Task<Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>>? load = null,
        List<string>? reports = null, Action<string>? report = null)
    {
        var localizer = Substitute.For<IStringLocalizer<ConfigurationUIResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        return new(load ?? (() => Task.FromResult(Versions([new() { Version = 1 }, new() { Version = 3 }, new() { Version = 2 }]))),
            compare ?? ((origin, target) => Task.FromResult(Result.Success(Comparison(origin, target)))),
            report ?? (message => reports?.Add(message)), localizer);
    }

    private static Result<IReadOnlyList<ConfigurationUnifiedVersionSummary>> Versions(IReadOnlyList<ConfigurationUnifiedVersionSummary> versions) => Result.Success(versions);
    private static ConfigurationUnifiedVersionComparison Comparison(long origin, long target) => new()
    {
        Origin = new() { Summary = new() { Version = origin } },
        Target = new() { Summary = new() { Version = target } }
    };
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
