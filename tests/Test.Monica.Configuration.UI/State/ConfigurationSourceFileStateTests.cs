using Monica.Configuration.Models;
using Monica.Configuration.UI.State;
using Monica.Core.Results;

namespace Test.Monica.Configuration.UI.State;

public sealed class ConfigurationSourceFileStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSource_ShouldClearPreviousEvidenceAndRejectLateOlderRead(bool olderFails)
    {
        const string originalKey = "  source:/区域?revision=1&value=%25  ";
        const string newKey = "source:/区域?revision=2&value=%25";
        var older = NewCompletion<Result<ConfigurationSourceFileView>>();
        var newer = NewCompletion<Result<ConfigurationSourceFileView>>();
        var calls = new List<string>();
        await using var state = new ConfigurationSourceFileState(key =>
        {
            calls.Add(key);
            return calls.Count switch
            {
                1 => Task.FromResult(Result.Success(View(key, "{\"original\":true}"))),
                2 => older.Task,
                _ => newer.Task
            };
        }, "unavailable");
        await ObserveAsync(state.LoadAsync(originalKey));
        var oldRefresh = state.LoadAsync(originalKey);
        Assert.True(state.Read.HasLoaded);
        var newRead = state.LoadAsync(newKey);
        Assert.Equal(newKey, state.SourceKey);
        Assert.False(state.Read.HasLoaded);
        Assert.Null(state.Read.Value);
        Assert.Null(state.Read.ObservedAtUtc);
        older.SetResult(olderFails ? Result.Failure<ConfigurationSourceFileView>("old unavailable") : Result.Success(View(originalKey, "{\"old\":true}")));
        await ObserveAsync(oldRefresh);
        Assert.Null(state.Read.Value);
        Assert.Null(state.Read.Error);
        Assert.True(state.Read.Loading);
        newer.SetResult(Result.Success(View(newKey, "{}")));
        await ObserveAsync(newRead);

        Assert.Equal(new[] { originalKey, originalKey, newKey }, calls);
        var accepted = Assert.IsType<ConfigurationSourceFileView>(state.Read.Value);
        Assert.Equal(newKey, accepted.Source.SourceKey);
        Assert.Equal("{}", accepted.Content);
        Assert.True(state.Read.HasLoaded);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("missing")]
    [InlineData("empty-object")]
    public async Task SameSourceRefresh_ShouldRetainAcceptedFileUntilNewestOutcome(string outcome)
    {
        var pending = NewCompletion<Result<ConfigurationSourceFileView>>();
        var calls = 0;
        await using var state = new ConfigurationSourceFileState(key => ++calls == 1
            ? Task.FromResult(Result.Success(View(key, "{\"accepted\":true}"))) : pending.Task, "unavailable");
        await ObserveAsync(state.LoadAsync("exact-key"));
        var accepted = state.Read.Value;
        var observed = state.Read.ObservedAtUtc;
        var refresh = state.LoadAsync("exact-key");
        Assert.Same(accepted, state.Read.Value);
        Assert.Equal(observed, state.Read.ObservedAtUtc);
        Assert.True(state.Read.HasLoaded);
        pending.SetResult(outcome switch
        {
            "failure" => Result.Failure<ConfigurationSourceFileView>("file unavailable"),
            "missing" => new Result<ConfigurationSourceFileView> { Status = ResultStatus.Ok, Data = null },
            _ => Result.Success(View("exact-key", "{}"))
        });
        await ObserveAsync(refresh);

        Assert.False(state.Read.Loading);
        Assert.Equal(outcome == "empty-object", state.Read.HasLoaded);
        if (outcome == "empty-object")
        {
            Assert.Equal("{}", Assert.IsType<ConfigurationSourceFileView>(state.Read.Value).Content);
            Assert.Null(state.Read.Error);
        }
        else
        {
            Assert.Null(state.Read.Value);
            Assert.Null(state.Read.ObservedAtUtc);
            Assert.Equal(outcome == "failure" ? "file unavailable" : "unavailable", state.Read.Error);
        }
    }

    [Fact]
    public async Task Dispose_ShouldJoinActualReadAndRejectLaterRequestsWithoutLateAdoption()
    {
        var pending = NewCompletion<Result<ConfigurationSourceFileView>>();
        var calls = 0;
        var state = new ConfigurationSourceFileState(_ => { calls++; return pending.Task; }, "unavailable");
        var load = state.LoadAsync("exact");
        var disposal = state.DisposeAsync().AsTask();
        Assert.Same(disposal, state.DisposeAsync().AsTask());
        Assert.False(disposal.IsCompleted);
        pending.SetResult(Result.Success(View("exact", "{}")));
        await ObserveAsync(load);
        await ObserveAsync(disposal);

        Assert.False(state.Read.HasLoaded);
        Assert.Null(state.Read.Value);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(state.LoadAsync("different")));
        Assert.Equal(1, calls);
        Assert.Equal("exact", state.SourceKey);
    }

    private static ConfigurationSourceFileView View(string key, string content) => new()
    {
        Source = new() { SourceKey = key, DisplayName = key, ProviderType = "ReviewProvider" }, Content = content
    };
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
