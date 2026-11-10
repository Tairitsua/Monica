using AwesomeAssertions;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>Checks exact distribution boundaries through the native provider's aggregate reader.</summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalJobExecutionDistributionTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, false)]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, true)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, false)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, true)]
    public async Task GetExecutionDistributionAsync_WhenTicksStraddleWideBuckets_ShouldKeepExactHalfOpenCounts(
        string provider, bool completed)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var store = fixture.CreateStore();
            var snapshot = new JobOwnerSnapshot("distribution-scope", "distribution-owner",
                [new JobDeclaration { JobKey = "distribution-job", JobName = "Distribution job", JobType = JobType.Triggered, JobArgsKey = "args" }]);
            await store.SyncOwnerSnapshotAsync(snapshot, token);
            var start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            // Beyond double's exact integer range, adjacent ticks must still land on opposite bucket sides.
            var width = TimeSpan.TicksPerDay * 366L * 40;
            long[] offsets = [0, width - 1, width, 2 * width - 1, 2 * width, 3 * width - 1];
            for (var index = 0; index < offsets.Length; index++)
            {
                var id = $"distribution-{index}";
                await store.EnqueueAsync(new JobEnqueueRequest
                {
                    SchedulerScopeKey = snapshot.SchedulerScopeKey, OwnerKey = snapshot.OwnerKey,
                    JobKey = "distribution-job", InstanceId = id, JobArgs = "{}"
                }, token);
            }

            // Timestamp arrangement is confined to this fixture-owned database; the query uses the real store.
            await using (var connection = await fixture.Database.OpenConnectionAsync(token))
            {
                for (var index = 0; index < offsets.Length; index++)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE "JobExecutions"
                        SET "CreatedAtUtcTicks" = @ticks, "CompletedAtUtcTicks" = @ticks, "State" = 'Succeeded'
                        WHERE "SchedulerScopeKey" = @scope AND "InstanceId" = @id
                        """;
                    foreach (var (name, value) in new (string, object)[]
                             { ("ticks", start.UtcTicks + offsets[index]), ("scope", snapshot.SchedulerScopeKey), ("id", $"distribution-{index}") })
                    {
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = name;
                        parameter.Value = value;
                        command.Parameters.Add(parameter);
                    }

                    Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
                }
            }

            var end = start.AddTicks(3 * width - 1);
            var query = new JobExecutionQuery
            {
                SchedulerScopeKey = snapshot.SchedulerScopeKey,
                CreatedAfterUtc = completed ? null : start,
                CreatedBeforeUtc = completed ? null : end,
                CompletedAfterUtc = completed ? start : null,
                CompletedBeforeUtc = completed ? end : null,
                States = [JobExecutionState.Failed], PageNumber = 2, PageSize = 1
            };
            var result = await store.GetExecutionDistributionAsync(query, 3, token);
            Assert.Equal(completed, result.UsesCompletionTime);
            Assert.Equal(start, result.StartTimeUtc);
            Assert.Equal(end.AddTicks(1), result.EndTimeUtc);
            Assert.Equal([2L, 2L, 2L], result.Buckets.Select(bucket => bucket.Counts[JobExecutionState.Succeeded]));
            Assert.Equal(6, result.StateTotals[JobExecutionState.Succeeded]);
            Assert.Equal(0, result.StateTotals[JobExecutionState.Failed]);
        });
    }

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
