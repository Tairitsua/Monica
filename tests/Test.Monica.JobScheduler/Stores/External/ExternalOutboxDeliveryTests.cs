using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

public sealed partial class ExternalOutboxLeaseTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, -7)]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, 7)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, -7)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, 7)]
    public async Task Delivery_WhenTheSenderFailsOnASkewedHost_ShouldScheduleRetryUsingDatabaseUtc(string provider, int offsetDays)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            using var host = CreateHost(fixture.Database, new SkewedUtcTimeProvider(TimeSpan.FromDays(offsetDays)),
                new OutboxCommands(), worker: false, transport: new FailingDeliveryTransport(), configureOutbox: options =>
                {
                    options.RetryBaseDelay = TimeSpan.FromSeconds(10);
                    options.RetryMaxDelay = TimeSpan.FromSeconds(10);
                });
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 1, token);
            await using var db = await Factory(host).CreateDbContextAsync(token);
            var before = DateTime.SpecifyKind(await db.Database
                .SqlQueryRaw<DateTime>("SELECT CURRENT_TIMESTAMP AS \"Value\"").SingleAsync(token), DateTimeKind.Utc);
            Assert.Equal(0, await Dispatcher(host).DrainAsync(1, token));
            var after = DateTime.SpecifyKind(await db.Database
                .SqlQueryRaw<DateTime>("SELECT CURRENT_TIMESTAMP AS \"Value\"").SingleAsync(token), DateTimeKind.Utc);
            var row = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(token);
            Assert.Null(row.DeliveredAtUtc);
            Assert.Null(row.LeaseId);
            Assert.Null(row.LeaseUntilUtc);
            Assert.Equal(1, row.Attempts);
            Assert.NotNull(row.NextAttemptAtUtc);
            // The configured 10-second delay is jittered to 8-10 seconds; timestamp(0) adds rounding slack.
            Assert.InRange(row.NextAttemptAtUtc.GetValueOrDefault(), before.AddSeconds(7), after.AddSeconds(11));
            Assert.Contains("delivery-test failure", row.LastError);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenCalledInsideAnAmbientTransaction_ShouldRejectBeforeClaimingOrSending(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport();
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(),
                worker: false, transport: transport);
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 1, token);
            using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher(host).DrainAsync(1, token));
                Assert.Contains("ambient transaction", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Empty(transport.MessageIds);
            var row = Assert.Single(await ReadDeliveryAsync(host, token));
            Assert.Null(row.DeliveredAtUtc);
            Assert.Null(row.LeaseId);
            Assert.Equal(0, row.Attempts);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenSixteenHostsContend_ShouldSendDisjointMessages(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            const int replicas = 16;
            const int batchSize = 4;
            var transport = new DeliveryTransport(blockedSends: replicas);
            var hosts = Enumerable.Range(0, replicas).Select(_ => CreateHost(fixture.Database,
                TimeProvider.System, new OutboxCommands(), worker: false, transport: transport)).ToArray();
            Task<int>[] draining = [];
            try
            {
                await CreateOutboxSchemaAsync(hosts[0], token);
                await SeedDeliveryAsync(hosts[0], replicas * batchSize, token);
                draining = hosts.Select(host => Dispatcher(host).DrainAsync(batchSize, token)).ToArray();
                // The pool has only three connections. All sixteen hosts must release their claim connection
                // before transport acceptance, otherwise this rendezvous cannot complete.
                await transport.AllBlocked.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                Assert.Equal(replicas, transport.MessageIds.Distinct().Count());
                transport.AllowSends.TrySetResult();
                Assert.Equal(replicas * batchSize, (await Task.WhenAll(draining)).Sum());
                Assert.Equal(replicas * batchSize, transport.MessageIds.Count);
                Assert.Equal(replicas * batchSize, transport.MessageIds.Distinct().Count());
                var stored = await ReadDeliveryAsync(hosts[0], token);
                Assert.All(stored, row =>
                {
                    Assert.NotNull(row.DeliveredAtUtc);
                    Assert.Equal(1, row.Attempts);
                    Assert.Null(row.LeaseId);
                });
            }
            finally
            {
                transport.AllowSends.TrySetResult();
                try { await Task.WhenAll(draining); }
                finally { foreach (var host in hosts) host.Dispose(); }
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenTheOldestRowIsPhysicallyLocked_ShouldSendTheNextDueRow(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport();
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(),
                worker: false, transport: transport);
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 2, token);
            var before = await ReadDeliveryAsync(host, token);
            await using var connection = await fixture.Database.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT "Sequence" FROM "MonicaOutbox" ORDER BY "Sequence" LIMIT 1 FOR UPDATE
                """;
            Assert.Equal(before[0].Sequence, Convert.ToInt64(await command.ExecuteScalarAsync(token)));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                Assert.Equal(1, await Dispatcher(host).DrainAsync(1, deadline.Token));
                Assert.Equal([before[1].MessageId], transport.MessageIds);
                var stored = await ReadDeliveryAsync(host, token);
                Assert.Null(stored[0].DeliveredAtUtc);
                Assert.Equal(0, stored[0].Attempts);
                Assert.NotNull(stored[1].DeliveredAtUtc);
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            Assert.Equal(1, await Dispatcher(host).DrainAsync(1, token));
            Assert.Equal([before[1].MessageId, before[0].MessageId], transport.MessageIds);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, -7)]
    [InlineData(ExternalSchedulerDatabase.POSTGRES, 7)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, -7)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE, 7)]
    public async Task Delivery_WhenHostUtcIsSkewed_ShouldUseDatabaseDueTimesAndPreserveExcludedRows(
        string provider, int offsetDays)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport();
            using var host = CreateHost(fixture.Database, new SkewedUtcTimeProvider(TimeSpan.FromDays(offsetDays)),
                new OutboxCommands(), worker: false, transport: transport);
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 4, token);
            Guid activeToken = Guid.NewGuid();
            DateTime databaseNow;
            await using (var db = await Factory(host).CreateDbContextAsync(token))
            {
                databaseNow = DateTime.SpecifyKind(await db.Database
                    .SqlQueryRaw<DateTime>("SELECT CURRENT_TIMESTAMP AS \"Value\"").SingleAsync(token), DateTimeKind.Utc);
                // Repository timestamps currently use whole-second storage precision.
                databaseNow = new DateTime(databaseNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(token);
                rows[0].NextAttemptAtUtc = databaseNow.AddHours(-1);
                rows[0].LeaseUntilUtc = databaseNow.AddHours(-1);
                rows[0].LeaseId = Guid.NewGuid();
                rows[1].NextAttemptAtUtc = databaseNow.AddHours(1);
                rows[2].LeaseUntilUtc = databaseNow.AddHours(1);
                rows[2].LeaseId = activeToken;
                rows[3].DeliveredAtUtc = databaseNow;
                await db.SaveChangesAsync(token);
            }
            Assert.Equal(1, await Dispatcher(host).DrainAsync(10, token));
            var stored = await ReadDeliveryAsync(host, token);
            Assert.Equal([stored[0].MessageId], transport.MessageIds);
            Assert.NotNull(stored[0].DeliveredAtUtc);
            Assert.Equal(1, stored[0].Attempts);
            Assert.Null(stored[1].DeliveredAtUtc);
            Assert.Equal(databaseNow.AddHours(1), stored[1].NextAttemptAtUtc);
            Assert.Equal(0, stored[1].Attempts);
            Assert.Null(stored[2].DeliveredAtUtc);
            Assert.Equal(activeToken, stored[2].LeaseId);
            Assert.Equal(databaseNow.AddHours(1), stored[2].LeaseUntilUtc);
            Assert.Equal(0, stored[2].Attempts);
            Assert.Equal(databaseNow, stored[3].DeliveredAtUtc);
            Assert.Equal(0, stored[3].Attempts);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenOneSenderTimesOut_ShouldRetainItsSlotAndUseTheOtherSlotForLaterRows(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport(blockedSends: 1);
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), worker: false,
                transport: transport, configureOutbox: options =>
                {
                    options.DeliveryLease = TimeSpan.FromSeconds(2);
                    options.DeliveryConcurrency = 2;
                });
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 4, token);
            var draining = Dispatcher(host).DrainAsync(4, token);
            try
            {
                Assert.Equal(3, await draining.WaitAsync(TimeSpan.FromSeconds(15), token));
                Assert.False(transport.AllowSends.Task.IsCompleted);
                Assert.Equal(4, transport.MessageIds.Count);
                var stored = await ReadDeliveryAsync(host, token);
                Assert.Null(stored[0].DeliveredAtUtc);
                Assert.NotNull(stored[0].LeaseId);
                Assert.NotNull(stored[0].LeaseUntilUtc);
                Assert.All(stored, row => Assert.Equal(1, row.Attempts));
                Assert.All(stored.Skip(1), row =>
                {
                    Assert.NotNull(row.DeliveredAtUtc);
                    Assert.Null(row.LeaseId);
                });
            }
            finally
            {
                transport.AllowSends.TrySetResult();
                await draining;
                using var settled = CancellationTokenSource.CreateLinkedTokenSource(token);
                settled.CancelAfter(TimeSpan.FromSeconds(10));
                while ((await ReadDeliveryAsync(host, settled.Token))[0].LastError is null) await Task.Yield();
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenCancelledDuringTheFirstSend_ShouldLeaveLaterRowsUnreserved(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport(blockedSends: 1);
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(),
                worker: false, transport: transport);
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, 4, token);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var draining = Dispatcher(host).DrainAsync(4, cancelled.Token);
            try
            {
                await transport.AllBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining);
                var stored = await ReadDeliveryAsync(host, token);
                Assert.Equal([stored[0].MessageId], transport.MessageIds);
                Assert.Null(stored[0].DeliveredAtUtc);
                Assert.NotNull(stored[0].LeaseId);
                Assert.Equal(1, stored[0].Attempts);
                Assert.All(stored.Skip(1), row =>
                {
                    Assert.Null(row.DeliveredAtUtc);
                    Assert.Null(row.LeaseId);
                    Assert.Null(row.LeaseUntilUtc);
                    Assert.Equal(0, row.Attempts);
                });
            }
            finally
            {
                cancelled.Cancel();
                transport.AllowSends.TrySetResult();
                try { await draining; }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenOneOfEightSlotsHangs_ShouldRefillOtherSlotsBeforeItsDeadline(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            const int count = 32;
            var transport = new DeliveryTransport(blockedSends: 1, expectedSends: count);
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), worker: false,
                transport: transport, configureOutbox: options =>
                {
                    options.DeliveryConcurrency = 8;
                    options.DeliveryLease = TimeSpan.FromSeconds(20);
                });
            await CreateOutboxSchemaAsync(host, token);
            await SeedDeliveryAsync(host, count, token);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var draining = Dispatcher(host).DrainAsync(count, cancelled.Token);
            try
            {
                await transport.AllSent.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.False(transport.AllowSends.Task.IsCompleted);
                Assert.False(draining.IsCompleted);
                var stored = await ReadDeliveryAsync(host, token);
                Assert.True(stored.Count(row => row.DeliveredAtUtc is not null) >= count - 8);
                var blocked = Assert.Single(stored, row => row.MessageId == transport.BlockedMessageId);
                Assert.Null(blocked.DeliveredAtUtc);
                Assert.NotNull(blocked.LeaseId);
                Assert.Equal(1, blocked.Attempts);
                Assert.Equal(count, transport.MessageIds.Distinct().Count());
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining);
                var after = Assert.Single(await ReadDeliveryAsync(host, token), row => row.Sequence == blocked.Sequence);
                Assert.Equal(blocked.LeaseId, after.LeaseId);
                Assert.Equal(blocked.LeaseUntilUtc, after.LeaseUntilUtc);
                Assert.Null(after.DeliveredAtUtc);
            }
            finally
            {
                cancelled.Cancel();
                transport.AllowSends.TrySetResult();
                try { await draining; }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task Delivery_WhenMeasuringNativeClaims_ShouldReportCostsAgainstThePreviousCasProtocol(string provider)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("MONICA_OUTBOX_DELIVERY_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            Assert.Skip("Set MONICA_OUTBOX_DELIVERY_PERF_OUTPUT to an existing absolute directory for delivery measurements.");
        Assert.True(Path.IsPathFullyQualified(outputDirectory) && Directory.Exists(outputDirectory));
        const int messages = 128;
        const int batchSize = 32;
        const int samples = 5;
        var token = TestContext.Current.CancellationToken;
        var results = new List<object>();
        foreach (var replicas in new[] { 1, 4, 16 })
        {
            var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
            await ExternalSchedulerScenario.RunAsync(fixture, async () =>
            {
                var cases = new[] { (Previous: true, Concurrency: 1), (Previous: false, Concurrency: 1),
                    (Previous: false, Concurrency: 8) };
                foreach (var measurement in cases)
                {
                    var commands = new ExternalStoreCommandCounter();
                    var transport = new DeliveryTransport();
                    var hosts = new List<IHost>();
                    try
                    {
                        for (var replica = 0; replica < replicas; replica++)
                            hosts.Add(CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(),
                                worker: false, allCommands: commands, transport: transport,
                                configureOutbox: options => options.DeliveryConcurrency = measurement.Concurrency));
                        if (measurement.Previous) await CreateOutboxSchemaAsync(hosts[0], token);
                        var participants = hosts.ToArray();
                        // Warm EF query compilation, each host, and the shared provider pool before recording costs.
                        _ = await MeasureDeliveryAsync(participants, transport, commands, measurement.Previous,
                            messages, batchSize, token);
                        for (var iteration = 0; iteration < samples; iteration++)
                        {
                            var sample = await MeasureDeliveryAsync(participants, transport, commands, measurement.Previous,
                                messages, batchSize, token);
                            var result = new
                            {
                                provider, serverVersion = fixture.Database.ServerVersion, replicas,
                                algorithm = measurement.Previous ? "previous-serial-cas" : "native-sliding-window",
                                deliveryConcurrency = measurement.Concurrency, iteration, sample
                            };
                            results.Add(result);
                            TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(result));
                        }
                    }
                    finally { foreach (var host in hosts) host.Dispose(); }
                }
            });
        }
        var output = Path.Combine(outputDirectory, $"outbox-delivery-{provider}.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow, provider, poolLimit = 3, messageCount = messages,
            batchSize, samples, payloadBytes = 512, transport = "recording-immediate-acceptance", results
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static OutboxDispatcher<ExternalOutboxContext> Dispatcher(IHost host) =>
        host.Services.GetRequiredService<OutboxDispatcher<ExternalOutboxContext>>();

    // This is the previous production delivery protocol, retained only for the opt-in measurement below.
    // Its controlled transport always accepts; failure/retry correctness is covered through the real dispatcher.
    private static async Task<int> PreviousDeliveryAsync(IHost host, int maximumMessages, CancellationToken token)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExternalOutboxContext>();
        var rows = db.Set<OutboxMessage>();
        var now = DateTime.UtcNow;
        var candidates = await rows.AsNoTracking().Where(row => row.DeliveredAtUtc == null
                && (row.NextAttemptAtUtc == null || row.NextAttemptAtUtc <= now)
                && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .OrderBy(row => row.Sequence).Take(maximumMessages).ToArrayAsync(token);
        var delivered = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var lease = Guid.NewGuid();
            now = DateTime.UtcNow;
            var claimed = await rows.Where(row => row.Sequence == candidate.Sequence && row.DeliveredAtUtc == null
                    && (row.NextAttemptAtUtc == null || row.NextAttemptAtUtc <= now)
                    && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseId, lease)
                    .SetProperty(row => row.LeaseUntilUtc, now.AddMinutes(5))
                    .SetProperty(row => row.Attempts, row => row.Attempts + 1), token);
            if (claimed == 0) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var message = new EventMessage(new EventDeliveryMetadata(candidate.MessageId, candidate.Source,
                candidate.EventName, candidate.TopicName, candidate.ServiceKey, candidate.TraceParent, candidate.TraceState)
                { TransportKey = candidate.TransportKey }, candidate.Scope, candidate.Body.ToArray());
            await scope.ServiceProvider.GetRequiredService<IEventTransport>().SendAsync(message, deadline.Token)
                .WaitAsync(deadline.Token);
            var acknowledgedAt = DateTime.UtcNow;
            delivered += await rows.Where(row => row.Sequence == candidate.Sequence && row.LeaseId == lease
                    && row.DeliveredAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveredAtUtc, acknowledgedAt)
                    .SetProperty(row => row.LeaseId, (Guid?)null).SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(row => row.LastError, (string?)null), token);
        }
        return delivered;
    }

    private static async Task<DeliverySample> MeasureDeliveryAsync(IHost[] hosts, DeliveryTransport transport,
        ExternalStoreCommandCounter commands, bool previous, int messageCount, int batchSize, CancellationToken token)
    {
        await SeedDeliveryAsync(hosts[0], messageCount, token);
        transport.Reset();
        commands.Reset();
        var latencies = new ConcurrentQueue<double>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = hosts.Select(async host =>
        {
            await ready.Task;
            var total = 0;
            int delivered;
            do
            {
                var started = Stopwatch.GetTimestamp();
                delivered = previous ? await PreviousDeliveryAsync(host, batchSize, token)
                    : await Dispatcher(host).DrainAsync(batchSize, token);
                latencies.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                total += delivered;
            } while (delivered > 0);
            return total;
        }).ToArray();
        var elapsed = Stopwatch.StartNew();
        ready.SetResult();
        var counts = await Task.WhenAll(tasks);
        elapsed.Stop();
        var queries = commands.CommandsExecuted;
        var transactions = commands.TransactionStarts;
        var durations = latencies.Order().ToArray();
        Assert.Equal(messageCount, counts.Sum());
        Assert.Equal(messageCount, transport.MessageIds.Count);
        Assert.Equal(messageCount, transport.MessageIds.Distinct().Count());
        var stored = await ReadDeliveryAsync(hosts[0], token);
        Assert.All(stored, row => { Assert.NotNull(row.DeliveredAtUtc); Assert.Equal(1, row.Attempts); });
        return new DeliverySample(elapsed.Elapsed.TotalMilliseconds, messageCount / elapsed.Elapsed.TotalSeconds,
            queries.Count, queries.Count(sql => sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("MonicaOutbox", StringComparison.Ordinal)), transactions, counts,
            durations.Length, Percentile(durations, 0.50), Percentile(durations, 0.95), durations[^1]);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private sealed record DeliverySample(double ElapsedMs, double MessagesPerSecond, int SqlCommands,
        int ReturningClaims, int SqlTransactions, int[] DeliveriesPerReplica, int DrainCalls,
        double DrainP50Ms, double DrainP95Ms, double DrainMaxMs);

    private static async Task SeedDeliveryAsync(IHost host, int count, CancellationToken token)
    {
        await using var db = await Factory(host).CreateDbContextAsync(token);
        // Every caller owns this fixture database. Reset data only between completed samples.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MonicaOutbox\"", token);
        db.AddRange(Enumerable.Range(0, count).Select(index => new OutboxMessage
        {
            MessageId = $"delivery-{index}", Source = "delivery-test", EventName = "delivery.test", TopicName = "delivery.test",
            Body = new byte[512], Scope = EventSubscriptionScope.Distributed, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        }));
        await db.SaveChangesAsync(token);
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"MonicaOutbox\"", token);
    }

    private static async Task<OutboxMessage[]> ReadDeliveryAsync(IHost host, CancellationToken token)
    {
        await using var db = await Factory(host).CreateDbContextAsync(token);
        return await db.Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.Sequence).ToArrayAsync(token);
    }

    private sealed class DeliveryTransport(int blockedSends = 0, int expectedSends = 0) : IEventTransport
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private int _started;
        public IReadOnlyList<string> MessageIds => _messages.ToArray();
        public string? BlockedMessageId { get; private set; }
        public TaskCompletionSource AllBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSends { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        {
            _messages.Enqueue(message.Metadata.MessageId!);
            var started = Interlocked.Increment(ref _started);
            if (started == 1 && blockedSends > 0) BlockedMessageId = message.Metadata.MessageId;
            if (started == blockedSends) AllBlocked.TrySetResult();
            if (started == expectedSends) AllSent.TrySetResult();
            // Deliberately ignore cancellation: the dispatcher must bound a transport that does not cooperate.
            return started <= blockedSends ? AllowSends.Task : Task.CompletedTask;
        }

        public void Reset() { _messages.Clear(); Interlocked.Exchange(ref _started, 0); }
    }

    private sealed class SkewedUtcTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    }

    private sealed class FailingDeliveryTransport : IEventTransport
    {
        public Task SendAsync(EventMessage message, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("delivery-test failure"));
    }
}
