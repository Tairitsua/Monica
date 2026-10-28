using System.Data.Common;
using AwesomeAssertions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Verifies native coordinator fencing and the cost of idle scheduler probes on independent provider connections.
/// </summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalJobSchedulerCoordinationTests
{
    private const string SCOPE = "external-coordination";
    private const string OWNER = "external-owner";
    private const string COORDINATOR = "owner:" + OWNER;
    private const string JOB = "external-job";
    private static readonly LeaseResource RESOURCE = new(SCOPE, "jobscheduler:" + COORDINATOR);

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task TryAcquireAsync_WhenConnectionsRaceThenTakeOver_ShouldFenceTheOldOwnerMutations(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAttempt = AcquireAsync(first, "first-worker");
            var secondAttempt = AcquireAsync(second, "second-worker");
            start.SetResult();
            var attempts = await Task.WhenAll(firstAttempt, secondAttempt);
            var winner = attempts.OfType<LeaseGrant>().Should().ContainSingle().Subject;
            var winnerKey = JobCoordinatorLeaseAdapter.ToKey(winner.Identity);
            var successorWorker = winner.Identity.HolderId == "first-worker" ? "second-worker" : "first-worker";
            var snapshot = new JobOwnerSnapshot(SCOPE, OWNER,
            [
                new JobDeclaration
                {
                    JobKey = JOB, JobName = JOB, JobType = JobType.Recurring,
                    CronExpression = "* * * * * *", TimeZoneId = "UTC"
                }
            ]) { CoordinatorLease = winnerKey };
            var published = await first.SyncOwnerSnapshotAsync(snapshot, token);
            var synchronized = await first.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
            {
                CoordinatorLease = winnerKey,
                CursorKey = new RecurringScheduleCursorKey { SchedulerScopeKey = SCOPE, OwnerKey = OWNER, JobKey = JOB }
            }, token);
            var cursor = synchronized.Cursor!;

            await using (var connection = await fixture.Database.OpenConnectionAsync(token))
            {
                await using var expire = connection.CreateCommand();
                expire.CommandTimeout = 5;
                expire.CommandText = """
                    UPDATE "MonicaLeases" SET "ExpiresAtUtcTicks" = 0
                    WHERE "Scope" = @scope AND "Name" = @coordinator AND "TermId" = @token
                    """;
                AddParameter(expire, "scope", SCOPE);
                AddParameter(expire, "coordinator", "jobscheduler:" + COORDINATOR);
                AddParameter(expire, "token", winner.Identity.TermId);
                Assert.Equal(1, await expire.ExecuteNonQueryAsync(token));
                await using var due = connection.CreateCommand();
                due.CommandTimeout = 5;
                due.CommandText = """
                    UPDATE "JobRecurringCursors" SET "NextOccurrenceUtcTicks" = @due
                    WHERE "SchedulerScopeKey" = @scope AND "OwnerKey" = @owner AND "JobKey" = @job
                    """;
                AddParameter(due, "due", cursor.NextOccurrenceUtc!.Value.AddMinutes(-1).UtcTicks);
                AddParameter(due, "scope", SCOPE);
                AddParameter(due, "owner", OWNER);
                AddParameter(due, "job", JOB);
                Assert.Equal(1, await due.ExecuteNonQueryAsync(token));
            }

            var successor = await second.CoordinatorLeases.TryAcquireAsync(RESOURCE,
                successorWorker, TimeSpan.FromMinutes(2), token);
            successor.Should().NotBeNull();
            var successorKey = JobCoordinatorLeaseAdapter.ToKey(successor!.Identity);
            successor!.Identity.TermId.Should().NotBe(winner.Identity.TermId);
            var dueCursor = (await first.GetDueRecurringSchedulesAsync(SCOPE, OWNER, 1, token))
                .Should().ContainSingle().Subject;
            var materialization = new RecurringOccurrenceMaterialization
            {
                CoordinatorLease = winnerKey, CursorKey = dueCursor.Key,
                ExpectedVersion = dueCursor.Version, ExpectedOccurrenceUtc = dueCursor.NextOccurrenceUtc!.Value,
                InstanceId = "stale-owner-occurrence"
            };
            Func<Task> stalePublish = () => first.SyncOwnerSnapshotAsync(snapshot, token);
            Func<Task> staleMaterialize = () => first.TryMaterializeRecurringOccurrenceAsync(materialization, token);
            await stalePublish.Should().ThrowAsync<InvalidOperationException>();
            await staleMaterialize.Should().ThrowAsync<InvalidOperationException>();
            (await first.GetExecutionAsync(SCOPE, materialization.InstanceId, token)).Should().BeNull();
            (await first.GetOwnerManifestHashAsync(SCOPE, OWNER, token)).Should().Be(published.ManifestHash);
            (await second.SyncOwnerSnapshotAsync(snapshot with { CoordinatorLease = successorKey }, token))
                .ContentChanged.Should().BeFalse();
            var accepted = await second.TryMaterializeRecurringOccurrenceAsync(materialization with
            {
                CoordinatorLease = successorKey, InstanceId = "successor-owner-occurrence"
            }, token);
            accepted.Status.Should().Be(RecurringMaterializationStatus.Materialized);

            async Task<LeaseGrant?> AcquireAsync(IJobSchedulerStore store, string worker)
            {
                await start.Task.WaitAsync(token);
                return await store.CoordinatorLeases.TryAcquireAsync(RESOURCE,
                    worker, TimeSpan.FromMinutes(2), token);
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task IdleProbes_WhenWorkIsFutureOrAnotherCoordinatorIsActive_ShouldUseOneSelectWithoutATransaction(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var counter = new ExternalStoreCommandCounter();
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token, counter.Interceptors);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var store = fixture.CreateStore();
            await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, OWNER,
            [
                new JobDeclaration { JobKey = JOB, JobName = JOB, JobType = JobType.Triggered, JobArgsKey = "test-args" }
            ]), token);
            DateTimeOffset databaseNow;
            await using (var connection = await fixture.Database.OpenConnectionAsync(token))
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "SELECT clock_timestamp() AT TIME ZONE 'UTC'";
                var timestamp = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(token));
                databaseNow = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
            }

            await store.EnqueueAsync(new JobEnqueueRequest
            {
                SchedulerScopeKey = SCOPE, OwnerKey = OWNER, JobKey = JOB, InstanceId = "future-execution",
                JobArgs = "{}", AvailableAtUtc = databaseNow.AddHours(1)
            }, token);
            counter.Reset();
            var claimed = await store.ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = SCOPE, OwnerKey = OWNER, WorkerInstanceId = "idle-worker", JobKeys = [JOB],
                LeaseDuration = TimeSpan.FromMinutes(2)
            }, token);
            claimed.Should().BeEmpty();
            AssertOneReadOnlySelect(counter);

            var holder = await store.CoordinatorLeases.TryAcquireAsync(RESOURCE,
                "holder-worker", TimeSpan.FromMinutes(2), token);
            holder.Should().NotBeNull();
            counter.Reset();
            var contender = await fixture.CreateStore().CoordinatorLeases.TryAcquireAsync(RESOURCE,
                "standby-worker", TimeSpan.FromMinutes(2), token);
            contender.Should().BeNull();
            AssertOneReadOnlySelect(counter);
        });
    }

    private static void AssertOneReadOnlySelect(ExternalStoreCommandCounter counter)
    {
        counter.TransactionStarts.Should().Be(0);
        var sql = counter.CommandsExecuted.Should().ContainSingle().Subject.TrimStart();
        sql.Should().StartWith("SELECT");
        sql.Should().NotContain("FOR UPDATE");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
