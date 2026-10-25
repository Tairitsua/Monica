using Monica.Configuration.UI.Pages;
using Monica.Configuration.UI.Support;

namespace Test.Monica.Configuration.UI.Support;

public sealed class ConfigurationHistoryQuerySessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateProducer_WhenNewFiltersAreLoaded_ShouldNeitherReplaceNorAppendAcrossQueryScope(bool append)
    {
        var owner = new ConfigurationOperationLifetime();
        var queries = new ConfigurationHistoryQuerySession();
        var oldQuery = Begin(queries, "old exact identity");
        var produced = NewCompletion<string[]>();
        var visible = new List<string> { "old first page" };
        var oldLoad = owner.RunAsync(async _ =>
        {
            var rows = await produced.Task;
            return queries.TryApply(oldQuery, () =>
            {
                if (!append) visible.Clear();
                visible.AddRange(rows);
            });
        });

        var newQuery = Begin(queries, "new exact identity");
        Assert.True(queries.TryApply(newQuery, () => { visible.Clear(); visible.Add("new result"); }));
        produced.SetResult(["late old result"]);

        Assert.False(await ObserveAsync(oldLoad));
        Assert.Equal(new[] { "new result" }, visible);
        await ObserveAsync(owner.RetireAsync().AsTask());
    }

    [Fact]
    public async Task Refresh_WhenFiltersAreIdentical_ShouldStillRejectTheEarlierDelayedResult()
    {
        var owner = new ConfigurationOperationLifetime();
        var queries = new ConfigurationHistoryQuerySession();
        var initial = Begin(queries, "exact identity");
        var produced = NewCompletion<bool>();
        var observed = "initial";
        var earlier = owner.RunAsync(async _ =>
        {
            await produced.Task;
            return queries.TryApply(initial, () => observed = "earlier delayed capture");
        });
        var refresh = queries.Begin(initial.History, initial.Groups);
        Assert.True(queries.TryApply(refresh, () => observed = "refreshed capture"));
        produced.SetResult(true);

        Assert.False(await ObserveAsync(earlier));
        Assert.Equal("refreshed capture", observed);
        await ObserveAsync(owner.RetireAsync().AsTask());
    }

    [Fact]
    public async Task Retirement_WhenTokenlessReadFinishesLate_ShouldDrainItWithoutAdoptingVisibleEvidence()
    {
        var owner = new ConfigurationOperationLifetime();
        var queries = new ConfigurationHistoryQuerySession();
        var query = Begin(queries, "exact identity");
        var produced = NewCompletion<bool>();
        var adopted = false;
        var operation = owner.RunAsync(async _ =>
        {
            await produced.Task;
            return queries.TryApply(query, () => adopted = true);
        });
        queries.Invalidate();
        var retirement = owner.RetireAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        produced.SetResult(true);

        Assert.False(await ObserveAsync(operation));
        await ObserveAsync(retirement);
        Assert.False(adopted);
    }

    private static ConfigurationHistoryQuerySession.Query Begin(ConfigurationHistoryQuerySession session, string definitionKey)
    {
        var requests = ConfigurationHistoryPage.CreateRequests(definitionKey, "", "all", null, null, TimeZoneInfo.Utc);
        return session.Begin(requests.History, requests.Groups);
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private static Task<T> ObserveAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
