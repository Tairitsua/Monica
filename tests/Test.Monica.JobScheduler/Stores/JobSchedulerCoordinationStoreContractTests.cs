using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>
/// Verifies manifest replacement and coordinator fencing through the public store boundary on both providers.
/// </summary>
public sealed class JobSchedulerCoordinationStoreContractTests
{
    private static readonly DateTimeOffset NOW = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string OWNER_COORDINATOR = "owner:" + StoreFixture.OWNER_A;
    private const string MAINTENANCE_COORDINATOR = "maintenance";

    [Fact]
    public void ComputeManifestHash_WhenDeclarationOrderAndNormalizedTextMatch_ShouldReturnTheSameHash()
    {
        var alpha = StoreFixture.TriggeredDeclaration("jobs.alpha") with
        {
            JobName = "Alpha",
            Description = "The alpha job"
        };
        var beta = StoreFixture.RecurringDeclaration("jobs.beta");
        var first = Snapshot(alpha, beta);
        var reordered = Snapshot(beta, alpha with { JobName = " Alpha ", Description = " The alpha job " });

        reordered.ComputeManifestHash().Should().Be(first.ComputeManifestHash());
        Snapshot(alpha with { RetryCount = 1 }, beta).ComputeManifestHash().Should().NotBe(first.ComputeManifestHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshotAsync_WhenTheManifestHashMatches_ShouldOnlyRefreshOwnerLiveness(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var snapshot = Snapshot(StoreFixture.TriggeredDeclaration("jobs.alpha"),
            StoreFixture.RecurringDeclaration("jobs.beta"));
        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, token)).Should().BeNull();
        var first = await fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        first.ContentChanged.Should().BeTrue();
        first.ManifestHash.Should().Be(snapshot.ComputeManifestHash());
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token);
        definition.Should().NotBeNull();
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.beta");

        fixture.Time.Advance(TimeSpan.FromSeconds(15));
        var repeated = await fixture.Store.SyncOwnerSnapshotAsync(snapshot with
        {
            Declarations = snapshot.Declarations.Reverse().ToArray()
        }, token);

