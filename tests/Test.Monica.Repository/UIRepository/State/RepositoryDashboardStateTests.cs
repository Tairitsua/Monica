using AwesomeAssertions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.UI.UIRepository.State;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.UIRepository.State.Support;
using Xunit;

namespace Test.Monica.Repository.UIRepository.State;

public sealed class RepositoryDashboardStateTests
{
    private static readonly RepositoryDashboardScenarioFactory Factory = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenActivityRefreshFails_ShouldRetainTheSampleAcrossRoutes()
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        var id = diagnostics.First.ContextId;
        var sample = diagnostics.Activity(id) with
        {
            Summary = new RepositoryActivitySummary { TotalSessions = 1 }
        };
        diagnostics.ActivityRead = (_, _) => Task.FromResult(sample);
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();

        await state.PrepareWorkspaceAsync(null, readActivity: false, refresh: false, revealConnectionStrings: null, Token);
        diagnostics.DetailCalls.Should().BeEmpty();

        await state.PrepareWorkspaceAsync(id, readActivity: true, refresh: false, revealConnectionStrings: null, Token);
        var capturedAt = state.DetailsCapturedAt;
        state.FindSampledActivity(id)!.TotalSessions.Should().Be(1);

        diagnostics.ActivityRead = (_, _) => Task.FromResult(new RepositoryActivityResult
        {
            Registration = diagnostics.First,
            Support = RepositoryActivitySupport.Failed,
            Message = "The provider became unavailable."
        });
        await state.PrepareWorkspaceAsync(id, readActivity: true, refresh: true, revealConnectionStrings: null, Token);
        await state.PrepareWorkspaceAsync(id, readActivity: false, refresh: false, revealConnectionStrings: null, Token);
        await state.PrepareWorkspaceAsync(id, readActivity: true, refresh: false, revealConnectionStrings: null, Token);

