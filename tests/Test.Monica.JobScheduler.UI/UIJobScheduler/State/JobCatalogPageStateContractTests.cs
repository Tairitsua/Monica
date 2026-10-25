using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using MudBlazor;
using NSubstitute;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class JobCatalogPageStateContractTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ApplyInitialQuery_WhenOwnerContainsWhitespaceAndReservedUnicode_ShouldQueryItsExactStoredIdentity()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        state.ApplyInitialQuery(null, SchedulerStateTestBoundary.Owner);
        await ObserveAsync(state.InitializeAsync());
        var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var items = page.Items;
        Assert.NotNull(items);

        Assert.Equal(SchedulerStateTestBoundary.Owner, state.OwnerId);
        Assert.Equal(2, page.TotalItems);
        Assert.All(items, item => Assert.Equal(SchedulerStateTestBoundary.Owner, item.Definition.OwnerKey));
        Assert.Equal(SchedulerStateTestBoundary.Owner, Assert.Single(boundary.CatalogQueries).OwnerKey);
    }

    [Fact]
    public async Task LoadTableAsync_WhenRequestsOverlap_ShouldRetainEachFrozenQueryAndReturnItsOwnPage()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        var authorize = Signal<bool>();
        boundary.Access.Authorize = _ => boundary.Access.Calls == 1 ? authorize.Task : Task.FromResult(true);
        var state = boundary.CreateCatalog();
        try
        {
            state.SearchText = SchedulerStateTestBoundary.Job;
            var first = state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
            state.SearchText = "another-job";
            var second = state.LoadTableAsync(Table(JobDefinitionSortField.OwnerKey, SortDirection.Descending), TestContext.Current.CancellationToken);
            Assert.Empty(boundary.CatalogQueries);
            authorize.TrySetResult(true);
            var firstPage = await first.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
            var secondPage = await second.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
            var firstItems = firstPage.Items;
            var secondItems = secondPage.Items;
            Assert.NotNull(firstItems);
            Assert.NotNull(secondItems);

            Assert.Equal(SchedulerStateTestBoundary.Job, Assert.Single(firstItems).Definition.Declaration.JobKey);
            Assert.Equal("another-job", Assert.Single(secondItems).Definition.Declaration.JobKey);
            Assert.Collection(boundary.CatalogQueries,
                query => { Assert.Equal(SchedulerStateTestBoundary.Job, query.SearchText); Assert.Equal(JobDefinitionSortField.JobName, query.SortField); Assert.False(query.SortDescending); },
                query => { Assert.Equal("another-job", query.SearchText); Assert.Equal(JobDefinitionSortField.OwnerKey, query.SortField); Assert.True(query.SortDescending); });
            Assert.Equal("another-job", Assert.Single(state.Summaries).Definition.Declaration.JobKey);
        }
        finally
        {
            authorize.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task SetSelectedDisabledAsync_WhenSelectionChangesDuringAuthorization_ShouldMutateOnlyConfirmedTargets()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        var state = boundary.CreateCatalog();
        var authorize = Signal<bool>();
        try
        {
            var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
            var items = page.Items;
            Assert.NotNull(items);
            var target = items.Single(item => item.Definition.Declaration.JobKey == SchedulerStateTestBoundary.Job);
            var other = items.Single(item => item.Definition.Declaration.JobKey == "another-job");
            state.ToggleSelection(target);
            JobOperationalSummary[] confirmed = [target];
            boundary.Access.Authorize = _ => authorize.Task;
            var mutation = state.SetSelectedDisabledAsync(true, confirmed);
            confirmed[0] = other;
            state.SelectAllRecurring(false);
            state.ToggleSelection(other);
            authorize.TrySetResult(true);
            var result = await mutation.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
            Assert.False(result.IsFailed(out _, out var batch));
            Assert.Equal(SchedulerStateTestBoundary.Job, Assert.Single(batch.Items).JobKey);
            var targetDefinition = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner,
                SchedulerStateTestBoundary.Job, TestContext.Current.CancellationToken);
            var otherDefinition = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner,
                "another-job", TestContext.Current.CancellationToken);
            Assert.True(targetDefinition!.IsDisabled);
            Assert.False(otherDefinition!.IsDisabled);
            Assert.Equal(other.Definition.Id, Assert.Single(state.SelectedJobIds));
        }
        finally
        {
            authorize.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task SetSelectedDisabledAsync_WhenOneRevisionIsStale_ShouldRetainFailedSelectionAndIndependentSuccessfulItem()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var items = page.Items;
        Assert.NotNull(items);
        var stale = items.Single(item => item.Definition.Declaration.JobKey == SchedulerStateTestBoundary.Job);
        state.SelectAllRecurring(true);
        await boundary.Store.UpdatePolicyAsync(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner, SchedulerStateTestBoundary.Job,
            new JobPolicyChange
            {
                Overrides = stale.Definition.Policy.Overrides with { MaxRetainedHistoryRecords = 0 },
                ExpectedConcurrencyStamp = stale.Definition.Policy.ConcurrencyStamp
            }, TestContext.Current.CancellationToken);

        var result = await state.SetSelectedDisabledAsync(true, items.ToArray());

        Assert.False(result.IsFailed(out _, out var batch));
        Assert.Equal(1, batch.SucceededCount);
        Assert.Equal(1, batch.FailedCount);
        Assert.Equal(stale.Definition.Id, Assert.Single(state.SelectedJobIds));
        var retained = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner,
            SchedulerStateTestBoundary.Job, TestContext.Current.CancellationToken);
        Assert.False(retained!.IsDisabled);
        Assert.Equal(0, retained.Policy.Overrides.MaxRetainedHistoryRecords);
        var succeeded = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner,
            "another-job", TestContext.Current.CancellationToken);
        Assert.True(succeeded!.IsDisabled);
    }

    [Fact]
    public async Task InitializeAsync_WhenFacetReadFails_ShouldDistinguishFailureFromSuccessfulEmptyAndKeepPriorEvidence()
    {
        var boundary = new SchedulerStateTestBoundary { FacetFailure = new IOException("Facet store unavailable.") };
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        await ObserveAsync(state.InitializeAsync());
        Assert.False(state.HasLoadedFacets);
        Assert.Empty(state.OwnerFacets);
        Assert.NotNull(state.FacetsError);
        Assert.Contains("Facet store unavailable", state.FacetsError);

        boundary.FacetFailure = null;
        await ObserveAsync(state.InitializeAsync());
        Assert.True(state.HasLoadedFacets);
        var accepted = state.OwnerFacets;
        Assert.Equal(2, Assert.Single(accepted).Value);
        Assert.Null(state.FacetsError);
        boundary.FacetFailure = new IOException("Next facet capture failed.");
        await ObserveAsync(state.InitializeAsync());
        Assert.True(state.HasLoadedFacets);
        Assert.Same(accepted, state.OwnerFacets);
        Assert.NotNull(state.FacetsError);
        Assert.Contains("Next facet capture failed", state.FacetsError);

        var emptyBoundary = new SchedulerStateTestBoundary();
        await using var empty = emptyBoundary.CreateCatalog();
        await ObserveAsync(empty.InitializeAsync());
        Assert.True(empty.HasLoadedFacets);
        Assert.Empty(empty.OwnerFacets);
        Assert.Null(empty.FacetsError);
    }

    [Fact]
    public async Task DisposeAsync_WhenAuthorizationIgnoresCancellation_ShouldDrainInitializationAndStandaloneAccessCheck()
    {
        var boundary = new SchedulerStateTestBoundary();
        var authorize = Signal<bool>();
        boundary.Access.Authorize = _ => authorize.Task;
        var state = boundary.CreateCatalog();
        var initialization = state.InitializeAsync();
        var reauthorization = state.ReauthorizeAsync();
        var retirement = state.DisposeAsync().AsTask();
        try
        {
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.LoadTableAsync(Table(), TestContext.Current.CancellationToken));
            authorize.TrySetResult(true);
            await ObserveAsync(initialization);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(reauthorization));
            await ObserveAsync(retirement);
            Assert.False(state.AccessChecked);
            Assert.False(state.HasLoadedFacets);
            Assert.Empty(boundary.CatalogQueries);
        }
        finally
        {
            authorize.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task SetDisabledAsync_WhenAuthorizationIsRevoked_ShouldClearEvidenceAndLeavePolicyUnchanged()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        await ObserveAsync(state.InitializeAsync());
        var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var items = page.Items;
        Assert.NotNull(items);
        var target = items.First();
        boundary.Access.Authorized = false;
        var result = await state.SetDisabledAsync(target, true);
        Assert.True(result.IsFailed(out _));
        Assert.False(state.IsAuthorized);
        Assert.Empty(state.Summaries);
        Assert.Empty(state.OwnerFacets);
        Assert.False(state.HasLoadedFacets);
        var definition = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope, target.Definition.OwnerKey,
            target.Definition.Declaration.JobKey, TestContext.Current.CancellationToken);
        Assert.False(definition!.IsDisabled);
    }

    [Fact]
    public async Task DisposeAsync_WhenQueryOwnerIsReplaced_ShouldKeepNewPresenceScopeIndependentOfLateRowsAndConfirmation()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        var oldState = boundary.CreateCatalog();
        await using var replacement = boundary.CreateCatalog();
        var pending = Signal<QueryResult<JobOperationalSummary>>();
        try
        {
            oldState.ApplyInitialQuery(true, SchedulerStateTestBoundary.Owner);
            var accepted = await oldState.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
            var oldRows = oldState.Summaries;
            var acceptedItems = accepted.Items;
            Assert.NotNull(acceptedItems);
            var confirmed = acceptedItems.ToArray();
            boundary.LoadCatalog = (_, _) => pending.Task;
            var oldLoad = oldState.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
            var retirement = oldState.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);

            await boundary.Store.SyncOwnerSnapshotAsync(
                new JobOwnerSnapshot(SchedulerStateTestBoundary.Scope, SchedulerStateTestBoundary.Owner, []),
                TestContext.Current.CancellationToken);
            boundary.LoadCatalog = null;
            replacement.ApplyInitialQuery(false, SchedulerStateTestBoundary.Owner);
            await ObserveAsync(replacement.InitializeAsync());
            var replacementPage = await replacement.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
            var replacementItems = replacementPage.Items;
            Assert.NotNull(replacementItems);
            Assert.Equal(2, replacementPage.TotalItems);
            Assert.All(replacementItems, item => Assert.False(item.Definition.IsPresent));
            Assert.Null(replacement.SelectedJobType);
            Assert.Equal(SchedulerStateTestBoundary.Owner, replacement.OwnerId);
            var replacementRows = replacement.Summaries;
            var selected = replacementItems.First();
            replacement.ToggleSelection(selected);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => oldState.SetSelectedDisabledAsync(true, confirmed));
            pending.TrySetResult(new([.. confirmed], accepted.TotalItems));
            // Linked cancellation may race the caller's result; retired owners must never adopt that result.
            try { await ObserveAsync(oldLoad); }
            catch (OperationCanceledException) { }
            await ObserveAsync(retirement);
            Assert.Same(oldRows, oldState.Summaries);
            Assert.Same(replacementRows, replacement.Summaries);
            Assert.Equal(selected.Definition.Id, Assert.Single(replacement.SelectedJobIds));
            var definition = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope,
                selected.Definition.OwnerKey, selected.Definition.Declaration.JobKey, TestContext.Current.CancellationToken);
            Assert.False(definition!.IsDisabled);
            Assert.Collection(boundary.CatalogQueries,
                query => { Assert.True(query.IsPresent); Assert.Equal(SchedulerStateTestBoundary.Owner, query.OwnerKey); },
                query => { Assert.True(query.IsPresent); Assert.Equal(SchedulerStateTestBoundary.Owner, query.OwnerKey); },
                query => { Assert.False(query.IsPresent); Assert.Equal(SchedulerStateTestBoundary.Owner, query.OwnerKey); });
        }
        finally
        {
            pending.TrySetResult(new([], 0));
            await ObserveAsync(oldState.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationAsync_WhenAuthorizationThrows_ShouldReturnLocalFailureWithoutPolicyOrQueueMutation(bool runNow)
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var items = page.Items;
        Assert.NotNull(items);
        var target = items.First();
        boundary.Access.Authorize = _ => throw new IOException("Operator access lookup unavailable.");

        if (runNow)
        {
            var result = await state.RunRecurringNowAsync(target);
            Assert.True(result.IsFailed(out var error));
            Assert.Equal("Operator access lookup unavailable.", error.Message);
        }
        else
        {
            var result = await state.SetDisabledAsync(target, true);
            Assert.True(result.IsFailed(out var error));
            Assert.Equal("Operator access lookup unavailable.", error.Message);
        }
        Assert.Equal("Operator access lookup unavailable.", state.Error);
        Assert.True(state.AccessChecked);
        Assert.False(state.IsAuthorized);
        Assert.False(state.IsMutating);
        Assert.Empty(state.Summaries);
        Assert.DoesNotContain(boundary.Boundary.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is "UpdatePolicyAsync" or "RunRecurringNowAsync");
        var durable = await boundary.Store.GetOperationalSummaryAsync(SchedulerStateTestBoundary.Scope,
            target.Definition.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(durable);
        Assert.False(durable.Definition.IsDisabled);
        Assert.Equal(0, durable.QueuedExecutionCount);
    }

    [Fact]
    public async Task SetSelectedDisabledAsync_WhenAuthorizationThrows_ShouldRejectAllConfirmedMutationsWithLocalError()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        var page = await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var items = page.Items;
        Assert.NotNull(items);
        state.SelectAllRecurring(true);
        boundary.Access.Authorize = _ => Task.FromException<bool>(new IOException("Batch access unavailable."));

        var result = await state.SetSelectedDisabledAsync(true, items.ToArray());

        Assert.True(result.IsFailed(out var error));
        Assert.Equal("Batch access unavailable.", error.Message);
        Assert.Equal("Batch access unavailable.", state.Error);
        Assert.False(state.IsAuthorized);
        Assert.Empty(state.SelectedJobIds);
        Assert.DoesNotContain(boundary.Boundary.ReceivedCalls(), call => call.GetMethodInfo().Name == "UpdatePolicyAsync");
        foreach (var target in items)
        {
            var definition = await boundary.Store.GetDefinitionAsync(SchedulerStateTestBoundary.Scope,
                target.Definition.OwnerKey, target.Definition.Declaration.JobKey, TestContext.Current.CancellationToken);
            Assert.NotNull(definition);
            Assert.False(definition.IsDisabled);
        }
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenAuthorizationThrows_ShouldRejectAccessLocallyAndClearInaccessibleEvidence()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        await ObserveAsync(state.InitializeAsync());
        await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        boundary.Access.Authorize = _ => throw new IOException("Access service unavailable.");

        Assert.False(await state.ReauthorizeAsync());

        Assert.True(state.AccessChecked);
        Assert.False(state.IsAuthorized);
        Assert.Equal("Access service unavailable.", state.Error);
        Assert.Empty(state.Summaries);
        Assert.Empty(state.OwnerFacets);
        Assert.False(state.HasLoadedFacets);
        Assert.Single(boundary.CatalogQueries);
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenAccessProviderCancels_ShouldPreserveCancellationInsteadOfInventingDenial()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateCatalog();
        await state.LoadTableAsync(Table(), TestContext.Current.CancellationToken);
        var accepted = state.Summaries;
        boundary.Access.Authorize = _ => Task.FromCanceled<bool>(new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.ReauthorizeAsync());

        Assert.True(state.IsAuthorized);
        Assert.Same(accepted, state.Summaries);
        Assert.Null(state.Error);
        Assert.Single(boundary.CatalogQueries);
    }

    private static TableState Table(JobDefinitionSortField field = JobDefinitionSortField.JobName, SortDirection direction = SortDirection.Ascending) =>
        new() { Page = 0, PageSize = 20, SortLabel = field.ToString(), SortDirection = direction };
    private static Task ObserveAsync(Task task) => task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
