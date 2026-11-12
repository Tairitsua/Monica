using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.Outbox;

public sealed class OutboxBatchDeliveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(32, 1)]
    [InlineData(205, 3)]
    public async Task DrainAsync_WhenAcceptedWithoutBulkProvider_ShouldUseBoundedAcknowledgments(int count, int expectedCommands)
    {
        var time = new OutboxTestTimeProvider();
        var commands = new AcknowledgmentCommands();
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, count, new EventBatchLimits(count), commands);
        await SeedAsync(app, time, count + 1);

        Assert.Equal(count, await app.DrainOutboxAsync<TestRepositoryDbContext>(count, Token));
        Assert.Equal(expectedCommands, commands.Attempts);
        Assert.Equal(count, commands.Updated.Sum());
        Assert.All(commands.Updated, affected => Assert.InRange(affected, 1, 100));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
            Assert.All(rows.Take(count), row =>
            {
                Assert.NotNull(row.DeliveredAtUtc);
                Assert.Null(row.LeaseId);
                Assert.Null(row.LeaseUntilUtc);
                Assert.Null(row.LastError);
                Assert.Equal(1, row.Attempts);
            });
            Assert.Null(rows[^1].DeliveredAtUtc);
            Assert.Null(rows[^1].LeaseId);
            Assert.Equal(0, rows[^1].Attempts);
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_WhenAcknowledgmentThrows_ShouldNotReleaseClaimsOrRepeatUpdates(bool committed)
    {
        var time = new OutboxTestTimeProvider();
        var commands = new AcknowledgmentCommands
        {
            FailBeforeAttempt = committed ? 0 : 1,
            FailAfterAttempt = committed ? 1 : 0
        };
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 32, interceptor: commands);
        await SeedAsync(app, time, 32);

        Assert.Equal(0, await app.DrainOutboxAsync<TestRepositoryDbContext>(32, Token));
        Assert.Equal(1, commands.Attempts);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().ToListAsync(ct);
            Assert.All(rows, row =>
            {
                Assert.Equal(committed, row.DeliveredAtUtc.HasValue);
                Assert.Equal(!committed, row.LeaseId.HasValue);
                Assert.Equal(!committed, row.LeaseUntilUtc.HasValue);
                Assert.Null(row.NextAttemptAtUtc);
                Assert.Equal(1, row.Attempts);
                if (committed) Assert.Null(row.LastError);
                else Assert.Contains("Acknowledgment database failure", row.LastError);
            });
        }, Token);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await app.DrainOutboxAsync<TestRepositoryDbContext>(32, Token));
        Assert.Single(probe.Attempts);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(committed ? 0 : 32, await app.DrainOutboxAsync<TestRepositoryDbContext>(32, Token));
        var sends = probe.Attempts.ToArray();
        Assert.Equal(committed ? 1 : 2, sends.Length);
        if (!committed)
            Assert.Equal(sends[0].Messages.Select(message => message.Metadata), sends[1].Messages.Select(message => message.Metadata));
    }

    [Fact]
    public async Task DrainAsync_WhenOneAcknowledgmentChunkFails_ShouldKeepOtherChunksAndRetryOnlyUnacknowledgedRows()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new AcknowledgmentCommands { FailBeforeAttempt = 2 };
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 205, new EventBatchLimits(205), commands);
        await SeedAsync(app, time, 205);

        Assert.Equal(105, await app.DrainOutboxAsync<TestRepositoryDbContext>(205, Token));
        Assert.Equal(3, commands.Attempts);
        Assert.Equal([100, 5], commands.Updated);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(105, await db.Set<OutboxMessage>().CountAsync(row => row.DeliveredAtUtc != null, ct));
            Assert.Equal(100, await db.Set<OutboxMessage>().CountAsync(row => row.LeaseId != null
                && row.DeliveredAtUtc == null && row.NextAttemptAtUtc == null, ct));
        }, Token);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await app.DrainOutboxAsync<TestRepositoryDbContext>(205, Token));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(100, await app.DrainOutboxAsync<TestRepositoryDbContext>(205, Token));
        var replay = probe.Attempts.Last();
        Assert.Equal(probe.Attempts.First().Messages.Skip(100).Take(100).Select(message => message.Metadata),
            replay.Messages.Select(message => message.Metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_WhenAcknowledgmentIsCanceled_ShouldHoldCapacityUntilCommandCompletes(bool cancelCaller)
    {
        var time = new OutboxTestTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new AcknowledgmentCommands();
        commands.Before = async ct =>
        {
            if (commands.Attempts != 1) return;
            entered.TrySetResult();
            await release.Task;
            ct.ThrowIfCancellationRequested();
        };
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 32, interceptor: commands);
        await SeedAsync(app, time, 33);
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var draining = dispatcher.DrainAsync(32, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var attempt = Assert.Single(probe.Attempts);
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining);
            }
            else
            {
                var timeout = await time.TakeDelayAsync(TimeSpan.FromSeconds(29), Token);
                time.Advance(TimeSpan.FromSeconds(29));
                timeout.Fire();
                Assert.Equal(0, await draining.WaitAsync(TimeSpan.FromSeconds(10), Token));
            }
            Assert.False(attempt.Disposed.Task.IsCompleted);
            Assert.Equal(0, await dispatcher.DrainAsync(1, Token));
            release.TrySetResult();
            await attempt.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
            bound.CancelAfter(TimeSpan.FromSeconds(10));
            while (await dispatcher.DrainAsync(1, bound.Token) == 0) await Task.Yield();
            await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            {
                var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
                Assert.All(rows.Take(32), row =>
                {
                    Assert.Null(row.DeliveredAtUtc);
                    Assert.NotNull(row.LeaseId);
                    Assert.Equal(1, row.Attempts);
                    Assert.Null(row.NextAttemptAtUtc);
                });
                Assert.NotNull(rows[^1].DeliveredAtUtc);
            }, Token);
        }
        finally
        {
            release.TrySetResult();
            await caller.CancelAsync();
            try { await draining; }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            foreach (var attempt in probe.Attempts)
                await attempt.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task DrainAsync_WhenBatchPartiallySucceeds_ShouldSettleIndividuallyAndReplayOriginalMessages()
    {
        var time = new OutboxTestTimeProvider();
        var probe = new Probe
        {
            Send = (messages, _) => Task.FromResult<IReadOnlyList<EventSendResult>>(messages.Count == 3
                ? [EventSendResult.Accepted, EventSendResult.Failed(new IOException("Rejected")),
                    EventSendResult.Unknown(new IOException("Acknowledgment lost"))]
                : [EventSendResult.Accepted])
        };
        await using var app = await CreateAsync(time, probe, 3);
        await SeedAsync(app, time, 3);
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();

        Assert.Equal(1, await dispatcher.DrainAsync(3, Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
            Assert.NotNull(rows[0].DeliveredAtUtc);
            Assert.Null(rows[0].LeaseId);
            Assert.Null(rows[1].DeliveredAtUtc);
            Assert.Null(rows[1].LeaseId);
            Assert.NotNull(rows[1].NextAttemptAtUtc);
            Assert.Null(rows[2].DeliveredAtUtc);
            Assert.NotNull(rows[2].LeaseId);
            Assert.All(rows.Skip(1), row => Assert.NotNull(row.LastError));
        }, Token);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await dispatcher.DrainAsync(3, Token));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await dispatcher.DrainAsync(3, Token));
        var sends = probe.Attempts.ToArray();
        Assert.True(sends[0].IsBatch);
        Assert.Equal(3, sends[0].Messages.Length);
        for (var index = 1; index < sends.Length; index++)
        {
            var replay = Assert.Single(sends[index].Messages);
            Assert.Equal(sends[0].Messages[index].Metadata, replay.Metadata);
            Assert.Equal(sends[0].Messages[index].Body.ToArray(), replay.Body.ToArray());
        }
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
            Assert.All(rows, row => Assert.NotNull(row.DeliveredAtUtc));
            Assert.Equal([1, 2, 2], rows.Select(row => row.Attempts));
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_WhenBatchThrowsOrOmitsResults_ShouldRetainClaimsWithoutSingleSendFallback(bool missingResult)
    {
        var time = new OutboxTestTimeProvider();
        var probe = new Probe
        {
            Send = (_, _) => missingResult ? Task.FromResult<IReadOnlyList<EventSendResult>>([EventSendResult.Accepted])
                : Task.FromException<IReadOnlyList<EventSendResult>>(new IOException("Response lost"))
        };
        await using var app = await CreateAsync(time, probe, 2);
        await SeedAsync(app, time, 2);

        Assert.Equal(0, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().DrainAsync(2, Token));
        Assert.True(Assert.Single(probe.Attempts).IsBatch);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().ToListAsync(ct);
            Assert.All(rows, row =>
            {
                Assert.Null(row.DeliveredAtUtc);
                Assert.NotNull(row.LeaseId);
                Assert.NotNull(row.LastError);
            });
        }, Token);
    }

    [Fact]
    public async Task DrainAsync_WhenClaimTokenChangesDuringBatch_ShouldAcknowledgeOnlyStillOwnedRows()
    {
        var time = new OutboxTestTimeProvider();
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 2);
        await SeedAsync(app, time, 2);
        var replacement = Guid.NewGuid();
        probe.Send = async (_, _) =>
        {
            await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
                await db.Set<OutboxMessage>().Where(row => row.MessageId == "message-1")
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseId, replacement), ct), Token);
            return [EventSendResult.Accepted, EventSendResult.Accepted];
        };

        Assert.Equal(1, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().DrainAsync(2, Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
            Assert.NotNull(rows[0].DeliveredAtUtc);
            Assert.Null(rows[1].DeliveredAtUtc);
            Assert.Equal(replacement, rows[1].LeaseId);
        }, Token);
    }

    [Fact]
    public async Task DrainAsync_WhenRoutesAndLimitsDiffer_ShouldKeepGroupsWithinTheirTransportBoundary()
    {
        var time = new OutboxTestTimeProvider();
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 8, new EventBatchLimits(2, 400));
        await SeedAsync(app, time, 8, (row, index) =>
        {
            row.TopicName = index < 3 ? "first" : "second";
            row.ServiceKey = index is 5 or 6 ? "secondary" : null;
            if (index == 7) row.Body = new byte[1000];
        });

        Assert.Equal(8, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().DrainAsync(8, Token));
        var attempts = probe.Attempts.ToArray();
        Assert.Equal([2, 1, 2, 2, 1], attempts.Select(attempt => attempt.Messages.Length));
        Assert.All(attempts, attempt =>
        {
            Assert.Single(attempt.Messages.Select(message => message.Metadata.TopicName).Distinct());
            Assert.Single(attempt.Messages.Select(message => message.Metadata.ServiceKey).Distinct());
        });
        Assert.False(attempts[^1].IsBatch);
        Assert.Equal("message-7", Assert.Single(attempts[^1].Messages).Metadata.MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_WhenBatchIgnoresCancellation_ShouldHoldAllSlotsAndNeverAcknowledgeLate(bool cancelCaller)
    {
        var time = new OutboxTestTimeProvider();
        var completion = new TaskCompletionSource<IReadOnlyList<EventSendResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe { Send = (messages, _) => messages.Count == 2 ? completion.Task
            : Task.FromResult<IReadOnlyList<EventSendResult>>([EventSendResult.Accepted]) };
        await using var app = await CreateAsync(time, probe, 2);
        await SeedAsync(app, time, 3);
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var draining = dispatcher.DrainAsync(3, caller.Token);
        var attempt = await probe.Started.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        try
        {
            Assert.True(attempt.IsBatch);
            Assert.Equal(2, attempt.Messages.Length);
            Assert.Equal(0, await dispatcher.DrainAsync(3, Token));
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining);
            }
            else
            {
                var timeout = await time.TakeDelayAsync(TimeSpan.FromSeconds(29), Token);
                time.Advance(TimeSpan.FromSeconds(29));
                timeout.Fire();
                Assert.Equal(0, await draining.WaitAsync(TimeSpan.FromSeconds(10), Token));
            }
            Assert.False(attempt.Disposed.Task.IsCompleted);
            Assert.Equal(0, await dispatcher.DrainAsync(3, Token));
            Assert.Single(probe.Attempts);
            completion.SetResult([EventSendResult.Accepted, EventSendResult.Accepted]);
            await attempt.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
            bound.CancelAfter(TimeSpan.FromSeconds(10));
            while (await dispatcher.DrainAsync(1, bound.Token) == 0) await Task.Yield();
            await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            {
                var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
                Assert.All(rows.Take(2), row =>
                {
                    Assert.Null(row.DeliveredAtUtc);
                    Assert.NotNull(row.LeaseId);
                    Assert.Equal(1, row.Attempts);
                });
                Assert.NotNull(rows[2].DeliveredAtUtc);
            }, Token);
        }
        finally
        {
            completion.TrySetResult([EventSendResult.Accepted, EventSendResult.Accepted]);
            await caller.CancelAsync();
            try { await draining; }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            await attempt.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task DrainAsync_WhenCapacityIsOne_ShouldKeepSingleSendBehavior()
    {
        var time = new OutboxTestTimeProvider();
        var probe = new Probe();
        await using var app = await CreateAsync(time, probe, 1);
        await SeedAsync(app, time, 3);
        Assert.Equal(3, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().DrainAsync(3, Token));
        Assert.Equal(3, probe.Attempts.Count);
        Assert.All(probe.Attempts, attempt => Assert.False(attempt.IsBatch));
    }

    private static Task<MonicaTestApplication> CreateAsync(OutboxTestTimeProvider time, Probe probe, int concurrency,
        EventBatchLimits? limits = null, IInterceptor? interceptor = null) => new RepositoryScenarioFactory(interceptor: interceptor, configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.RemoveAll<IEventTransport>();
            services.AddScoped<IEventTransport>(_ => new Transport(probe, limits ?? new()));
            services.AddKeyedScoped<IEventTransport>("secondary", (_, _) => new Transport(probe, limits ?? new()));
        }, configureOutbox: options =>
        {
            options.DeliveryLease = TimeSpan.FromSeconds(30);
            options.DeliveryConcurrency = concurrency;
        }).CreateAsync(cancellationToken: Token);

    private static Task SeedAsync(MonicaTestApplication app, TimeProvider time, int count,
        Action<OutboxMessage, int>? configure = null) => app.SeedAsync<TestRepositoryDbContext, int>(async (db, ct) =>
        {
            for (var index = 0; index < count; index++)
            {
                var row = new OutboxMessage
                {
                    MessageId = $"message-{index}", Source = "tests", EventName = "tests.batch", TopicName = "tests",
                    Scope = EventSubscriptionScope.Distributed, Body = "{}"u8.ToArray(),
                    TransportKey = $"key-{index}", TraceParent = "trace", TraceState = "state",
                    CreatedAtUtc = time.GetUtcNow().UtcDateTime
                };
                configure?.Invoke(row, index);
                db.Add(row);
            }
            return await db.SaveChangesAsync(ct);
        }, Token);

    private sealed class AcknowledgmentCommands : DbCommandInterceptor
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public ConcurrentQueue<int> Updated { get; } = new();
        public int FailBeforeAttempt { get; init; }
        public int FailAfterAttempt { get; init; }
        public Func<CancellationToken, Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!IsAcknowledgment(command)) return result;
            var attempt = Interlocked.Increment(ref _attempts);
            if (Before is not null) await Before(cancellationToken);
            if (attempt == FailBeforeAttempt) throw new IOException("Acknowledgment database failure before execution.");
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (IsAcknowledgment(command))
            {
                Updated.Enqueue(result);
                if (Attempts == FailAfterAttempt) throw new IOException("Acknowledgment database failure after execution.");
            }
            return ValueTask.FromResult(result);
        }

        private static bool IsAcknowledgment(DbCommand command)
        {
            var sql = command.CommandText;
            var set = sql.IndexOf("SET ", StringComparison.Ordinal);
            var where = sql.IndexOf("WHERE ", StringComparison.Ordinal);
            return sql.StartsWith("UPDATE ", StringComparison.Ordinal) && sql.Contains("MonicaOutbox", StringComparison.Ordinal)
                && set >= 0 && where > set && sql[set..where].Contains("\"DeliveredAtUtc\"", StringComparison.Ordinal);
        }
    }

    private sealed class Probe
    {
        public ConcurrentQueue<Attempt> Attempts { get; } = new();
        public Channel<Attempt> Started { get; } = Channel.CreateUnbounded<Attempt>();
        public Func<IReadOnlyList<EventMessage>, CancellationToken, Task<IReadOnlyList<EventSendResult>>> Send { get; set; }
            = (messages, _) => Task.FromResult<IReadOnlyList<EventSendResult>>(messages.Select(_ => EventSendResult.Accepted).ToArray());
    }

    private sealed record Attempt(EventMessage[] Messages, bool IsBatch)
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Transport(Probe probe, EventBatchLimits limits) : IEventBatchTransport, IAsyncDisposable
    {
        private Attempt? _attempt;
        public EventBatchLimits BatchLimits => limits;

        public async Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        {
            var results = await Send([message], false, cancellationToken);
            if (results[0].Error is { } error) throw error;
        }

        public Task<IReadOnlyList<EventSendResult>> SendBatchAsync(IReadOnlyList<EventMessage> messages, CancellationToken cancellationToken)
        {
            limits.Validate(messages);
            return Send(messages, true, cancellationToken);
        }

        private Task<IReadOnlyList<EventSendResult>> Send(IReadOnlyList<EventMessage> messages, bool batch, CancellationToken token)
        {
            _attempt = new Attempt(messages.ToArray(), batch);
            probe.Attempts.Enqueue(_attempt);
            probe.Started.Writer.TryWrite(_attempt);
            return probe.Send(messages, token);
        }

        public ValueTask DisposeAsync()
        {
            _attempt?.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
