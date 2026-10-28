using Monica.EntityFrameworkCore.Models;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>Exercises native scheduler probes and row locks with all store tables outside the search path.</summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalSchedulerTableLayoutTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task SchedulerLifecycle_WhenInfrastructureSchemaIsSelected_ShouldPublishClaimAndComplete(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateWithLayoutAsync(provider,
            InfrastructureTableLayout.PreferSchema("infra"), token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var store = fixture.CreateStore();
            var snapshot = new JobOwnerSnapshot("schema-scope", "schema-owner",
            [new JobDeclaration { JobKey = "schema-job", JobName = "Schema job", JobType = JobType.Triggered, JobArgsKey = "args" }]);
            Assert.True((await store.SyncOwnerSnapshotAsync(snapshot, token)).ContentChanged);
            Assert.False((await store.SyncOwnerSnapshotAsync(snapshot, token)).ContentChanged);
            var request = new JobClaimRequest
            {
                SchedulerScopeKey = snapshot.SchedulerScopeKey, OwnerKey = snapshot.OwnerKey,
                WorkerInstanceId = "schema-worker", JobKeys = ["schema-job"], LeaseDuration = TimeSpan.FromMinutes(2)
            };
            Assert.Empty(await store.ClaimAsync(request, token));
            await store.EnqueueAsync(new JobEnqueueRequest
            {
                SchedulerScopeKey = snapshot.SchedulerScopeKey, OwnerKey = snapshot.OwnerKey,
                JobKey = "schema-job", InstanceId = "schema-execution", JobArgs = "{}"
            }, token);
            var claimed = Assert.Single(await store.ClaimAsync(request, token));
            var completed = await store.CompleteAttemptAsync(new JobAttemptCompletion
            {
                LeaseKey = claimed.LeaseKey, Outcome = JobAttemptOutcome.Succeeded
            }, token);
            Assert.Equal(JobAttemptCompletionStatus.Applied, completed.Status);
            Assert.Equal(JobExecutionState.Succeeded,
                (await store.GetExecutionAsync(snapshot.SchedulerScopeKey, "schema-execution", token))!.State);
            Assert.Empty(await store.ClaimAsync(request, token));
            await using var connection = await fixture.Database.OpenConnectionAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename LIKE 'Job%'";
            Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        });
    }
}
