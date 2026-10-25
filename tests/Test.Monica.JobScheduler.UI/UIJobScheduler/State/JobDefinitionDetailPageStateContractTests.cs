using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using NSubstitute;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class JobDefinitionDetailPageStateContractTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RefreshAsync_WhenIdentityIsOpaque_ShouldUseExactOwnerAndJobWithoutFallback()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateDetail();
        await ObserveAsync(state.InitializeAsync());
        Assert.NotNull(state.Summary);
        var exact = new JobId(SchedulerStateTestBoundary.Owner, SchedulerStateTestBoundary.Job);
        Assert.Equal(exact, state.Summary.Definition.Id);
        Assert.Equal(exact, Assert.Single(boundary.DetailQueries));

        var missingIdentity = new JobId("unknown-owner", SchedulerStateTestBoundary.Job);
        await using var missing = boundary.CreateDetail(missingIdentity);
        await ObserveAsync(missing.InitializeAsync());
        Assert.Equal(missingIdentity, missing.JobId);
        Assert.True(missing.IsNotFound);
        Assert.Null(missing.Summary);
        Assert.Equal(missingIdentity, boundary.DetailQueries.Last());
    }

    [Fact]
    public async Task RefreshAsync_WhenInitialAuthorizationThrows_ShouldCompleteAccessCheckWithoutReading()
    {
        var boundary = new SchedulerStateTestBoundary();
        boundary.Access.Authorize = _ => throw new IOException("Authorization unavailable.");
        await using var state = boundary.CreateDetail();
        await ObserveAsync(state.InitializeAsync());
        Assert.True(state.AccessChecked);
        Assert.False(state.IsAuthorized);
        Assert.Equal("Authorization unavailable.", state.Error);
        Assert.False(state.IsLoading);
        Assert.Empty(boundary.DetailQueries);
    }

    [Fact]
    public async Task DisposeAsync_WhenReadIgnoresCancellation_ShouldDrainActualProducerAndRejectLateEvidence()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        var state = boundary.CreateDetail();
        var pending = Signal<JobOperationalSummary?>();
        try
        {
            await ObserveAsync(state.RefreshAsync());
            var accepted = state.Summary!;
            var observedAt = state.ObservedAtUtc;
            var callbacks = 0;
            state.Changed += () => { callbacks++; return Task.CompletedTask; };
            boundary.LoadDetail = (_, _) => pending.Task;
            var refresh = state.RefreshAsync();
            var retirement = state.DisposeAsync().AsTask();
            var countAtRetirement = callbacks;
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            pending.TrySetResult(accepted with { QueuedExecutionCount = 100 });
            await ObserveAsync(Task.WhenAll(refresh, retirement));
            Assert.Same(accepted, state.Summary);
            Assert.Equal(observedAt, state.ObservedAtUtc);
            Assert.Equal(countAtRetirement, callbacks);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.RefreshAsync());
            Assert.Collection(boundary.DetailQueries,
                identity => Assert.Equal(state.JobId, identity),
                identity => Assert.Equal(state.JobId, identity));
        }
        finally
        {
            pending.TrySetResult(null);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task SetDisabledAsync_WhenPolicyChangesDuringAuthorization_ShouldKeepClickedRevisionAndReturnConflict()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        var state = boundary.CreateDetail();
        var authorize = Signal<bool>();
        try
        {
            await ObserveAsync(state.InitializeAsync());
            var clicked = state.Summary!.Definition;
            boundary.Access.Authorize = _ => boundary.Access.Calls == 2 ? authorize.Task : Task.FromResult(true);
            var mutation = state.SetDisabledAsync(true);
            await boundary.Store.UpdatePolicyAsync(SchedulerStateTestBoundary.Scope, clicked.OwnerKey, clicked.Declaration.JobKey,
                new JobPolicyChange
                {
                    Overrides = clicked.Policy.Overrides with { MaxRetainedHistoryRecords = 0 },
                    ExpectedConcurrencyStamp = clicked.Policy.ConcurrencyStamp
                }, TestContext.Current.CancellationToken);
            await ObserveAsync(state.RefreshAsync());
            var accepted = state.Summary;
            authorize.TrySetResult(true);
            var result = await mutation.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(ResStatus.Conflict, result.Status);
            Assert.Same(accepted, state.Summary);
            Assert.False(state.Summary!.Definition.IsDisabled);
            Assert.Equal(0, state.Summary.Definition.Policy.Overrides.MaxRetainedHistoryRecords);
        }
        finally
        {
            authorize.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenRefreshAndMutationAreAdmitted_ShouldDrainBothAndQueuedRefreshBeforeCleanup()
    {
        var boundary = new SchedulerStateTestBoundary();
        var authorize = Signal<bool>();
        boundary.Access.Authorize = _ => authorize.Task;
        var state = boundary.CreateDetail();
        var first = state.InitializeAsync();
        var queued = state.RefreshAsync();
        var mutation = state.RunRecurringNowAsync();
        var retirement = state.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            authorize.TrySetResult(true);
            await ObserveAsync(first);
            // A queued refresh can observe retirement before admission or handle it inside its load body.
            try { await ObserveAsync(queued); }
            catch (OperationCanceledException) { }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(mutation));
            await ObserveAsync(retirement);
            Assert.Empty(boundary.DetailQueries);
            Assert.Null(state.Summary);
            Assert.False(state.AccessChecked);
        }
        finally
        {
            authorize.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RunRecurringNowAsync_WhenAuthorizationIsRevoked_ShouldClearAllEvidenceWithoutQueueAdmission()
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateDetail();
        await ObserveAsync(state.RefreshAsync());
        Assert.NotNull(state.Analytics);
        boundary.Access.Authorized = false;
        var result = await state.RunRecurringNowAsync();
        Assert.True(result.IsFailed(out _));
        Assert.False(state.IsAuthorized);
        Assert.Null(state.Summary);
        Assert.Null(state.Analytics);
        Assert.Empty(state.RunningActivity.Items);
        Assert.Empty(state.RecentFailureActivity.Items);
        Assert.Null(state.ObservedAtUtc);
        var durable = await boundary.Store.GetOperationalSummaryAsync(SchedulerStateTestBoundary.Scope,
            new JobId(SchedulerStateTestBoundary.Owner, SchedulerStateTestBoundary.Job), TestContext.Current.CancellationToken);
        Assert.Equal(0, durable!.QueuedExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationAsync_WhenAuthorizationThrows_ShouldReturnLocalFailureWithoutChangingStoredPolicyOrQueue(bool runNow)
    {
        var boundary = new SchedulerStateTestBoundary();
        await boundary.SeedAsync(TestContext.Current.CancellationToken);
        await using var state = boundary.CreateDetail();
        await ObserveAsync(state.InitializeAsync());
        var identity = state.JobId;
        boundary.Access.Authorize = _ => Task.FromException<bool>(new IOException("Definition access unavailable."));

        if (runNow)
        {
            var result = await state.RunRecurringNowAsync();
            Assert.True(result.IsFailed(out var error));
            Assert.Equal("Definition access unavailable.", error.Message);
        }
        else
        {
            var result = await state.SetDisabledAsync(true);
            Assert.True(result.IsFailed(out var error));
            Assert.Equal("Definition access unavailable.", error.Message);
        }
        Assert.Equal("Definition access unavailable.", state.Error);
        Assert.True(state.AccessChecked);
        Assert.False(state.IsAuthorized);
        Assert.False(state.IsMutating);
        Assert.Null(state.Summary);
        Assert.Null(state.Analytics);
        Assert.Empty(state.RunningActivity.Items);
        Assert.Empty(state.RecentFailureActivity.Items);
        Assert.DoesNotContain(boundary.Boundary.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is "UpdatePolicyAsync" or "RunRecurringNowAsync");
        var durable = await boundary.Store.GetOperationalSummaryAsync(SchedulerStateTestBoundary.Scope,
            identity, TestContext.Current.CancellationToken);
        Assert.NotNull(durable);
        Assert.False(durable.Definition.IsDisabled);
        Assert.Equal(0, durable.QueuedExecutionCount);
        Assert.Single(boundary.DetailQueries);
    }

    private static Task ObserveAsync(Task task) => task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
