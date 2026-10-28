using System.Data.Common;
using Monica.Locker.Leases;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalSharedLeaseTests
{
    private static readonly LeaseResource RESOURCE = new("shared-lease-contract", "outbox:maintenance");

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Acquisition_WhenReplicasRaceAndTheSameHolderReturns_ShouldFenceEveryPreviousTerm(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var stores = Enumerable.Range(0, 16).Select(_ => fixture.CreateStore()).ToArray();
            try
            {
                var attempts = await Task.WhenAll(stores.Select((store, index) =>
                    store.CoordinatorLeases.TryAcquireAsync(RESOURCE, $"replica-{index}", TimeSpan.FromMinutes(1), token)));
                var first = Assert.Single(attempts.OfType<LeaseGrant>());
                await using (var connection = await fixture.Database.OpenConnectionAsync(token))
                    await ExecuteAsync(connection, null,
                        "UPDATE \"MonicaLeases\" SET \"ExpiresAtUtcTicks\" = 0 WHERE \"Scope\" = @scope AND \"Name\" = @name", token);
                var store = stores[0].CoordinatorLeases;
                Assert.Null(await store.TryRenewAsync(first.Identity, TimeSpan.FromMinutes(1), token));
                var next = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(RESOURCE, first.Identity.HolderId, TimeSpan.FromMinutes(1), token));
                Assert.NotEqual(first.Identity.TermId, next.Identity.TermId);
                Assert.False(await store.TryReleaseAsync(first.Identity, token));
                Assert.Null(await store.TryRenewAsync(first.Identity, TimeSpan.FromMinutes(1), token));
                var renewed = Assert.IsType<LeaseGrant>(await store.TryRenewAsync(next.Identity, TimeSpan.FromMinutes(1), token));
                Assert.Equal(next.Identity, renewed.Identity);
                var snapshot = await store.GetSnapshotAsync(RESOURCE.Scope, "outbox:", token);
                var row = Assert.Single(snapshot.Leases);
                Assert.Equal(next.ConfirmedAtUtc, row.AcquiredAtUtc);
                Assert.Equal(renewed.ConfirmedAtUtc, row.ConfirmedAtUtc);
                var exact = await store.GetSnapshotAsync(RESOURCE, token);
                Assert.Equal(row, Assert.Single(exact.Leases));
                Assert.True(exact.ObservedAtUtc >= renewed.ConfirmedAtUtc);
                Assert.Empty((await store.GetSnapshotAsync(new(RESOURCE.Scope, RESOURCE.Name + ":missing"), token)).Leases);
            }
            finally { foreach (var store in stores.OfType<IDisposable>()) store.Dispose(); }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Renewal_WhenBlockedBeyondItsExpiry_ShouldObserveTimeAfterLockAndLeaveOtherResourcesIndependent(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var owner = fixture.CreateStore();
            try
            {
                var store = owner.CoordinatorLeases;
                var first = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(RESOURCE, "holder", TimeSpan.FromMinutes(1), token));
                await using var blocker = await fixture.Database.OpenConnectionAsync(token);
                var blockerId = await ExternalSchedulerSession.ReadIdAsync(blocker, token);
                await using var transaction = await blocker.BeginTransactionAsync(token);
                await ExecuteAsync(blocker, transaction,
                    "SELECT * FROM \"MonicaLeases\" WHERE \"Scope\" = @scope AND \"Name\" = @name FOR UPDATE", token);
                var renewing = store.TryRenewAsync(first.Identity, TimeSpan.FromMinutes(1), token);
                try
                {
                    await WaitForLockAsync(blocker, transaction, blockerId, renewing, token);
                    var unrelated = await store.TryAcquireAsync(new LeaseResource(RESOURCE.Scope, "jobscheduler:owner:independent"),
                        "another-holder", TimeSpan.FromMinutes(1), token);
                    Assert.NotNull(unrelated);
                    // Expire after the renewal has started waiting. A pre-lock clock sample would incorrectly revive it.
                    await ExecuteAsync(blocker, transaction, """
                        UPDATE "MonicaLeases" SET "ExpiresAtUtcTicks" =
                            CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 10000000 AS bigint) + 621355968000000000
                        WHERE "Scope" = @scope AND "Name" = @name
                        """, token);
                    await transaction.CommitAsync(token);
                }
                finally
                {
                    await transaction.DisposeAsync();
                    await renewing;
                }
                Assert.Null(await renewing);
            }
            finally { (owner as IDisposable)?.Dispose(); }
        });
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = sql;
        Add(command, "scope", RESOURCE.Scope);
        Add(command, "name", RESOURCE.Name);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task WaitForLockAsync(DbConnection connection, DbTransaction transaction, long blockerId,
        Task pending, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var observations = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            var identity = ExternalSchedulerSession.LockIdentityColumn(connection);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            command.CommandText = $"""
                SELECT EXISTS (SELECT 1 FROM pg_locks waiting WHERE NOT waiting.granted
                  AND waiting.transactionid IN (SELECT held.transactionid FROM pg_locks held
                    WHERE held.{identity} = @blocker AND held.locktype = 'transactionid' AND held.granted))
                """;
            Add(command, "blocker", blockerId);
            if (Equals(true, await command.ExecuteScalarAsync(deadline.Token))) return;
            Assert.False(pending.IsCompleted, "Renewal must wait for the protected row before the test expires it.");
        } while (await observations.WaitForNextTickAsync(deadline.Token));
        throw new InvalidOperationException("The expected lease-row contention was not observed.");
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
