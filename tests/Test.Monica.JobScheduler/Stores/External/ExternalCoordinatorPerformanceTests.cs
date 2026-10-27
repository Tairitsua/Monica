using System.Diagnostics;
using System.Text.Json;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>Opt-in, repeatable production-store measurements against a disposable external database.</summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalCoordinatorPerformanceTests
{
    [Fact]
    public async Task CoordinatorWorkload_ShouldRecordCommandCountsAndLatencyAcrossReplicaCounts()
    {
        var output = Environment.GetEnvironmentVariable("MONICA_LEASE_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) Assert.Skip("Set MONICA_LEASE_PERF_OUTPUT to record the opt-in workload.");
        var token = TestContext.Current.CancellationToken;
        var counter = new ExternalStoreCommandCounter();
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(ExternalSchedulerDatabase.POSTGRES, token, counter.Interceptors);
        var results = new List<object>();
        var unrelatedRows = int.TryParse(Environment.GetEnvironmentVariable("MONICA_LEASE_PERF_UNRELATED_ROWS"), out var count) ? count : 0;
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            if (unrelatedRows > 0)
            {
                await using var connection = await fixture.Database.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO "MonicaLeases" ("Scope", "Name", "HolderId", "TermId", "AcquiredAtUtcTicks", "ConfirmedAtUtcTicks", "ExpiresAtUtcTicks")
                    SELECT 'unrelated', 'role-' || n, 'other-owner', 'unrelated-term', 0, 0, 0 FROM generate_series(1, @count) n;
                    ANALYZE "MonicaLeases";
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "count";
                parameter.Value = unrelatedRows;
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync(token);
            }
            foreach (var replicas in new[] { 1, 4, 16 })
            {
                var stores = Enumerable.Range(0, replicas).Select(_ => fixture.CreateStore()).ToArray();
                try
                {
                    var scope = $"perf-{replicas}";
                    var holder = await AcquireAsync(stores[0], scope, "held", "holder");
                    Assert.NotNull(holder);
                    var grants = new JobCoordinatorLease[replicas];
                    for (var replica = 0; replica < replicas; replica++)
                        grants[replica] = (await AcquireAsync(stores[replica], scope, $"resource-{replica}", $"holder-{replica}"))!;

                    // Warm up query translation and each scenario before recording the same fixed work.
                    await MeasureAsync("standby", async (store, replica) =>
                        Assert.Null(await AcquireAsync(store, scope, "held", $"standby-{replica}")));
                    await MeasureAsync("independent-renewal", async (store, replica) =>
                    {
                        var previous = grants[replica];
                        var renewed = await AcquireAsync(store, scope, previous.Key.CoordinatorKey,
                            previous.Key.WorkerInstanceId, previous.Key.LeaseToken);
                        Assert.NotNull(renewed);
                        Assert.Equal(previous.Key.LeaseToken, renewed.Key.LeaseToken);
                        grants[replica] = renewed;
                    });

                    async Task MeasureAsync(string scenario, Func<IJobSchedulerStore, int, Task> operation)
                    {
                        for (var replica = 0; replica < replicas; replica++) await operation(stores[replica], replica);
                        counter.Reset();
                        var durations = new double[replicas * 100];
                        var elapsed = Stopwatch.StartNew();
                        await Task.WhenAll(stores.Select(async (store, replica) =>
                        {
                            for (var iteration = 0; iteration < 100; iteration++)
                            {
                                var started = Stopwatch.GetTimestamp();
                                await operation(store, replica);
                                durations[replica * 100 + iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            }
                        }));
                        elapsed.Stop();
                        Array.Sort(durations);
                        results.Add(new
                        {
                            scenario, replicas, operations = durations.Length,
                            poolLimit = 3, sqlCommands = counter.CommandsExecuted.Count,
                            transactions = counter.TransactionStarts,
                            elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                            p50Ms = durations[(durations.Length - 1) / 2],
                            p95Ms = durations[(int)((durations.Length - 1) * 0.95)],
                            p99Ms = durations[(int)((durations.Length - 1) * 0.99)]
                        });
                    }
                }
                finally
                {
                    foreach (var store in stores.OfType<IDisposable>()) store.Dispose();
                }
            }
        });
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            database = fixture.Database.ServerVersion,
            capturedAtUtc = DateTimeOffset.UtcNow,
            unrelatedRows,
            results
        }, new JsonSerializerOptions { WriteIndented = true }), token);

        Task<JobCoordinatorLease?> AcquireAsync(IJobSchedulerStore store, string scope, string resource,
            string holder, string? current = null) => store.TryAcquireCoordinatorLeaseAsync(scope, resource, holder,
                TimeSpan.FromMinutes(5), current, token);
    }
}
