using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;
using VastbaseException = GaussDB.PostgresException;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>
/// Verifies identity-race replay through actual store saves when Vastbase omits structured constraint metadata.
/// </summary>
public sealed class EfCoreJobSchedulerStoreIdentityConflictTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private const string COORDINATOR = "owner:identity-conflict";
    private const string WORKER = "identity-worker";
    private const string IDENTITY_MESSAGE = "duplicate key value violates unique constraint \"PK_JobSchedulerCoordinators\"";

    [Fact]
    public async Task TryAcquireCoordinatorLeaseAsync_WhenVastbaseReportsMatchingInsertConflictWithoutConstraintMetadata_ShouldReplayAndPersistOneGrant()
    {
        var fault = new IdentityConflict(IDENTITY_MESSAGE);
        await using var fixture = await StoreFixture.CreateAsync(true, Now, fault);
        var token = TestContext.Current.CancellationToken;

        var grant = await fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, WORKER,
            TimeSpan.FromMinutes(1), cancellationToken: token);

        grant.Should().NotBeNull();
        fault.SaveAttempts.Should().Be(2);
        var committed = (await fixture.Store.GetCoordinatorSnapshotAsync(StoreFixture.SCOPE, token))
            .Coordinators.Should().ContainSingle().Subject;
        committed.WorkerInstanceId.Should().Be(WORKER);
        committed.ExpiresAtUtc.Should().Be(grant!.ExpiresAtUtc);
        (await fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, "other-worker",
            TimeSpan.FromMinutes(1), cancellationToken: token)).Should().BeNull();
    }

    [Theory]
    [InlineData("duplicate key value violates unique constraint \"UX_CustomCoordinatorRule\"")]
    [InlineData("duplicate key value violates unique constraint \"PK_JobExecutionGates\"")]
    [InlineData(IDENTITY_MESSAGE + " with additional context")]
    public async Task TryAcquireCoordinatorLeaseAsync_WhenVastbaseConflictDoesNotIdentifyThisInsertion_ShouldFailWithoutReplay(string message)
    {
        var fault = new IdentityConflict(message);
        await using var fixture = await StoreFixture.CreateAsync(true, Now, fault);
        var token = TestContext.Current.CancellationToken;

        Func<Task> acquire = () => fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, WORKER,
            TimeSpan.FromMinutes(1), cancellationToken: token);

        var failure = await acquire.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<VastbaseException>();
        fault.SaveAttempts.Should().Be(1);
        (await fixture.Store.GetCoordinatorSnapshotAsync(StoreFixture.SCOPE, token)).Coordinators.Should().BeEmpty();
    }

    [Fact]
    public async Task TryAcquireCoordinatorLeaseAsync_WhenVastbaseReportsIdentityMessageDuringRenewal_ShouldNotReplayOrExtendTheGrant()
    {
        var fault = new IdentityConflict(IDENTITY_MESSAGE) { Armed = false };
        await using var fixture = await StoreFixture.CreateAsync(true, Now, fault);
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, WORKER,
            TimeSpan.FromMinutes(1), cancellationToken: token);
        original.Should().NotBeNull();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fault.Arm();

        Func<Task> renew = () => fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, WORKER,
            TimeSpan.FromMinutes(3), original!.Key.LeaseToken, token);

        await renew.Should().ThrowAsync<DbUpdateException>();
        fault.SaveAttempts.Should().Be(1);
        var committed = (await fixture.Store.GetCoordinatorSnapshotAsync(StoreFixture.SCOPE, token))
            .Coordinators.Should().ContainSingle().Subject;
        committed.ExpiresAtUtc.Should().Be(original!.ExpiresAtUtc);
    }

    [Fact]
    public async Task TryAcquireCoordinatorLeaseAsync_WhenVastbaseConflictHasNoAssociatedEntries_ShouldFailWithoutReplay()
    {
        var fault = new IdentityConflict(IDENTITY_MESSAGE, includeEntries: false);
        await using var fixture = await StoreFixture.CreateAsync(true, Now, fault);
        var token = TestContext.Current.CancellationToken;

        Func<Task> acquire = () => fixture.Store.TryAcquireCoordinatorLeaseAsync(StoreFixture.SCOPE, COORDINATOR, WORKER,
            TimeSpan.FromMinutes(1), cancellationToken: token);

        await acquire.Should().ThrowAsync<DbUpdateException>();
        fault.SaveAttempts.Should().Be(1);
        (await fixture.Store.GetCoordinatorSnapshotAsync(StoreFixture.SCOPE, token)).Coordinators.Should().BeEmpty();
    }

    private sealed class IdentityConflict(string message, bool includeEntries = true) : SaveChangesInterceptor
    {
        internal int SaveAttempts { get; private set; }
        internal bool Armed { get; set; } = true;

        internal void Arm()
        {
            SaveAttempts = 0;
            Armed = true;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context ?? throw new InvalidOperationException("The store save supplied no context.");
            var entries = context.ChangeTracker.Entries().Where(entry =>
                entry.Metadata.GetTableName() == "JobSchedulerCoordinators"
                && entry.State is EntityState.Added or EntityState.Modified).ToArray();
            if (entries.Length == 0)
            {
                return ValueTask.FromResult(result);
            }

            SaveAttempts++;
            if (Armed)
            {
                Armed = false;
                var native = new VastbaseException(message, "ERROR", "ERROR", "23505");
                IReadOnlyList<EntityEntry> associatedEntries = includeEntries ? entries : [];
                throw new DbUpdateException("Injected provider save conflict.", native, associatedEntries);
            }

            return ValueTask.FromResult(result);
        }
    }
}
