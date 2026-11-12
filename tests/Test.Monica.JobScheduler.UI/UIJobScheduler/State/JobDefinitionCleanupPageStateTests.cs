using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.Modules;
using Monica.Testing.Localization;
using NSubstitute;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>Exercises authorization and asynchronous ownership without creating a renderer.</summary>
public sealed class JobDefinitionCleanupPageStateTests
{
    private const string SCOPE = "cleanup-state", OWNER = " owner/运营?% ";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preview_WhenAuthorizationIsDelayed_ShouldFreezeExactIdentities()
    {
        var harness = await Harness.CreateAsync();
        await using var state = harness.State;
        await state.RefreshAsync(new());
        var authorization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Access.View = _ => authorization.Task;
        JobId[] ids = [new(OWNER, "first")];
        var pending = state.PreviewAsync(ids, false);
        ids[0] = new(OWNER, "second");
        authorization.SetResult(true);
        var plan = await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.NotNull(plan);
        Assert.Equal("first", Assert.Single(plan.Items).Id.JobKey);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Remove_WhenAuthorizationIsLost_ShouldClearEvidenceAndNeverCallMutation(bool throws)
    {
        var harness = await Harness.CreateAsync();
        await using var state = harness.State;
        await state.RefreshAsync(new());
        state.SelectReadyPage();
        var plan = await state.PreviewAsync(state.Selected.ToArray(), false);
        Assert.NotNull(plan);
        harness.Access.View = _ => throws
            ? Task.FromException<bool>(new IOException("Access unavailable")) : Task.FromResult(false);
        Assert.Null(await state.RemoveAsync(plan));
        Assert.False(state.IsAuthorized);
        Assert.Null(state.Snapshot);
        Assert.Empty(state.Selected);
        Assert.DoesNotContain(harness.Boundary.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IJobSchedulerStore.RemoveDefinitionsAsync));
        Assert.NotNull(await harness.Store.GetDefinitionAsync(SCOPE, OWNER, "first", Token));
    }

    [Fact]
    public async Task Inspect_WhenMaintenancePolicyIsDenied_ShouldAllowEvidenceWithoutRemovalPreview()
    {
        var harness = await Harness.CreateAsync();
        await using var state = harness.State;
        harness.Access.Maintenance = false;
        await state.RefreshAsync(new());
        await state.InspectAsync(new(OWNER, "first"));
        Assert.NotNull(state.Snapshot);
        Assert.NotNull(state.Inspection);
        Assert.False(state.CanWrite);
        Assert.Null(await state.PreviewAsync([new(OWNER, "first")], true));
    }

    [Fact]
    public async Task Refresh_WhenAChangedQueryFails_ShouldNotRelabelOldRowsAsNewEvidence()
    {
        var harness = await Harness.CreateAsync();
        await using var state = harness.State;
        await state.RefreshAsync(new());
        state.SelectReadyPage();
        harness.Boundary.QueryDefinitionCleanupAsync(Arg.Any<string>(), Arg.Any<JobDefinitionCleanupQuery>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JobDefinitionCleanupSnapshot>(new IOException("Store unavailable")));
        await state.RefreshAsync(new());
        Assert.NotNull(state.Snapshot);
        Assert.True(state.IsStale);
        Assert.False(state.CanWrite);
        await state.RefreshAsync(new() { SearchText = "other" });
        Assert.Null(state.Snapshot);
        Assert.Equal(2, state.Selected.Count);
    }