        state.RetainedActivityError.Should().Be("The provider became unavailable.");
        state.Activity!.Rows.Should().Equal(sample.Rows);
        state.DetailsCapturedAt.Should().Be(capturedAt);
        diagnostics.DetailCalls.Should().Equal("activity:" + id, "activity:" + id);
    }

    [Fact]
    public async Task SelectContextAsync_WhenSelectionsOverlap_ShouldSerializeAndNeverPublishAnotherContextsEvidence()
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        diagnostics.DetailCalls.Clear();
        var firstEntered = Signal();
        var firstRelease = Signal();
        var secondEntered = Signal();
        var secondRelease = Signal();
        diagnostics.ActivityRead = async (id, _) =>
        {
            var entered = id == diagnostics.First.ContextId ? firstEntered : secondEntered;
            var release = id == diagnostics.First.ContextId ? firstRelease : secondRelease;
            entered.TrySetResult(true);
            await release.Task;
            return diagnostics.Activity(id);
        };

        var firstSelection = state.SelectContextAsync(diagnostics.First.ContextId, Token);
        Task? secondSelection = null;
        try
        {
            await AwaitAsync(firstEntered.Task);
            secondSelection = state.SelectContextAsync(diagnostics.Second.ContextId, Token);
            secondEntered.Task.IsCompleted.Should().BeFalse();
            state.SelectedContext!.Registration.ContextId.Should().Be(diagnostics.First.ContextId);
            state.Activity.Should().BeNull();
            state.MigrationStatus.Should().BeNull();
            state.DetailsCapturedAt.Should().BeNull();

            firstRelease.TrySetResult(true);
            await AwaitAsync(secondEntered.Task);
            state.SelectedContext!.Registration.ContextId.Should().Be(diagnostics.Second.ContextId);
            state.Activity.Should().BeNull("the first context's evidence must be cleared before the second read");
            state.MigrationStatus.Should().BeNull();
            state.DetailsCapturedAt.Should().BeNull();
            secondRelease.TrySetResult(true);
            await AwaitAsync(Task.WhenAll(firstSelection, secondSelection));

            AssertEvidence(state, diagnostics.Second.ContextId);
            diagnostics.DetailCalls.Should().Equal(
                "migrations:" + diagnostics.First.ContextId,
                "activity:" + diagnostics.First.ContextId,
                "migrations:" + diagnostics.Second.ContextId,
                "activity:" + diagnostics.Second.ContextId);
            state.IsLoadingDetails.Should().BeFalse();
        }
        finally
        {
            firstRelease.TrySetResult(true);
            secondRelease.TrySetResult(true);
            await AwaitAsync(firstSelection);
            if (secondSelection is not null) await AwaitAsync(secondSelection);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectContextAsync_WhenExactIdIsAbsent_ShouldClearDetailsWithoutFallingBack(bool useShortName)
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        var captured = state.ContextsCapturedAt;
        diagnostics.DetailCalls.Clear();
        var absent = useShortName ? diagnostics.First.DbContextName : diagnostics.First.ContextId.ToLowerInvariant();

        await state.SelectContextAsync(absent, Token);

        state.SelectedContext.Should().BeNull();
        state.Activity.Should().BeNull();
        state.MigrationStatus.Should().BeNull();
        state.DetailsCapturedAt.Should().BeNull();
        state.Contexts.Should().HaveCount(2);
        state.ContextsCapturedAt.Should().Be(captured);
        diagnostics.DetailCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshContextsAsync_WhenCancellationPrecedesFacadeCompletion_ShouldRejectLateSuccessOrCaughtFailure(bool throwCancellation)
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        var contexts = state.Contexts;
        var activity = state.Activity;
        var migrations = state.MigrationStatus;
        var contextsCaptured = state.ContextsCapturedAt;
        var detailsCaptured = state.DetailsCapturedAt;
        var entered = Signal();
        var release = Signal();
        diagnostics.RegistryRead = async (_, cancellation) =>
        {
            entered.TrySetResult(true);
            // Deliberately emulate a provider that completes after cancellation. The real facade catches
            // the exception variant, so the state must check its token before adopting that envelope.
            await release.Task;
            if (throwCancellation) throw new OperationCanceledException(cancellation);
            return [diagnostics.Snapshot(diagnostics.Second)];
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresh = state.RefreshContextsAsync(cancellationToken: cancellation.Token);
        try
        {
            await AwaitAsync(entered.Task);
            await cancellation.CancelAsync();
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(refresh));

            state.Contexts.Should().BeSameAs(contexts);
            state.SelectedContext!.Registration.ContextId.Should().Be(diagnostics.First.ContextId);
            state.Activity.Should().BeSameAs(activity);
            state.MigrationStatus.Should().BeSameAs(migrations);
            state.ContextsCapturedAt.Should().Be(contextsCaptured);
            state.DetailsCapturedAt.Should().Be(detailsCaptured);
            state.ErrorMessage.Should().BeNull();
            state.IsLoadingContexts.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(refresh));
        }

        diagnostics.RegistryRead = (_, _) => Task.FromResult<IReadOnlyList<RepositoryDbContextSnapshot>>([diagnostics.Snapshot(diagnostics.Second)]);
        await state.RefreshContextsAsync(cancellationToken: Token);
        AssertEvidence(state, diagnostics.Second.ContextId);
    }

    [Fact]
    public async Task SelectContextAsync_WhenCanceledDuringActivityRead_ShouldRejectLateDetailsAndReleaseItsGate()
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        var entered = Signal();
        var release = Signal();
        diagnostics.ActivityRead = async (id, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return diagnostics.Activity(id);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var selection = state.SelectContextAsync(diagnostics.Second.ContextId, cancellation.Token);
        try
        {
            await AwaitAsync(entered.Task);
            await cancellation.CancelAsync();
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(selection));

            state.SelectedContext!.Registration.ContextId.Should().Be(diagnostics.Second.ContextId);
            state.Activity.Should().BeNull();
            state.MigrationStatus.Should().BeNull();
            state.DetailsCapturedAt.Should().BeNull();
            state.IsLoadingDetails.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(selection));
        }
        diagnostics.ActivityRead = (id, _) => Task.FromResult(diagnostics.Activity(id));
        await state.SelectContextAsync(diagnostics.First.ContextId, Token);
        AssertEvidence(state, diagnostics.First.ContextId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateSelectedMigrationAsync_WhenQueuedBehindASelectionChangingRefresh_ShouldUseTheIdFrozenAtAdmission(bool confirmedContextRemoved)
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        var entered = Signal();
        var release = Signal();
        diagnostics.RegistryRead = async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            if (confirmedContextRemoved) return [diagnostics.Snapshot(diagnostics.Second)];
            return [diagnostics.Snapshot(diagnostics.Second), diagnostics.Snapshot(diagnostics.First)];
        };
        var refresh = state.RefreshContextsAsync(keepSelection: false, cancellationToken: Token);
        Task<RepositoryMigrationUpdateResult?>? update = null;
        try
        {
            await AwaitAsync(entered.Task);
            state.SelectedContext!.Registration.ContextId.Should().Be(diagnostics.First.ContextId);
            update = state.UpdateSelectedMigrationAsync(Token);
            diagnostics.UpdatedContextIds.Should().BeEmpty();
            release.TrySetResult(true);
            await AwaitAsync(refresh);
            var outcome = await AwaitAsync(update);

            if (confirmedContextRemoved)
            {
                outcome.Should().BeNull();
                diagnostics.UpdatedContextIds.Should().BeEmpty("the confirmed context disappeared before admission");
                diagnostics.CommandTimeouts.Should().BeEmpty();
            }
            else
            {
                outcome.Should().NotBeNull();
                outcome!.Registration.ContextId.Should().Be(diagnostics.First.ContextId);
                diagnostics.UpdatedContextIds.Should().Equal(diagnostics.First.ContextId);
                diagnostics.CommandTimeouts.Should().Equal(TimeSpan.FromSeconds(17));
            }
            AssertEvidence(state, diagnostics.Second.ContextId);
        }
        finally
        {
            release.TrySetResult(true);
            await AwaitAsync(refresh);
            if (update is not null) await AwaitAsync(update);
        }
    }

    [Fact]
    public async Task UpdateMigrationAsync_WhenConfirmedIdDoesNotMatchExactly_ShouldDenyAdmissionInsteadOfUpdatingSelection()
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);

        var outcome = await state.UpdateMigrationAsync(diagnostics.First.ContextId.ToLowerInvariant(), Token);

        outcome.Should().BeNull();
        diagnostics.UpdatedContextIds.Should().BeEmpty();
        state.UpdateResults.Should().BeEmpty();
        AssertEvidence(state, diagnostics.First.ContextId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateMigrationAsync_WhenCommandSucceedsButRegistryRefreshFails_ShouldPreserveSuccessSeparatelyFromReadError(bool bulk)
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        var captured = state.ContextsCapturedAt;
        var oldContexts = state.Contexts;
        diagnostics.RegistryRead = (_, _) => throw new InvalidOperationException("Registry unavailable after the provider committed.");

        if (bulk)
        {
            var outcomes = await state.UpdateAllPendingMigrationsAsync(Token);
            outcomes.Should().HaveCount(2).And.OnlyContain(outcome => outcome.Succeeded);
            diagnostics.BulkUpdateCalls.Should().Be(1);
        }
        else
        {
            var outcome = await state.UpdateMigrationAsync(diagnostics.First.ContextId, Token);
            outcome.Should().NotBeNull();
            outcome!.Succeeded.Should().BeTrue();
            diagnostics.UpdatedContextIds.Should().Equal(diagnostics.First.ContextId);
        }

        state.UpdateResults.Should().HaveCount(bulk ? 2 : 1).And.OnlyContain(outcome => outcome.Succeeded);
        state.MigrationCommandError.Should().BeNull("the command completed even though its follow-up observation failed");
        state.ErrorMessage.Should().Contain("Registry unavailable after the provider committed.");
        state.Contexts.Should().BeSameAs(oldContexts);
        state.ContextsCapturedAt.Should().Be(captured);
        state.IsUpdatingMigrations.Should().BeFalse();
        state.IsLoadingContexts.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateMigrationAsync_WhenFacadeCatchesCommandFailure_ShouldClearPreviousOutcomesAndRecordCommandError(bool bulk)
    {
        var diagnostics = new ControlledRepositoryDiagnostics();
        await using var application = await CreateAsync(diagnostics);
        await using var scope = application.CreateScope(Token);
        var state = scope.Resolve<RepositoryDashboardState>();
        await state.InitializeAsync(Token);
        await state.UpdateMigrationAsync(diagnostics.First.ContextId, Token);
        state.UpdateResults.Should().ContainSingle();
        diagnostics.SelectedUpdate = (_, _, _) => throw new InvalidOperationException("Migration provider unavailable.");
        diagnostics.BulkUpdate = (_, _) => throw new InvalidOperationException("Migration provider unavailable.");

        if (bulk) (await state.UpdateAllPendingMigrationsAsync(Token)).Should().BeEmpty();
        else (await state.UpdateMigrationAsync(diagnostics.First.ContextId, Token)).Should().BeNull();

        state.UpdateResults.Should().BeEmpty();
        state.MigrationCommandError.Should().Contain("Migration provider unavailable.");
        state.ErrorMessage.Should().Be(state.MigrationCommandError);
        state.IsUpdatingMigrations.Should().BeFalse();
    }

    private static Task<MonicaTestApplication> CreateAsync(ControlledRepositoryDiagnostics diagnostics) =>
        Factory.CreateAsync(scenario => scenario.With<IRepositoryDbContextDiagnosticsService>(diagnostics), Token);

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task AwaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static Task<T> AwaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);

    private static void AssertEvidence(RepositoryDashboardState state, string contextId)
    {
        state.SelectedContext!.Registration.ContextId.Should().Be(contextId);
        state.MigrationStatus!.Registration.ContextId.Should().Be(contextId);
        state.Activity!.Registration.ContextId.Should().Be(contextId);
        state.Activity.Rows.Should().ContainSingle().Which.Query.Should().Be("evidence:" + contextId);
        state.DetailsCapturedAt.Should().NotBeNull();
    }
}
