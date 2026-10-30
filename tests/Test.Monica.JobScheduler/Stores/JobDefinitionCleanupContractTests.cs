using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Locker.Leases;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

public sealed class JobDefinitionCleanupContractTests
{
    private static readonly DateTimeOffset NOW = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false), InlineData(true)]
    public async Task Query_WhenPagingCandidates_ShouldCountAllStatesAndNeverInferAbsenceFromAge(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("ready"),
            StoreFixture.TriggeredDeclaration("queued"), StoreFixture.TriggeredDeclaration("running"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "queued");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "running");
        await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker", ["running"]);
        await fixture.SyncAsync(StoreFixture.OWNER_A);
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("still-published"));
        fixture.Time.Advance(TimeSpan.FromHours(1));

        var snapshot = await fixture.Store.QueryDefinitionCleanupAsync(fixture.Scope,
            new JobDefinitionCleanupQuery { State = JobDefinitionCleanupState.Ready, PageSize = 1 }, TimeSpan.FromMinutes(1), Token);
        snapshot.Items.Should().ContainSingle(item => item.Id.JobKey == "ready");
        snapshot.TotalCount.Should().Be(1);
        snapshot.ScopeDefinitionCount.Should().Be(4);
        snapshot.Counts.Values.Should().OnlyContain(count => count == 1);
        snapshot.Owners.Should().Equal(StoreFixture.OWNER_A, StoreFixture.OWNER_B);
        var preview = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope,
            [new(StoreFixture.OWNER_B, "still-published")], true, Token);
        preview.Items[0].State.Should().Be(JobDefinitionCleanupState.OwnerUnconfirmed);
        preview.Eligible.Should().BeEmpty();
        var otherScope = await fixture.Store.QueryDefinitionCleanupAsync("other-scope", new(), TimeSpan.FromMinutes(1), Token);
        otherScope.TotalCount.Should().Be(0);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Remove_WhenQueueCancellationIsExplicit_ShouldPreserveHistoryAndLeaveRenamedDefinitionIndependent(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("old-name"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "old-name", instanceId: "completed");
        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker", ["old-name"])).Single();
        await fixture.Store.CompleteAttemptAsync(new() { LeaseKey = lease.LeaseKey, Outcome = JobAttemptOutcome.Succeeded }, Token);
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "old-name", instanceId: "queued");
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("new-name"));
        var hash = await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, Token);
        JobId[] ids = [new(StoreFixture.OWNER_A, "old-name"), new(StoreFixture.OWNER_A, "new-name")];
        var protectedPlan = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope, ids, false, Token);
        protectedPlan.Eligible.Should().BeEmpty();
        var plan = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope, ids, true, Token);
        plan.Eligible.Should().ContainSingle(item => item.Id.JobKey == "old-name");
        plan.Items[0].Definition!.AbsentSinceUtc.Should().Be(NOW.AddMinutes(1));
        var applied = await fixture.Store.RemoveDefinitionsAsync(fixture.Scope, plan.ToRequest(), Token);
        applied.Status.Should().Be(JobDefinitionMaintenanceStatus.Applied);
        applied.CancelledCount.Should().Be(1);
        applied.RetainedExecutionCount.Should().Be(2);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "old-name", Token)).Should().BeNull();
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "new-name", Token))!.IsPresent.Should().BeTrue();
        (await fixture.Store.GetExecutionAsync(fixture.Scope, "completed", Token))!.State.Should().Be(JobExecutionState.Succeeded);
        var cancelled = (await fixture.Store.GetExecutionAsync(fixture.Scope, "queued", Token))!;
        cancelled.State.Should().Be(JobExecutionState.Cancelled);
        cancelled.History.Should().NotBeEmpty();
        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, Token)).Should().Be(hash);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("new-name"));
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "old-name", Token)).Should().BeNull();
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Remove_WhenOneReviewedTargetIsRepublished_ShouldRejectTheWholeSelection(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("first"), StoreFixture.TriggeredDeclaration("second"));
        await fixture.SyncAsync(StoreFixture.OWNER_A);
        var plan = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope,
            [new(StoreFixture.OWNER_A, "first"), new(StoreFixture.OWNER_A, "second")], false, Token);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("second"));
        var outcome = await fixture.Store.RemoveDefinitionsAsync(fixture.Scope, plan.ToRequest(), Token);
        outcome.Status.Should().Be(JobDefinitionMaintenanceStatus.Changed);
        outcome.Definitions.Should().BeEmpty();
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "first", Token)).Should().NotBeNull();
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Remove_WhenRunningLeaseExpiresWithoutRecovery_ShouldStillBlockRemoval(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("active"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "active");
        await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker", ["active"]);
        await fixture.SyncAsync(StoreFixture.OWNER_A);
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        var plan = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope, [new(StoreFixture.OWNER_A, "active")], true, Token);
        var item = plan.Items.Single();
        item.State.Should().Be(JobDefinitionCleanupState.Running);
        item.ActiveLeaseCount.Should().BeGreaterThan(0);
        var attempt = await fixture.Store.RemoveDefinitionsAsync(fixture.Scope,
            new() { Targets = [new(item.Id, item.Stamp)], CancelQueued = true }, Token);
        attempt.Status.Should().Be(JobDefinitionMaintenanceStatus.Blocked);
        (await fixture.Store.PreviewOwnerRetirementAsync(fixture.Scope, StoreFixture.OWNER_A, Token)).CanRetire.Should().BeFalse();
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RetireOwner_WhenPublisherStops_ShouldFenceFuturePublicationAndPreserveQueuedWork(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("retired"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "retired", instanceId: "retained-queue");
        var leases = fixture.Store.CoordinatorLeases;
        var grant = (await leases.TryAcquireAsync(new(fixture.Scope, "jobscheduler:owner:" + StoreFixture.OWNER_A),
            "publisher", TimeSpan.FromMinutes(2), Token))!;
        var active = await fixture.Store.PreviewOwnerRetirementAsync(fixture.Scope, StoreFixture.OWNER_A, Token);
        active.HasActivePublisher.Should().BeTrue();
        (await fixture.Store.RetireOwnerAsync(fixture.Scope, StoreFixture.OWNER_A, active.Stamp, Token)).Status.Should().Be(JobDefinitionMaintenanceStatus.Blocked);
        await leases.TryReleaseAsync(grant.Identity, Token);
        var stopped = await fixture.Store.PreviewOwnerRetirementAsync(fixture.Scope, StoreFixture.OWNER_A, Token);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("retired"));
        (await fixture.Store.RetireOwnerAsync(fixture.Scope, StoreFixture.OWNER_A, stopped.Stamp, Token)).Status.Should().Be(JobDefinitionMaintenanceStatus.Changed);
        var fresh = await fixture.Store.PreviewOwnerRetirementAsync(fixture.Scope, StoreFixture.OWNER_A, Token);
        (await fixture.Store.RetireOwnerAsync(fixture.Scope, StoreFixture.OWNER_A, fresh.Stamp, Token)).Status.Should().Be(JobDefinitionMaintenanceStatus.Applied);
        (await fixture.Store.GetExecutionAsync(fixture.Scope, "retained-queue", Token))!.State.Should().Be(JobExecutionState.Queued);
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "stale-worker", ["retired"])).Should().BeEmpty();
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "retired", Token))!.AbsentSinceUtc.Should().Be(NOW.AddSeconds(1));
        Func<Task> republish = () => fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("retired"));
        await republish.Should().ThrowAsync<InvalidOperationException>();
        Func<Task> replacement = () => fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("replacement"));
        await replacement.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Remove_WhenQueueChangesAfterPreview_ShouldRequireANewReviewWithoutDeletingAnything(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("queued"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "queued", instanceId: "changed");
        await fixture.SyncAsync(StoreFixture.OWNER_A);
        var plan = await fixture.Store.PreviewDefinitionCleanupAsync(fixture.Scope, [new(StoreFixture.OWNER_A, "queued")], true, Token);
        await fixture.Store.RequestCancellationAsync(fixture.Scope, "changed", cancellationToken: Token);
        (await fixture.Store.RemoveDefinitionsAsync(fixture.Scope, plan.ToRequest(), Token)).Status.Should().Be(JobDefinitionMaintenanceStatus.Changed);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "queued", Token)).Should().NotBeNull();
    }
}