    [Fact]
    public async Task Dispose_WhenAReadIgnoresCancellation_ShouldDrainItWithoutPublishingLateEvidence()
    {
        var harness = await Harness.CreateAsync();
        var completion = new TaskCompletionSource<JobDefinitionCleanupSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Boundary.QueryDefinitionCleanupAsync(Arg.Any<string>(), Arg.Any<JobDefinitionCleanupQuery>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(completion.Task);
        var pending = harness.State.RefreshAsync(new());
        var retirement = harness.State.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        completion.SetResult(await harness.Store.QueryDefinitionCleanupAsync(SCOPE, new(), TimeSpan.FromMinutes(1), Token));
        await Task.WhenAll(pending, retirement).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Null(harness.State.Snapshot);
        await harness.State.DisposeAsync();
    }

    [Fact]
    public async Task Maintenance_WhenManualModeIsActive_ShouldNotReadOrMutateTheSharedStore()
    {
        var harness = await Harness.CreateAsync(false);
        await using var state = harness.State;
        await state.CheckAccessAsync();
        await state.RefreshAsync(new());
        Assert.True(state.IsAuthorized);
        Assert.False(state.CanWrite);
        Assert.Null(await state.PreviewAsync([new(OWNER, "first")], true));
        var result = await harness.Facade.QueryDefinitionCleanupAsync(new(), Token);
        Assert.True(result.IsFailure());
        Assert.Empty(harness.Boundary.ReceivedCalls());
    }

    private sealed class Harness
    {
        internal InMemoryJobSchedulerStore Store { get; } = new(TimeProvider.System);
        internal IJobSchedulerStore Boundary { get; } = Substitute.For<IJobSchedulerStore>();
        internal TestAccess Access { get; } = new();
        internal JobSchedulerFacade Facade { get; }
        internal JobDefinitionCleanupPageState State { get; }

        private Harness(bool autonomous)
        {
            Boundary.QueryDefinitionCleanupAsync(Arg.Any<string>(), Arg.Any<JobDefinitionCleanupQuery>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(call => Store.QueryDefinitionCleanupAsync(call.ArgAt<string>(0), call.ArgAt<JobDefinitionCleanupQuery>(1), call.ArgAt<TimeSpan>(2), call.ArgAt<CancellationToken>(3)));
            Boundary.PreviewDefinitionCleanupAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<JobId>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(call => Store.PreviewDefinitionCleanupAsync(call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<JobId>>(1), call.ArgAt<bool>(2), call.ArgAt<CancellationToken>(3)));
            Boundary.RemoveDefinitionsAsync(Arg.Any<string>(), Arg.Any<JobDefinitionCleanupRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Store.RemoveDefinitionsAsync(call.ArgAt<string>(0), call.ArgAt<JobDefinitionCleanupRequest>(1), call.ArgAt<CancellationToken>(2)));
            Facade = new(Boundary, Options.Create(new ModuleJobSchedulerOption { SchedulerScopeKey = SCOPE, AutonomousWork = autonomous }),
                TimeProvider.System, new JobSchedulerRuntimeState(), NullLogger<JobSchedulerFacade>.Instance);
            State = new(Facade, Access, autonomous, new EchoStringLocalizer<JobSchedulerResource>());
        }

        internal static async Task<Harness> CreateAsync(bool autonomous = true)
        {
            var harness = new Harness(autonomous);
            await harness.Store.SyncOwnerSnapshotAsync(new(SCOPE, OWNER,
                [new() { JobKey = "first", JobName = "First", JobType = JobType.Triggered, JobArgsKey = "Args" }, new() { JobKey = "second", JobName = "Second", JobType = JobType.Triggered, JobArgsKey = "Args" }]), Token);
            await harness.Store.SyncOwnerSnapshotAsync(new(SCOPE, OWNER, [])
                { ExpectedPreviousManifestHash = await harness.Store.GetOwnerManifestHashAsync(SCOPE, OWNER, Token) }, Token);
            return harness;
        }
    }

    private sealed class TestAccess : IJobSchedulerUiAccess
    {
        internal Func<CancellationToken, Task<bool>> View { get; set; } = _ => Task.FromResult(true);
        internal bool Maintenance { get; set; } = true;
        public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) => View(cancellationToken);
        public Task<bool> IsDefinitionMaintenanceAuthorizedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Maintenance);
    }
}
