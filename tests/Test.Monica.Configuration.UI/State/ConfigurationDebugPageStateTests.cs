using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;

namespace Test.Monica.Configuration.UI.State;

public sealed class ConfigurationDebugPageStateTests
{
    [Fact]
    public async Task Refresh_ShouldRemaskAcceptedLinesAndNeverCarryRevealIntoReplacementData()
    {
        var pending = NewCompletion<Result<string>>();
        var calls = 0;
        await using var state = new ConfigurationDebugPageState(() => ++calls == 1
            ? Task.FromResult(Result.Success<string>("Credentials:Password=first-test-secret")) : pending.Task, "unavailable");
        await ObserveAsync(state.LoadAsync());
        var accepted = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationDebugLine>>(state.Read.Value));
        Assert.DoesNotContain("first-test-secret", accepted.SearchText);
        state.ToggleSensitiveLine(accepted.Index);
        Assert.Contains("first-test-secret", accepted.GetDisplayText(state.RevealedLines.Contains(accepted.Index)));
        var observed = state.Read.ObservedAtUtc;
        var refresh = state.LoadAsync();

        Assert.True(state.Read.HasLoaded);
        Assert.Equal(observed, state.Read.ObservedAtUtc);
        Assert.Empty(state.RevealedLines);
        state.ToggleSensitiveLine(accepted.Index);
        Assert.Empty(state.RevealedLines);
        Assert.DoesNotContain("first-test-secret", accepted.GetDisplayText(state.RevealedLines.Contains(accepted.Index)));
        pending.SetResult(Result.Success<string>("Credentials:Password=replacement-test-secret"));
        await ObserveAsync(refresh);

        var replacement = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationDebugLine>>(state.Read.Value));
        Assert.Empty(state.RevealedLines);
        Assert.DoesNotContain("replacement-test-secret", replacement.SearchText);
        Assert.DoesNotContain("replacement-test-secret", replacement.GetDisplayText(false));
        state.ToggleSensitiveLine(replacement.Index);
        Assert.Contains("replacement-test-secret", replacement.GetDisplayText(state.RevealedLines.Contains(replacement.Index)));
        state.ToggleSensitiveLine(replacement.Index);
        Assert.Empty(state.RevealedLines);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task Load_ShouldDistinguishUnavailablePayloadFromSuccessfulEmptyText(string outcome)
    {
        await using var state = new ConfigurationDebugPageState(() => Task.FromResult<Result<string>>(outcome switch
        {
            "failure" => Result.Failure<string>("debug unavailable"),
            "missing" => new Result<string> { Status = ResultStatus.Ok, Data = null },
            _ => Result.Success<string>(string.Empty)
        }), "unavailable");
        await ObserveAsync(state.LoadAsync());

        Assert.False(state.Read.Loading);
        Assert.Equal(outcome == "empty", state.Read.HasLoaded);
        if (outcome == "empty")
        {
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationDebugLine>>(state.Read.Value));
            Assert.NotNull(state.Read.ObservedAtUtc);
            Assert.Null(state.Read.Error);
        }
        else
        {
            Assert.Null(state.Read.Value);
            Assert.Null(state.Read.ObservedAtUtc);
            Assert.Equal(outcome == "failure" ? "debug unavailable" : "unavailable", state.Read.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_WhenOldReadCompletesLate_ShouldKeepNewerMaskedEvidence(bool olderFails)
    {
        var pending = NewCompletion<Result<string>>();
        var calls = 0;
        await using var state = new ConfigurationDebugPageState(() => ++calls == 1
            ? pending.Task : Task.FromResult(Result.Success<string>("Credentials:Password=new-test-secret")), "unavailable");
        var older = state.LoadAsync();
        await ObserveAsync(state.LoadAsync());
        pending.SetResult(olderFails ? Result.Failure<string>("old unavailable") : Result.Success<string>("Credentials:Password=old-test-secret"));
        await ObserveAsync(older);

        Assert.True(state.Read.HasLoaded);
        Assert.Null(state.Read.Error);
        var line = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ConfigurationDebugLine>>(state.Read.Value));
        Assert.Contains("new-test-secret", line.RawText);
        Assert.DoesNotContain("new-test-secret", line.SearchText);
        Assert.Empty(state.RevealedLines);
    }

    [Fact]
    public async Task Dispose_ShouldDrainActualDebugReadWithoutLateAdoptionOrReveal()
    {
        var pending = NewCompletion<Result<string>>();
        var state = new ConfigurationDebugPageState(() => pending.Task, "unavailable");
        var load = state.LoadAsync();
        var disposal = state.DisposeAsync().AsTask();
        Assert.Same(disposal, state.DisposeAsync().AsTask());
        Assert.False(disposal.IsCompleted);
        pending.SetResult(Result.Success<string>("Credentials:Password=late-test-secret"));
        await ObserveAsync(load);
        await ObserveAsync(disposal);

        Assert.Null(state.Read.Value);
        Assert.False(state.Read.HasLoaded);
        state.ToggleSensitiveLine(0);
        Assert.Empty(state.RevealedLines);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(state.LoadAsync()));
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
