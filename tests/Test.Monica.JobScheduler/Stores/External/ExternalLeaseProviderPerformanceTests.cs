using System.Diagnostics;
using System.Text.Json;
using Monica.Locker.Leases;
using Monica.Locker.Redis;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalLeaseProviderPerformanceTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    public async Task NativeAuthorities_ShouldRecordEquivalentLeaseWorkloadsWithSeparateReplicaConnections(int sqlPoolLimit)
    {
        var output = Environment.GetEnvironmentVariable("MONICA_REDIS_LEASE_PERF_OUTPUT");
        var redis = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(redis))
            Assert.Skip("Set MONICA_REDIS_LEASE_PERF_OUTPUT and MONICA_LEASE_TEST_REDIS to run the comparison.");
        var token = TestContext.Current.CancellationToken;
        var counter = new ExternalStoreCommandCounter();
        var fixture = await ExternalSchedulerStoreFixture.CreateWithPoolAsync(ExternalSchedulerDatabase.POSTGRES, token, sqlPoolLimit, counter.Interceptors);
        var results = new List<object>();
        string? redisVersion = null;
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            foreach (var replicas in new[] { 1, 4, 16 })
            {
                var sqlStores = Enumerable.Range(0, replicas).Select(_ => fixture.CreateStore()).ToArray();
                var connections = new List<ConnectionMultiplexer>();
                try
                {
                    for (var replica = 0; replica < replicas; replica++)
                        connections.Add(await ConnectionMultiplexer.ConnectAsync(redis));
                    redisVersion ??= connections[0].GetServer(connections[0].GetEndPoints()[0]).Version.ToString();
                    var sessions = connections.Select(_ => new ProfilingSession()).ToArray();
                    for (var replica = 0; replica < replicas; replica++)
                    {
                        var index = replica;
                        connections[index].RegisterProfiler(() => sessions[index]);
                    }
                    var options = new RedisLeaseStoreOptions { KeyPrefix = "perf:" + Guid.NewGuid().ToString("N") };
                    await MeasureAuthorityAsync("postgresql", sqlStores.Select(store => store.CoordinatorLeases).ToArray());
                    await MeasureAuthorityAsync("redis", connections.Select(connection => (IExclusiveLeaseStore)new RedisLeaseStore(connection, options)).ToArray());

                    async Task MeasureAuthorityAsync(string authority, IExclusiveLeaseStore[] stores)
                    {
                        var scope = $"perf-{replicas}-{authority}";
                        var held = new LeaseResource(scope, "held");
                        var owner = Assert.IsType<LeaseGrant>(await stores[0].TryAcquireAsync(held, "holder", TimeSpan.FromMinutes(5), token));
                        var grants = new LeaseGrant[replicas];
                        for (var replica = 0; replica < replicas; replica++)
                            grants[replica] = Assert.IsType<LeaseGrant>(await stores[replica].TryAcquireAsync(new(scope, $"role-{replica}"),
                                $"replica-{replica}", TimeSpan.FromMinutes(5), token));
                        try
                        {
                            await MeasureAsync("standby", async replica => Assert.Null(await stores[replica]
                                .TryAcquireAsync(held, $"standby-{replica}", TimeSpan.FromMinutes(5), token)));
                            await MeasureAsync("independent-renewal", async replica => Assert.Equal(grants[replica].Identity,
                                (await stores[replica].TryRenewAsync(grants[replica].Identity, TimeSpan.FromMinutes(5), token))!.Identity));
                        }
                        finally
                        {
                            await stores[0].TryReleaseAsync(owner.Identity, token);
                            for (var replica = 0; replica < replicas; replica++) await stores[replica].TryReleaseAsync(grants[replica].Identity, token);
                        }

                        async Task MeasureAsync(string scenario, Func<int, Task> operation)
                        {
                            for (var replica = 0; replica < replicas; replica++) await operation(replica);
                            counter.Reset();
                            foreach (var session in sessions) _ = session.FinishProfiling();
                            var durations = new double[replicas * 100];
                            var elapsed = Stopwatch.StartNew();
                            await Task.WhenAll(Enumerable.Range(0, replicas).Select(async replica =>
                            {
                                for (var iteration = 0; iteration < 100; iteration++)
                                {
                                    var started = Stopwatch.GetTimestamp();
                                    await operation(replica);
                                    durations[replica * 100 + iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                                }
                            }));
                            elapsed.Stop();
                            Array.Sort(durations);
                            results.Add(new
                            {
                                authority, scenario, replicas, operations = durations.Length,
                                sqlCommands = counter.CommandsExecuted.Count, sqlTransactions = counter.TransactionStarts,
                                redisCommands = sessions.SelectMany(session => session.FinishProfiling())
                                    .Count(command => command.Command is "EVAL" or "EVALSHA"),
                                elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                                p50Ms = durations[(durations.Length - 1) / 2],
                                p95Ms = durations[(int)((durations.Length - 1) * .95)],
                                p99Ms = durations[(int)((durations.Length - 1) * .99)]
                            });
                        }
                    }
                }
                finally
                {
                    foreach (var store in sqlStores.OfType<IDisposable>()) store.Dispose();
                    foreach (var connection in connections) await connection.DisposeAsync();
                }
            }
        });
        var outputPath = sqlPoolLimit == 3 ? output : Path.ChangeExtension(output, $"pool{sqlPoolLimit}.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow, postgres = fixture.Database.ServerVersion, redis = redisVersion,
            sqlPoolLimit, redisConnectionsPerReplica = 1, results
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
