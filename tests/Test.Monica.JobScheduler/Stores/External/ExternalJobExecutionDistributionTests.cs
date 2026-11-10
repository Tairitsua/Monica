using AwesomeAssertions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalJobExecutionDistributionTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ExecutionAggregates_WhenRecordsBorderBuckets_ShouldMaterializeExactIntegerBuckets(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var store = fixture.CreateStore();
            const string scope = "bucket-scope";
            const string owner = "bucket-owner";
            const string job = "bucket-job";
            await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(scope, owner,
            [new JobDeclaration { JobKey = job, JobName = job, JobType = JobType.Triggered, JobArgsKey = "args" }]), token);
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var width = TimeSpan.TicksPerHour;
            long[] offsets = [0, width / 2 + 1, width - 1, width, 2 * width - 1];
            await using var connection = await fixture.Database.OpenConnectionAsync(token);
            foreach (var offset in offsets)
            {
                var id = $"at-{offset}";
                await store.EnqueueAsync(new JobEnqueueRequest
                {
                    SchedulerScopeKey = scope, OwnerKey = owner, JobKey = job, InstanceId = id, JobArgs = "{}"
                }, token);
                // Arrange retained terminal evidence in this fixture-owned database without waiting for wall time.
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = """
                    UPDATE "JobExecutions"
                    SET "CreatedAtUtcTicks" = @at, "StartedAtUtcTicks" = @at, "CompletedAtUtcTicks" = @at,
                        "State" = 'Succeeded'
                    WHERE "SchedulerScopeKey" = @scope AND "InstanceId" = @id
                    """;
                foreach (var (name, value) in new (string, object)[]
                         { ("at", start.UtcTicks + offset), ("scope", scope), ("id", id) })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }

                (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
            }

            foreach (var completed in new[] { false, true })
            {
                var distribution = await store.GetExecutionDistributionAsync(new JobExecutionQuery
                {
                    SchedulerScopeKey = scope,
                    CreatedAfterUtc = completed ? null : start,
                    CreatedBeforeUtc = completed ? null : start.AddHours(2).AddTicks(-1),
                    CompletedAfterUtc = completed ? start : null,
                    CompletedBeforeUtc = completed ? start.AddHours(2).AddTicks(-1) : null
                }, 2, token);
                distribution.Buckets.Select(bucket => bucket.Counts[JobExecutionState.Succeeded]).Should().Equal(3, 2);
                distribution.StateTotals[JobExecutionState.Succeeded].Should().Be(offsets.Length);
            }

            var analytics = await store.GetExecutionAnalyticsAsync(scope, new JobExecutionAnalyticsQuery
            {
                StartTimeUtc = start,
                EndTimeUtc = start.AddHours(2)
            }, token);
            analytics.Trend.Select(bucket => bucket.SucceededCount).Should().Equal(3, 2);
            analytics.Trend.Sum(bucket => bucket.ExecutedTerminalCount).Should().Be(offsets.Length);
        });
    }
}
