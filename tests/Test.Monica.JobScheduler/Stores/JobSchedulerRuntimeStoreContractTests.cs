using AwesomeAssertions;
using Monica.JobScheduler.Models.Definitions;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>Protects diagnostic evidence boundaries across the volatile and relational stores.</summary>
public sealed class JobSchedulerRuntimeStoreContractTests
{
    private static readonly DateTimeOffset NOW = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorSnapshot_ShouldKeepScopeAndExpiredOwnershipWithoutRenewingIt(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.Store.TryAcquireCoordinatorLeaseAsync(fixture.Scope, "maintenance", "first",
            TimeSpan.FromSeconds(2), cancellationToken: token);
        await fixture.Store.TryAcquireCoordinatorLeaseAsync("other-scope", "maintenance", "other",
            TimeSpan.FromMinutes(1), cancellationToken: token);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));

        var snapshot = await fixture.Store.GetCoordinatorSnapshotAsync(fixture.Scope, token);

        snapshot.ObservedAtUtc.Should().Be(NOW.AddSeconds(3));
        var row = snapshot.Coordinators.Should().ContainSingle().Subject;
        row.WorkerInstanceId.Should().Be("first");
        row.ExpiresAtUtc.Should().Be(NOW.AddSeconds(2));
        var successor = await fixture.Store.TryAcquireCoordinatorLeaseAsync(fixture.Scope, "maintenance", "second",
            TimeSpan.FromSeconds(2), cancellationToken: token);
        successor.Should().NotBeNull("observing an expired grant must not renew it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerObservation_ShouldDistinguishManifestPublicationFromRepeatedObservation(bool useEfCore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var snapshot = new JobOwnerSnapshot(fixture.Scope, StoreFixture.OWNER_A,
            [StoreFixture.TriggeredDeclaration("runtime.job")]);
        var original = await fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Store.SyncOwnerSnapshotAsync(snapshot, token);
        var observed = (await fixture.Store.QueryOwnerSummariesAsync(fixture.Scope, token)).Single();
        observed.ManifestPublishedAtUtc.Should().Be(NOW);
        observed.LastObservedAtUtc.Should().Be(NOW.AddSeconds(10));
        observed.ManifestHash.Should().Be(original.ManifestHash);

        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Store.SyncOwnerSnapshotAsync(snapshot with
        {
            Declarations = [snapshot.Declarations[0] with { RetryCount = 3 }],
            ExpectedPreviousManifestHash = original.ManifestHash
        }, token);
        var changed = (await fixture.Store.QueryOwnerSummariesAsync(fixture.Scope, token)).Single();
        changed.ManifestPublishedAtUtc.Should().Be(NOW.AddSeconds(20));
        changed.ManifestHash.Should().NotBe(original.ManifestHash);
    }
}