        repeated.ContentChanged.Should().BeFalse();
        repeated.ManifestHash.Should().Be(first.ManifestHash);
        repeated.PresentCount.Should().Be(2);
        repeated.MarkedAbsentCount.Should().Be(0);
        repeated.ObservedAtUtc.Should().Be(NOW.AddSeconds(15));
        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, token))
            .Should().Be(first.ManifestHash);
        var current = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token);
        current!.Declaration.Should().Be(definition!.Declaration);
        current.Policy.Should().Be(definition.Policy);
        var cursorAfter = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.beta");
        cursorAfter.Version.Should().Be(cursor.Version);
        cursorAfter.NextOccurrenceUtc.Should().Be(cursor.NextOccurrenceUtc);
        var owners = await fixture.Store.QueryOwnerSummariesAsync(fixture.Scope, token);
        owners.Should().ContainSingle().Which.LastObservedAtUtc.Should().Be(repeated.ObservedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshotAsync_WhenDeclarationsChangeWithoutAnExpectedHash_ShouldRejectAllChanges(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var original = Snapshot(StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));
        var first = await fixture.Store.SyncOwnerSnapshotAsync(original, token);
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        var changed = Snapshot(original.Declarations[0] with { JobName = "Unauthorized replacement" },
            StoreFixture.TriggeredDeclaration("jobs.gamma"));

        Func<Task> publish = () => fixture.Store.SyncOwnerSnapshotAsync(changed, token);
        await publish.Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, token))
            .Should().Be(first.ManifestHash);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token))
            .Should().Be(definition);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", token))!
            .IsPresent.Should().BeTrue();
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.gamma", token))
            .Should().BeNull();
        var cursorAfter = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        cursorAfter.Version.Should().Be(cursor.Version);
        cursorAfter.NextOccurrenceUtc.Should().Be(cursor.NextOccurrenceUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshotAsync_WhenTheExactPreviousHashIsAuthorized_ShouldReplaceAndRejectStalePublishers(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var original = Snapshot(StoreFixture.TriggeredDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));
        var first = await fixture.Store.SyncOwnerSnapshotAsync(original, token);
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token);
        var policy = await fixture.UpdatePolicyAsync(StoreFixture.OWNER_A, "jobs.alpha", definition!.Policy,
            new JobPolicyOverrides { MaxConcurrencyOverride = 4 });
        var replacement = Snapshot(original.Declarations[0] with { JobName = "Accepted replacement" },
            StoreFixture.TriggeredDeclaration("jobs.gamma")) with
        {
            ExpectedPreviousManifestHash = first.ManifestHash
        };
        fixture.Time.Advance(TimeSpan.FromSeconds(5));

        var replaced = await fixture.Store.SyncOwnerSnapshotAsync(replacement, token);

        replaced.ContentChanged.Should().BeTrue();
        replaced.ManifestHash.Should().Be(replacement.ComputeManifestHash()).And.NotBe(first.ManifestHash);
        replaced.MarkedAbsentCount.Should().Be(1);
        var current = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token);
        current!.Declaration.JobName.Should().Be("Accepted replacement");
        current.Policy.Should().Be(policy);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", token))!
            .IsPresent.Should().BeFalse();
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.gamma", token))!
            .IsPresent.Should().BeTrue();

        Func<Task> stalePublisher = () => fixture.Store.SyncOwnerSnapshotAsync(original with
        {
            ExpectedPreviousManifestHash = first.ManifestHash
        }, token);
        await stalePublisher.Should().ThrowAsync<InvalidOperationException>();
        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, token))
            .Should().Be(replaced.ManifestHash);
        (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", token))!
            .Declaration.JobName.Should().Be("Accepted replacement");

        // A same-content publisher does not need replacement authority, even when its remembered old hash is stale.
        var identical = await fixture.Store.SyncOwnerSnapshotAsync(replacement, token);
        identical.ContentChanged.Should().BeFalse();
        identical.ManifestHash.Should().Be(replaced.ManifestHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorLeases_WhenHeldRenewedAndExpired_ShouldRequireTheCurrentIdentityAndExplicitReacquisition(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var leases = fixture.Store.CoordinatorLeases;
        var resource = new LeaseResource(fixture.Scope, "jobscheduler:" + OWNER_COORDINATOR);
        var duration = TimeSpan.FromSeconds(30);
        var first = await leases.TryAcquireAsync(resource, "worker-a", duration, token);
        first.Should().NotBeNull();
        first!.ConfirmedAtUtc.Should().Be(NOW);
        first.ExpiresAtUtc.Should().Be(NOW.AddSeconds(30));
        first.Identity.TermId.Should().NotBeNullOrWhiteSpace();
        (await leases.TryAcquireAsync(resource, "worker-a", duration, token)).Should().BeNull();
        (await leases.TryRenewAsync(first.Identity with { TermId = "wrong-token" }, duration, token)).Should().BeNull();
        (await leases.TryRenewAsync(first.Identity with { HolderId = "worker-b" }, duration, token)).Should().BeNull();
        (await leases.TryAcquireAsync(new LeaseResource(fixture.Scope, "jobscheduler:owner:" + StoreFixture.OWNER_B),
            "worker-b", duration, token)).Should().NotBeNull();
        (await leases.TryAcquireAsync(new LeaseResource("other-scope", resource.Name),
            "worker-b", duration, token)).Should().NotBeNull();

        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        var renewed = await leases.TryRenewAsync(first.Identity, duration, token);
        renewed.Should().NotBeNull();
        renewed!.Identity.Should().Be(first.Identity);
        renewed.ConfirmedAtUtc.Should().Be(NOW.AddSeconds(10));
        renewed.ExpiresAtUtc.Should().Be(NOW.AddSeconds(40));
        fixture.Time.Advance(TimeSpan.FromSeconds(21));
        (await leases.TryAcquireAsync(resource, "worker-b", duration, token)).Should().BeNull();

        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        (await leases.TryRenewAsync(first.Identity, duration, token)).Should().BeNull();
        var takeover = await leases.TryAcquireAsync(resource, "worker-b", duration, token);
        takeover.Should().NotBeNull();
        takeover!.ConfirmedAtUtc.Should().Be(renewed.ExpiresAtUtc);
        takeover.Identity.HolderId.Should().Be("worker-b");
        takeover.Identity.TermId.Should().NotBe(first.Identity.TermId);
        (await leases.TryRenewAsync(first.Identity, duration, token)).Should().BeNull();
        (await leases.TryReleaseAsync(first.Identity, token)).Should().BeFalse();

        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        (await leases.TryRenewAsync(takeover.Identity, duration, token)).Should().BeNull();
        var reacquired = await leases.TryAcquireAsync(resource, "worker-b", duration, token);
        reacquired.Should().NotBeNull();
        reacquired!.Identity.TermId.Should().NotBe(takeover.Identity.TermId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSnapshotAsync_WhenReadingOneLease_ShouldIsolateItsResourceAndPreserveExpiredEvidence(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var leases = fixture.Store.CoordinatorLeases;
        var resource = new LeaseResource(fixture.Scope, "jobscheduler:owner:exact");
        await leases.TryAcquireAsync(new(resource.Scope, resource.Name + ":other"), "neighbor", TimeSpan.FromMinutes(1), token);
        await leases.TryAcquireAsync(new("other-scope", resource.Name), "other-scope", TimeSpan.FromMinutes(1), token);

        var missing = await leases.GetSnapshotAsync(resource, token);
        missing.ObservedAtUtc.Should().Be(NOW);
        missing.Leases.Should().BeEmpty();

        var grant = (await leases.TryAcquireAsync(resource, "holder", TimeSpan.FromSeconds(30), token))!;
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        var live = await leases.GetSnapshotAsync(resource, token);
        live.ObservedAtUtc.Should().Be(NOW.AddSeconds(10));
        var observation = live.Leases.Should().ContainSingle().Subject;
        observation.Resource.Should().Be(resource);
        observation.HolderId.Should().Be(grant.Identity.HolderId);
        observation.AcquiredAtUtc.Should().Be(grant.ConfirmedAtUtc);
        observation.ConfirmedAtUtc.Should().Be(grant.ConfirmedAtUtc);
        observation.ExpiresAtUtc.Should().Be(grant.ExpiresAtUtc);

        fixture.Time.Advance(TimeSpan.FromSeconds(21));
        var expired = await leases.GetSnapshotAsync(resource, token);
        expired.ObservedAtUtc.Should().Be(NOW.AddSeconds(31));
        expired.Leases.Should().ContainSingle().Which.Should().Be(observation);
        (await leases.TryRenewAsync(grant.Identity, TimeSpan.FromSeconds(30), token)).Should().BeNull();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            leases.GetSnapshotAsync(resource, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedOwnerMutations_WhenTheGrantExpiresOrIsTakenOver_ShouldRejectStaleSyncAndRecurringWork(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var grant = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:" + OWNER_COORDINATOR), "worker-a", TimeSpan.FromSeconds(30), token);
        grant.Should().NotBeNull();
        var grantKey = JobCoordinatorLeaseAdapter.ToKey(grant!.Identity);
        var snapshot = Snapshot(StoreFixture.RecurringDeclaration("jobs.alpha")) with { CoordinatorLease = grantKey };
        var published = await fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        var synchronization = new RecurringScheduleSynchronization
        {
            CoordinatorLease = grantKey,
            CursorKey = CursorKey("jobs.alpha")
        };
        var created = await fixture.Store.SynchronizeRecurringScheduleAsync(synchronization, token);
        created.Status.Should().Be(RecurringScheduleSynchronizationStatus.Created);
        var cursor = created.Cursor!;
        fixture.Time.Advance(TimeSpan.FromSeconds(31));

        Func<Task> expiredSnapshot = () => fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        await expiredSnapshot.Should().ThrowAsync<InvalidOperationException>();
        var takeover = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:" + OWNER_COORDINATOR), "worker-b", TimeSpan.FromMinutes(1), token);
        takeover.Should().NotBeNull();
        var takeoverKey = JobCoordinatorLeaseAdapter.ToKey(takeover!.Identity);
        Func<Task> staleSnapshot = () => fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        Func<Task> staleSynchronization = () => fixture.Store.SynchronizeRecurringScheduleAsync(synchronization with
        {
            HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
        }, token);
        var materialization = new RecurringOccurrenceMaterialization
        {
            CoordinatorLease = grantKey,
            CursorKey = cursor.Key,
            ExpectedVersion = cursor.Version,
            ExpectedOccurrenceUtc = cursor.NextOccurrenceUtc!.Value,
            InstanceId = "stale-coordinator-occurrence"
        };
        Func<Task> staleMaterialization = () => fixture.Store.TryMaterializeRecurringOccurrenceAsync(materialization, token);
        await staleSnapshot.Should().ThrowAsync<InvalidOperationException>();
        await staleSynchronization.Should().ThrowAsync<InvalidOperationException>();
        await staleMaterialization.Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_A, token))
            .Should().Be(published.ManifestHash);
        (await fixture.Store.GetExecutionAsync(fixture.Scope, materialization.InstanceId, token)).Should().BeNull();
        var unchanged = (await fixture.Store.GetDueRecurringSchedulesAsync(fixture.Scope, StoreFixture.OWNER_A, 10, token))
            .Should().ContainSingle().Subject;
        unchanged.Version.Should().Be(cursor.Version);
        unchanged.NextOccurrenceUtc.Should().Be(cursor.NextOccurrenceUtc);
        unchanged.IsSuspended.Should().BeFalse();

        var acceptedSnapshot = await fixture.Store.SyncOwnerSnapshotAsync(snapshot with { CoordinatorLease = takeoverKey }, token);
        acceptedSnapshot.ContentChanged.Should().BeFalse();
        var acceptedSynchronization = await fixture.Store.SynchronizeRecurringScheduleAsync(synchronization with
        {
            CoordinatorLease = takeoverKey
        }, token);
        acceptedSynchronization.Status.Should().Be(RecurringScheduleSynchronizationStatus.Unchanged);
        var acceptedMaterialization = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(materialization with
        {
            CoordinatorLease = takeoverKey,
            InstanceId = "current-coordinator-occurrence"
        }, token);
        acceptedMaterialization.Status.Should().Be(RecurringMaterializationStatus.Materialized);
        acceptedMaterialization.Execution!.InstanceId.Should().Be("current-coordinator-occurrence");
        acceptedMaterialization.Cursor!.Version.Should().BeGreaterThan(cursor.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedMutations_WhenTheGrantBelongsToAnotherPartition_ShouldRejectBeforeMutatingState(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var grant = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:" + OWNER_COORDINATOR), "owner-worker", TimeSpan.FromMinutes(1), token);
        grant.Should().NotBeNull();
        var grantKey = JobCoordinatorLeaseAdapter.ToKey(grant!.Identity);
        var declaration = StoreFixture.RecurringDeclaration("jobs.alpha");
        Func<Task> otherOwner = () => fixture.Store.SyncOwnerSnapshotAsync(
            new JobOwnerSnapshot(fixture.Scope, StoreFixture.OWNER_B, [declaration]) { CoordinatorLease = grantKey }, token);
        Func<Task> otherScope = () => fixture.Store.SyncOwnerSnapshotAsync(
            new JobOwnerSnapshot("other-scope", StoreFixture.OWNER_A, [declaration]) { CoordinatorLease = grantKey }, token);
        Func<Task> otherCursor = () => fixture.Store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
        {
            CoordinatorLease = grantKey,
            CursorKey = CursorKey("jobs.alpha") with { OwnerKey = StoreFixture.OWNER_B }
        }, token);
        Func<Task> maintenance = () => fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            CoordinatorLease = grantKey
        }, token);

        await otherOwner.Should().ThrowAsync<InvalidOperationException>();
        await otherScope.Should().ThrowAsync<InvalidOperationException>();
        await otherCursor.Should().ThrowAsync<InvalidOperationException>();
        await maintenance.Should().ThrowAsync<InvalidOperationException>();
        (await fixture.Store.GetOwnerManifestHashAsync(fixture.Scope, StoreFixture.OWNER_B, token)).Should().BeNull();
        (await fixture.Store.GetOwnerManifestHashAsync("other-scope", StoreFixture.OWNER_A, token)).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverExpiredLeasesAsync_WhenTheMaintenanceGrantIsStale_ShouldPreserveOccupancyUntilTheCurrentGrantRecovers(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.Store.SyncOwnerSnapshotAsync(Snapshot(StoreFixture.TriggeredDeclaration("jobs.alpha")), token);
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.alpha", instanceId: "expired-execution");
        var executionLease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "execution-worker", ["jobs.alpha"],
            leaseDuration: TimeSpan.FromSeconds(10))).Should().ContainSingle().Subject;
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.alpha", instanceId: "waiting-execution");
        var grant = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:" + MAINTENANCE_COORDINATOR), "maintenance-a", TimeSpan.FromSeconds(10), token);
        grant.Should().NotBeNull();
        var grantKey = JobCoordinatorLeaseAdapter.ToKey(grant!.Identity);
        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        var request = new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            CoordinatorLease = grantKey,
            MaxCount = 1
        };
        Func<Task> staleRecovery = () => fixture.Store.RecoverExpiredLeasesAsync(request, token);
        await staleRecovery.Should().ThrowAsync<InvalidOperationException>();
        var takeover = await fixture.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(fixture.Scope, "jobscheduler:" + MAINTENANCE_COORDINATOR), "maintenance-b", TimeSpan.FromSeconds(30), token);
        takeover.Should().NotBeNull();
        var takeoverKey = JobCoordinatorLeaseAdapter.ToKey(takeover!.Identity);
        await staleRecovery.Should().ThrowAsync<InvalidOperationException>();
        var beforeRecovery = await fixture.Store.GetExecutionAsync(fixture.Scope, executionLease.Execution.InstanceId, token);
        beforeRecovery!.State.Should().Be(JobExecutionState.Running);
        beforeRecovery.LeaseLossCount.Should().Be(0);
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "blocked-worker", ["jobs.alpha"]))
            .Should().BeEmpty();

        var recovered = await fixture.Store.RecoverExpiredLeasesAsync(request with { CoordinatorLease = takeoverKey }, token);
        recovered.Should().ContainSingle().Which.State.Should().Be(JobExecutionState.Queued);
        (await fixture.Store.RequestCancellationAsync(fixture.Scope, "waiting-execution", cancellationToken: token))
            .Status.Should().Be(JobCancellationStatus.Cancelled);
        var reclaimed = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "replacement-worker", ["jobs.alpha"]))
            .Should().ContainSingle().Subject;
        reclaimed.Execution.InstanceId.Should().Be(executionLease.Execution.InstanceId);
        reclaimed.LeaseKey.LeaseToken.Should().NotBe(executionLease.LeaseKey.LeaseToken);
        (await fixture.Store.RenewLeaseAsync(executionLease.LeaseKey, TimeSpan.FromSeconds(30), token))
            .Status.Should().Be(JobLeaseRenewalStatus.Lost);
    }

    private static JobOwnerSnapshot Snapshot(params JobDeclaration[] declarations) =>
        new(StoreFixture.SCOPE, StoreFixture.OWNER_A, declarations);

    private static RecurringScheduleCursorKey CursorKey(string jobKey) => new()
    {
        SchedulerScopeKey = StoreFixture.SCOPE,
        OwnerKey = StoreFixture.OWNER_A,
        JobKey = jobKey
    };
}
