using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Modules;
using Monica.Locker.Leases;
using Monica.Locker.Redis;
using StackExchange.Redis;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Monica.Testing.Time;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed partial class ExternalOutboxLeaseTests
{
    [Fact]
    public async Task Cleanup_WhenItsTransactionIsBlocked_ShouldAllowDeliveryOnAnotherNativeConnection()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(ExternalSchedulerDatabase.POSTGRES, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var commands = new OutboxCommands { BlockDelete = true };
            using var host = CreateHost(fixture.Database, TimeProvider.System, commands, worker: false);
            await CreateOutboxSchemaAsync(host, token);
            await SeedAsync(host, DateTimeOffset.UtcNow, 1, pending: true, token);
            var dispatcher = host.Services.GetRequiredService<OutboxDispatcher<ExternalOutboxContext>>();
            var cleaning = dispatcher.CleanupAsync(token);
            try
            {
                await commands.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
                Assert.Equal(1, await dispatcher.DrainAsync(1, token).WaitAsync(TimeSpan.FromSeconds(3), token));
                Assert.False(cleaning.IsCompleted);
            }
            finally
            {
                commands.AllowDelete.TrySetResult();
                await cleaning;
            }
            Assert.Equal(1, await cleaning);
        });
    }

    [Fact]
    public async Task Maintenance_WhenManyHostsShareTheTable_ShouldSuppressDuplicateScansAndRecordNativeCosts()
    {
        var output = Environment.GetEnvironmentVariable("MONICA_OUTBOX_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) Assert.Skip("Set MONICA_OUTBOX_PERF_OUTPUT to record the opt-in multi-host workload.");
        var token = TestContext.Current.CancellationToken;
        var results = new List<object>();
        foreach (var replicas in new[] { 1, 4, 16 })
        {
            var fixture = await ExternalSchedulerStoreFixture.CreateAsync(ExternalSchedulerDatabase.POSTGRES, token);
            await ExternalSchedulerScenario.RunAsync(fixture, async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var time = new ControllableTimeProvider(now);
                var commands = new OutboxCommands();
                var hosts = Enumerable.Range(0, replicas).Select(_ => CreateHost(fixture.Database, time, commands, worker: true)).ToArray();
                try
                {
                    await CreateOutboxSchemaAsync(hosts[0], token);
                    await SeedAsync(hosts[0], now, 4000, pending: false, token);
                    commands.Reset();
                    var baseline = Stopwatch.StartNew();
                    var baselineDeleted = await Task.WhenAll(hosts.Select(host => OriginalCleanupAsync(host, now, token)));
                    baseline.Stop();
                    Assert.Equal(4000, baselineDeleted.Sum());
                    results.Add(new { scenario = "original-cleanup", replicas, elapsedMs = baseline.Elapsed.TotalMilliseconds,
                        deleted = baselineDeleted.Sum(), deleteStatements = commands.Deletes, leaseReads = commands.LeaseReads });

                    await SeedAsync(hosts[0], now, 4000, pending: false, token);
                    await Task.WhenAll(hosts.Select(host => host.StartAsync(token)));
                    await commands.WaitForDeliveryScansAsync(replicas, token);
                    await time.WaitForTimerAsync(TimeSpan.FromSeconds(60), token);
                    commands.Reset();
                    var coordinated = Stopwatch.StartNew();
                    time.Advance(TimeSpan.FromSeconds(60));
                    await time.WaitForTimerAsync(TimeSpan.FromSeconds(120), token);
                    coordinated.Stop();
                    Assert.Equal(5, commands.Deletes); // Four full batches and one bounded empty probe, by one owner.
                    results.Add(new { scenario = "coordinated-cleanup", replicas, elapsedMs = coordinated.Elapsed.TotalMilliseconds,
                        deleted = 4000, deleteStatements = commands.Deletes, leaseReads = commands.LeaseReads });

                    time.Advance(TimeSpan.FromSeconds(1));
                    await time.WaitForTimerAsync(TimeSpan.FromSeconds(122), token);
                    Assert.Equal(1, commands.StatusQueries);
                    time.Advance(TimeSpan.FromSeconds(4));
                    await time.WaitForTimerAsync(TimeSpan.FromSeconds(70), token);
                    Assert.Equal(5, commands.Deletes);
                    await using var db = await Factory(hosts[0]).CreateDbContextAsync(token);
                    Assert.Equal(12000, await db.Set<OutboxMessage>().CountAsync(token));
                    var observer = fixture.CreateStore();
                    try { Assert.Single((await observer.CoordinatorLeases.GetSnapshotAsync("outbox", "maintenance:", token)).Leases); }
                    finally { (observer as IDisposable)?.Dispose(); }
                }
                finally
                {
                    await Task.WhenAll(hosts.Select(host => host.StopAsync(token)));
                    foreach (var host in hosts) host.Dispose();
                }
            });
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow, poolLimit = 3, retainedRows = 12000, expiredRows = 4000,
            payloadBytes = 512, cleanupBatchSize = 1000, results
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    [Fact]
    public async Task RedisRevocation_DuringAnInFlightCleanup_ShouldAllowSafeOverlapAndPreservePendingMessages()
    {
        var redis = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(redis)) Assert.Skip("Set MONICA_LEASE_TEST_REDIS to exercise advisory cleanup.");
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(ExternalSchedulerDatabase.POSTGRES, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(redis);
            var commands = new OutboxCommands { BlockAutocommitDelete = true };
            using var firstHost = CreateHost(fixture.Database, TimeProvider.System, commands, false, connection);
            using var secondHost = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), false, connection);
            await CreateOutboxSchemaAsync(firstHost, token);
            await SeedAsync(firstHost, DateTimeOffset.UtcNow, 1, pending: true, token);
            commands.Reset();
            commands.BlockDelete = true;
            var first = firstHost.Services.GetRequiredService<OutboxDispatcher<ExternalOutboxContext>>().CleanupAsync(token);
            try
            {
                await commands.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
                var leases = firstHost.Services.GetRequiredKeyedService<IExclusiveLeaseStore>("maintenance");
                var role = Assert.Single((await leases.GetSnapshotAsync(fixture.Database.DatabaseName, "outbox:", token)).Leases);
                Assert.True(await ((ILeaseAdministration)leases).RevokeAsync(role.Resource, token));
                var second = secondHost.Services.GetRequiredService<OutboxDispatcher<ExternalOutboxContext>>();
                Assert.Equal(1, await second.CleanupAsync(token));
                commands.AllowDelete.TrySetResult();
                Assert.Equal(0, await first);
                Assert.Equal(0, commands.LeaseReads);
                await using var db = await Factory(firstHost).CreateDbContextAsync(token);
                Assert.Null(Assert.Single(await db.Set<OutboxMessage>().ToArrayAsync(token)).DeliveredAtUtc);
                Assert.Equal(1, await second.DrainAsync(1, token));
            }
            finally
            {
                commands.AllowDelete.TrySetResult();
                await first;
            }
        });
    }

    [Fact]
    public async Task MaintenanceAuthorities_ShouldCompareColdElectionAndCachedCleanupWithRealServerClocks()
    {
        var output = Environment.GetEnvironmentVariable("MONICA_ADVISORY_OUTBOX_PERF_OUTPUT");
        var redis = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(redis))
            Assert.Skip("Set MONICA_ADVISORY_OUTBOX_PERF_OUTPUT and MONICA_LEASE_TEST_REDIS to compare maintenance providers.");
        var token = TestContext.Current.CancellationToken;
        var results = new List<object>();
        foreach (var useRedis in new[] { false, true })
        foreach (var replicas in new[] { 1, 4, 16 })
        {
            var fixture = await ExternalSchedulerStoreFixture.CreateAsync(ExternalSchedulerDatabase.POSTGRES, token);
            await ExternalSchedulerScenario.RunAsync(fixture, async () =>
            {
                var cadence = new MaintenanceCadenceTimeProvider();
                var commands = new OutboxCommands();
                var allCommands = new ExternalStoreCommandCounter();
                var connections = new List<ConnectionMultiplexer>();
                var hosts = new List<IHost>();
                var authority = useRedis ? "redis" : "postgresql";
                try
                {
                    for (var replica = 0; replica < replicas; replica++)
                    {
                        ConnectionMultiplexer? connection = null;
                        if (useRedis) connections.Add(connection = await ConnectionMultiplexer.ConnectAsync(redis));
                        hosts.Add(CreateHost(fixture.Database, cadence, commands, true, connection, allCommands));
                    }
                    await CreateOutboxSchemaAsync(hosts[0], token);
                    var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    await SeedAsync(hosts[0], now, 4000, false, token);
                    await Task.WhenAll(hosts.Select(host => host.StartAsync(token)));
                    await commands.WaitForDeliveryScansAsync(replicas, token);
                    var initial = await cadence.TakeAsync(2 * replicas, token);
                    Assert.Equal(replicas, initial.Count(ticket => ticket.Due == TimeSpan.FromSeconds(60)));
                    commands.Reset();
                    allCommands.Reset();
                    var elapsed = Stopwatch.StartNew();
                    foreach (var ticket in initial.Where(ticket => ticket.Due == TimeSpan.FromSeconds(60))) ticket.Fire();
                    var scheduled = await cadence.TakeAsync(replicas, token);
                    elapsed.Stop();
                    Assert.Equal(5, commands.Deletes);
                    var nextCleanup = Assert.Single(scheduled, ticket => ticket.Due == TimeSpan.FromSeconds(60));
                    Record("cold-election-and-cleanup", elapsed.Elapsed.TotalMilliseconds);
                    if (useRedis)
                    {
                        Assert.Equal(0, commands.LeaseReads);
                        Assert.Equal(0, allCommands.TransactionStarts);
                    }

                    foreach (var ticket in initial.Where(ticket => ticket.Due == TimeSpan.FromSeconds(61))) ticket.Fire();
                    _ = await cadence.TakeAsync(replicas, token);
                    Assert.Equal(1, commands.StatusQueries);
                    results.Add(new { authority, replicas, scenario = "statistics", queries = commands.StatusQueries });

                    // Only the holder runs these passes; standby traffic is measured separately by the lease benchmark.
                    // Cadence is manually triggered, but monotonic time, lease renewal, Redis TTL and database time are real.
                    for (var iteration = 0; iteration < 5; iteration++)
                    {
                        await SeedAsync(hosts[0], now, 4000, false, token);
                        commands.Reset();
                        allCommands.Reset();
                        elapsed.Restart();
                        nextCleanup.Fire();
                        nextCleanup = Assert.Single(await cadence.TakeAsync(1, token));
                        elapsed.Stop();
                        Assert.Equal(TimeSpan.FromSeconds(60), nextCleanup.Due);
                        if (commands.Deletes != 5)
                        {
                            await using var diagnostic = await Factory(hosts[0]).CreateDbContextAsync(token);
                            var remaining = await diagnostic.Set<OutboxMessage>()
                                .CountAsync(row => row.DeliveredAtUtc < now.UtcDateTime.AddDays(-7), token);
                            var failures = hosts.SelectMany(host => host.Services.GetServices<IHostedService>())
                                .OfType<global::Monica.Core.HostedService.Abstractions.MoBackgroundService>()
                                .SelectMany(service => service.RuntimeInfo.StateHistory)
                                .Where(state => state.Exception is not null).Select(state => state.Exception!.ToString());
                            Assert.Fail($"{authority}/{replicas} iteration {iteration}: {commands.Deletes} deletes in {elapsed.Elapsed.TotalMilliseconds} ms, "
                                + $"{remaining} expired rows remain. " + string.Join("\n", failures));
                        }
                        Record("cached-holder-cleanup", elapsed.Elapsed.TotalMilliseconds);
                    }
                    await using var db = await Factory(hosts[0]).CreateDbContextAsync(token);
                    Assert.Equal(12000, await db.Set<OutboxMessage>().CountAsync(token));
                    Assert.Equal(now.UtcDateTime, Assert.Single(await db.Set<OutboxMessage>()
                        .Select(row => row.DeliveredAtUtc).Distinct().ToArrayAsync(token)));

                    void Record(string scenario, double elapsedMs)
                    {
                        var result = new
                        {
                            authority, replicas, scenario, elapsedMs, deleted = 4000,
                            deleteStatements = commands.Deletes, leaseReads = commands.LeaseReads,
                            sqlTransactions = allCommands.TransactionStarts
                        };
                        results.Add(result);
                        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(result));
                    }
                }
                finally
                {
                    await Task.WhenAll(hosts.Select(host => host.StopAsync(token)));
                    foreach (var host in hosts) host.Dispose();
                    foreach (var connection in connections) await connection.DisposeAsync();
                }
            });
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow, poolLimit = 3, retainedRows = 12000, expiredRows = 4000,
            payloadBytes = 512, cleanupBatchSize = 1000, results
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static IHost CreateHost(ExternalSchedulerDatabase database, TimeProvider time, OutboxCommands commands, bool worker,
        IConnectionMultiplexer? redis = null, ExternalStoreCommandCounter? allCommands = null,
        IEventTransport? transport = null, Action<RepositoryOutboxOptions>? configureOutbox = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (redis is not null)
        {
            builder.Services.AddSingleton(redis);
            builder.Services.AddRedisLeaseStore("maintenance", new RedisLeaseStoreOptions { KeyPrefix = database.DatabaseName });
        }
        builder.AddMonica(monica =>
        {
            monica.AddEventBus().UseNoOpDistributedEventBus();
            monica.AddRepository().AddRepositoryDbContext<ExternalOutboxContext>(
                (_, options) =>
                {
                    database.ConfigureDbContext(options);
                    options.AddInterceptors(commands);
                    if (allCommands is not null) options.AddInterceptors(allCommands.Interceptors);
                }, DbContextProviderType.Default)
                .AddOutbox<ExternalOutboxContext>(options =>
                {
                    options.EnableWorker = worker;
                    options.LeaseTable.ExcludeFromMigrations = true; // The scheduler context owns the shared table.
                    options.CleanupInterval = TimeSpan.FromSeconds(60);
                    options.StatusInterval = TimeSpan.FromSeconds(61);
                    if (redis is not null) options.MaintenanceLeaseStore = new LeaseStoreSelection("maintenance", database.DatabaseName);
                    if (time is MaintenanceCadenceTimeProvider)
                    {
                        options.PollInterval = TimeSpan.FromSeconds(7);
                        options.MaintenanceLease = new LeaseCoordinatorOptions
                            { StandbyInterval = TimeSpan.FromSeconds(3) };
                    }
                    configureOutbox?.Invoke(options);
                });
        });
        builder.Services.AddMonicaTestSeams();
        if (transport is not null)
        {
            builder.Services.RemoveAll<IEventTransport>();
            builder.Services.AddSingleton(transport);
        }
        builder.Services.AddSingleton(time);
        return builder.Build();
    }

    private static IDbContextFactory<ExternalOutboxContext> Factory(IHost host) =>
        host.Services.GetRequiredService<IDbContextFactory<ExternalOutboxContext>>();

    private static async Task CreateOutboxSchemaAsync(IHost host, CancellationToken token)
    {
        await using var db = await Factory(host).CreateDbContextAsync(token);
        var script = db.Database.GenerateCreateScript();
        Assert.DoesNotContain("CREATE TABLE \"MonicaLeases\"", script, StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync(script, token);
    }

    private static async Task SeedAsync(IHost host, DateTimeOffset now, int expired, bool pending, CancellationToken token)
    {
        await using var db = await Factory(host).CreateDbContextAsync(token);
        // Only this fixture-owned disposable database is reset. Avoid accumulating dead tuples across repetitions.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MonicaOutbox\"", token);
        var retained = pending ? 0 : 12000;
        db.AddRange(Enumerable.Range(0, expired + retained + (pending ? 1 : 0)).Select(index => new OutboxMessage
        {
            MessageId = $"message-{index}", Source = "lease-test", EventName = "lease.test", TopicName = "lease.test",
            Body = new byte[512], Scope = EventSubscriptionScope.Distributed,
            CreatedAtUtc = now.UtcDateTime.AddDays(-10),
            DeliveredAtUtc = index < expired ? now.UtcDateTime.AddDays(-8) : index < expired + retained ? now.UtcDateTime : null
        }));
        await db.SaveChangesAsync(token);
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"MonicaOutbox\"", token);
    }

    // The pre-refactor bounded cleanup algorithm, kept only as a measurement baseline; production has one implementation.
    private static async Task<int> OriginalCleanupAsync(IHost host, DateTimeOffset now, CancellationToken token)
    {
        await using var db = await Factory(host).CreateDbContextAsync(token);
        var cutoff = now.UtcDateTime.AddDays(-7);
        var rows = db.Set<OutboxMessage>();
        var keys = rows.Where(row => row.DeliveredAtUtc != null && row.DeliveredAtUtc < cutoff)
            .OrderBy(row => row.DeliveredAtUtc).Select(row => row.Sequence).Take(1000);
        var deleted = 0;
        var started = Stopwatch.GetTimestamp();
        do
        {
            var count = await rows.Where(row => keys.Contains(row.Sequence)).ExecuteDeleteAsync(token);
            deleted += count;
            if (count < 1000) break;
        } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        return deleted;
    }

    public sealed class ExternalOutboxContext(DbContextOptions<ExternalOutboxContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<ExternalOutboxContext>(options, services);

    private sealed class OutboxCommands : DbCommandInterceptor
    {
        private int _deletes;
        private int _leaseReads;
        private int _deliveryScans;
        private int _statusQueries;
        private readonly SemaphoreSlim _scanObserved = new(0);
        public bool BlockDelete { get; set; }
        public bool BlockAutocommitDelete { get; init; }
        public int Deletes => Volatile.Read(ref _deletes);
        public int LeaseReads => Volatile.Read(ref _leaseReads);
        public int StatusQueries => Volatile.Read(ref _statusQueries);
        public TaskCompletionSource DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Interlocked.Exchange(ref _deletes, 0); Interlocked.Exchange(ref _leaseReads, 0); }

        public async Task WaitForDeliveryScansAsync(int count, CancellationToken token)
        {
            while (Volatile.Read(ref _deliveryScans) < count)
                Assert.True(await _scanObserved.WaitAsync(TimeSpan.FromSeconds(5), token));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MonicaLeases", StringComparison.Ordinal)) Interlocked.Increment(ref _leaseReads);
            if (command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal)
                && command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _statusQueries);
            if (command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal)
                && (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
                    || command.CommandText.TrimStart().StartsWith("WITH", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _deliveryScans);
                _scanObserved.Release();
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _deletes);
                if (BlockDelete && (BlockAutocommitDelete || command.Transaction is not null))
                {
                    DeleteEntered.TrySetResult();
                    await AllowDelete.Task.WaitAsync(cancellationToken);
                }
            }
            return result;
        }
    }
}
