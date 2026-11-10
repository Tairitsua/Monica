using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Facades;

public sealed class JobSchedulerFacadeReadBudgetTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 10, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExecutionSampleAsync_WhenPopulationExceedsLimit_ShouldRetainCountAndStableNewestSample(bool useEfCore)
    {
        var counter = new ReadCounter();
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW, counter);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("sample-job"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("sample-job"));
        for (var index = 0; index < 2001; index++)
            await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "sample-job", instanceId: $"sample-{index:D4}");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_B, "sample-job", instanceId: "excluded-owner");

        var facade = CreateFacade(fixture);
        counter.Commands.Clear();
        var result = await facade.GetExecutionSampleAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = "caller-scope-is-ignored",
            OwnerKey = StoreFixture.OWNER_A,
            CreatedAfterUtc = NOW,
            CreatedBeforeUtc = NOW,
            PageNumber = 99,
            PageSize = 1,
            SortDescending = false,
            SortField = JobExecutionSortField.CompletedAtUtc
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(2001, result.Data.TotalCount);
        Assert.Equal(2000, result.Data.Items.Count);
        Assert.Equal("sample-2000", result.Data.Items[0].InstanceId);
        Assert.Equal("sample-0001", result.Data.Items[^1].InstanceId);
        Assert.All(result.Data.Items, item => Assert.Empty(item.History));
        if (useEfCore)
        {
            Assert.Equal(2, counter.Commands.Count);
            Assert.Single(counter.Commands, sql => sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        }

        var page = await facade.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE, PageSize = int.MaxValue
        }, TestContext.Current.CancellationToken);
        Assert.Equal(ResultStatus.Ok, page.Status);
        Assert.Equal(200, page.Data!.Items.Count);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenReadingThroughput_ShouldAggregateWithoutLoadingAnotherExecutionPage()
    {
        var counter = new ReadCounter();
        await using var fixture = await StoreFixture.CreateAsync(true, NOW.AddDays(-2), counter);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("sample-job"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "sample-job", instanceId: "old-execution");
        fixture.Time.Advance(TimeSpan.FromDays(2));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "sample-job", instanceId: "recent-execution");
        counter.Commands.Clear();

        var result = await CreateFacade(fixture).GetOverviewAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Equal(1 / 24d, result.Data!.ExecutionsPerHourLast24h);
        Assert.Equal(2, result.Data.ExecutionStateCounts[JobExecutionState.Queued]);
        Assert.Single(counter.Commands, sql => sql.Contains("TemplateJson", StringComparison.Ordinal));
    }

    private static JobSchedulerFacade CreateFacade(StoreFixture fixture) => new(
        fixture.Store,
        Options.Create(new ModuleJobSchedulerOption { SchedulerScopeKey = StoreFixture.SCOPE, ProjectName = StoreFixture.OWNER_A }),
        fixture.Time,
        new JobSchedulerRuntimeState(),
        NullLogger<JobSchedulerFacade>.Instance);

    private sealed class ReadCounter : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
