using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using MudBlazor;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class JobSchedulerNativeTableStateTests
{
    [Fact]
    public async Task ExecutionLoadTableAsync_WhenMudStateIsProvided_ShouldMapPagingAndSorting()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "execution-table-state-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}",
            AvailableAtUtc = DateTimeOffset.UtcNow
        }, Xunit.TestContext.Current.CancellationToken);
        await using var state = context.Services
            .GetRequiredService<JobExecutionsStateFactory>()
            .CreatePageState();
        await state.InitializeAsync();

        var result = await state.LoadTableAsync(new TableState
        {
            Page = 0,
            PageSize = 10,
            SortLabel = nameof(JobExecutionSortField.AvailableAtUtc),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);

        result.Items.Should().ContainSingle(item => item.InstanceId == execution.InstanceId);
        result.TotalItems.Should().Be(1);
        state.PageNumber.Should().Be(1);
        state.PageSize.Should().Be(10);
        state.SortField.Should().Be(JobExecutionSortField.AvailableAtUtc);
        state.SortDescending.Should().BeFalse();
    }

    [Fact]
    public async Task CatalogLoadTableAsync_WhenMudStateIsProvided_ShouldMapPagingAndSorting()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);
        await state.InitializeAsync();

        var result = await state.LoadTableAsync(new TableState
        {
            Page = 0,
            PageSize = 10,
            SortLabel = nameof(JobDefinitionSortField.OwnerKey),
            SortDirection = SortDirection.Descending
        }, Xunit.TestContext.Current.CancellationToken);

        result.Items.Should().ContainSingle(summary =>
            summary.Definition.Declaration.JobKey == context.RecurringDefinition.Declaration.JobKey);
        result.TotalItems.Should().Be(1);
        state.PageNumber.Should().Be(1);
        state.PageSize.Should().Be(10);
        state.SortField.Should().Be(JobDefinitionSortField.OwnerKey);
        state.SortDescending.Should().BeTrue();
    }

    [Fact]
    public async Task ExecutionLoadTableAsync_WhenRequestIsCancelled_ShouldPropagateCancellation()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services
            .GetRequiredService<JobExecutionsStateFactory>()
            .CreatePageState();
        await state.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> action = async () => await state.LoadTableAsync(new TableState
        {
            PageSize = 10,
            SortLabel = nameof(JobExecutionSortField.CreatedAtUtc),
            SortDirection = SortDirection.Descending
        }, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecutionLoadTableAsync_WhenCustomRangeIsInvalid_ShouldClearStaleResults()
    {
        await using var context = new JobSchedulerUiTestContext();
        await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "execution-invalid-range-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}",
            AvailableAtUtc = DateTimeOffset.UtcNow
        }, Xunit.TestContext.Current.CancellationToken);
        await using var state = context.Services
            .GetRequiredService<JobExecutionsStateFactory>()
            .CreatePageState();
        await state.InitializeAsync();
        var tableState = new TableState
        {
            PageSize = 10,
            SortLabel = nameof(JobExecutionSortField.CreatedAtUtc),
            SortDirection = SortDirection.Descending
        };
        var initial = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);
        initial.Items.Should().ContainSingle();

        state.TimeRange = ExecutionTimeRange.Custom;
        state.CustomStartDate = new DateTime(2026, 8, 15);
        state.CustomEndDate = new DateTime(2026, 8, 14);
        var invalid = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);

        invalid.Items.Should().BeEmpty();
        invalid.TotalItems.Should().Be(0);
        state.ObservedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task CatalogInitializeAsync_WhenAuthorizationThrows_ShouldCompleteAccessCheck()
    {
        await using var context = new JobSchedulerUiTestContext();
        context.Access.AuthorizationException = new InvalidOperationException("Authorization unavailable");
        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);

        await state.InitializeAsync();

        state.AccessChecked.Should().BeTrue();
        state.IsAuthorized.Should().BeFalse();
        state.Error.Should().Be("Authorization unavailable");
    }

    [Fact]
    public async Task CatalogLoadTableAsync_WhenReloadingVisiblePage_ShouldRetainSelection()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);
        await state.InitializeAsync();
        var tableState = new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        };
        var initial = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);
        var summary = initial.Items.Should().ContainSingle().Which;
        state.ToggleSelection(summary);

        await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);

        state.SelectedJobIds.Should().ContainSingle().Which.Should()
            .Be(summary.Definition.Id);
    }

    [Fact]
    public async Task CatalogLoadTableAsync_WhenAbsentDeepLinkApplied_ShouldQueryOnlyAbsentDefinitions()
    {
        await using var context = new JobSchedulerUiTestContext();
        var retiredDeclaration = context.RecurringDefinition.Declaration with
        {
            JobKey = "Sample.Jobs.RetiredCleanup",
            JobName = "Retired cleanup"
        };
        var survivingDeclaration = context.RecurringDefinition.Declaration with
        {
            JobKey = "Sample.Jobs.SurvivingCleanup",
            JobName = "Surviving cleanup"
        };
        await context.Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            JobSchedulerUiTestContext.SCOPE,
            "worker-b",
            [retiredDeclaration, survivingDeclaration]), Xunit.TestContext.Current.CancellationToken);
        // Republish without the retired declaration so it stays in the catalog as an absent definition.
        await context.Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            JobSchedulerUiTestContext.SCOPE,
            "worker-b",
            [survivingDeclaration]), Xunit.TestContext.Current.CancellationToken);

        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);
        state.ApplyInitialQuery(present: false, owner: null);
        await state.InitializeAsync();

        var result = await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);

        state.PresentFilter.Should().BeFalse();
        result.TotalItems.Should().Be(1);
        result.Items.Should().ContainSingle()
            .Which.Definition.Declaration.JobKey.Should().Be(retiredDeclaration.JobKey);
        result.Items.Single().Definition.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task CatalogSelection_ShouldUseOwnerAndJobKeyIdentity()
    {
        await using var context = new JobSchedulerUiTestContext();
        await context.Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            JobSchedulerUiTestContext.SCOPE,
            "worker-b",
            [context.RecurringDefinition.Declaration]), Xunit.TestContext.Current.CancellationToken);
        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);

        await state.InitializeAsync();
        var tableState = new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        };
        var summaries = (await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken))
            .Items!
            .Where(summary => summary.Definition.Declaration.JobKey == context.RecurringDefinition.Declaration.JobKey)
            .ToArray();
        summaries.Should().HaveCount(2);

        var ownerA = summaries.Single(summary => summary.Definition.OwnerKey == JobSchedulerUiTestContext.OWNER);
        var ownerB = summaries.Single(summary => summary.Definition.OwnerKey == "worker-b");
        state.ToggleSelection(ownerA);
        state.SelectedRecurringSummaries.Should().ContainSingle()
            .Which.Definition.OwnerKey.Should().Be(JobSchedulerUiTestContext.OWNER);

        state.ToggleSelection(ownerB);
        state.SelectedJobIds.Should().BeEquivalentTo([ownerA.Definition.Id, ownerB.Definition.Id]);

        var result = await state.SetDisabledAsync(ownerB, true);
        var error = result;
        result.IsFailure().Should().BeFalse(error?.Message);
        state.Summaries
            .Where(summary => summary.Definition.Declaration.JobKey == context.RecurringDefinition.Declaration.JobKey)
            .Single(summary => summary.Definition.OwnerKey == "worker-b")
            .Definition.IsDisabled.Should().BeTrue();
        state.Summaries
            .Where(summary => summary.Definition.Declaration.JobKey == context.RecurringDefinition.Declaration.JobKey)
            .Single(summary => summary.Definition.OwnerKey == JobSchedulerUiTestContext.OWNER)
            .Definition.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task CatalogBatchUpdate_WhenOnePolicyIsStale_ShouldRetainOnlyFailedSelectionAfterReload()
    {
        await using var context = new JobSchedulerUiTestContext();
        JobDeclaration[] declarations =
        [
            context.RecurringDefinition.Declaration with
            {
                JobKey = "Sample.Jobs.PartialBatchAlpha",
                JobName = "Partial batch alpha"
            },
            context.RecurringDefinition.Declaration with
            {
                JobKey = "Sample.Jobs.PartialBatchBeta",
                JobName = "Partial batch beta"
            }
        ];
        await context.Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            declarations), Xunit.TestContext.Current.CancellationToken);

        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);
        await state.InitializeAsync();
        var tableState = new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        };
        var initial = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);
        initial.Items.Should().HaveCount(2);
        state.SelectAllRecurring(true);
        var staleDefinition = initial.Items.First().Definition;
        await context.Store.UpdatePolicyAsync(
            JobSchedulerUiTestContext.SCOPE,
            staleDefinition.OwnerKey,
            staleDefinition.Declaration.JobKey,
            new JobPolicyChange
            {
                Overrides = staleDefinition.Policy.Overrides with { DisabledOverride = true },
                ExpectedConcurrencyStamp = staleDefinition.Policy.ConcurrencyStamp
            },
            Xunit.TestContext.Current.CancellationToken);

        var result = await state.SetSelectedDisabledAsync(true);
        var error = result;
        var batch = result.Data;
        result.IsFailure().Should().BeFalse(error?.Message);
        batch.Should().NotBeNull();
        batch!.SucceededCount.Should().Be(1);
        batch.FailedCount.Should().Be(1);
        state.SelectedJobIds.Should().ContainSingle().Which.Should()
            .Be(staleDefinition.Id);

        await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);

        state.SelectedJobIds.Should().ContainSingle().Which.Should()
            .Be(staleDefinition.Id);
    }

    [Fact]
    public async Task CatalogLoadTableAsync_WhenAuthorizationThrows_ShouldReturnCoherentEmptyPage()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services
            .GetRequiredService<JobCatalogPageStateFactory>()
            .Create(20);
        await state.InitializeAsync();
        var tableState = new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        };
        var initial = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);
        state.ToggleSelection(initial.Items.Should().ContainSingle().Which);
        context.Access.AuthorizationException = new InvalidOperationException("Authorization unavailable");

        var failed = await state.LoadTableAsync(tableState, Xunit.TestContext.Current.CancellationToken);

        failed.Items.Should().BeEmpty();
        failed.TotalItems.Should().Be(0);
        state.SelectedJobIds.Should().BeEmpty();
        state.Error.Should().Be("Authorization unavailable");
    }
}
