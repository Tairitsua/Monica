using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
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

public sealed class OutboxDeliveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DrainAsync_WhenSendIgnoresCancellation_ShouldRetainCapacityAndScopeUntilItFinishes(
        bool cancelCaller, bool failLate)
    {
        var time = new OutboxTestTimeProvider();
        var probe = new SendProbe();
        await using var app = await CreateAsync(time, probe);
        await SeedAsync(app, time, 3);
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var draining = dispatcher.DrainAsync(3, caller.Token);
        var first = await probe.Started.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        try
        {
            // A concurrent manual pass shares capacity with the already running pass.
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

            for (var pass = 0; pass < 3; pass++)
                Assert.Equal(0, await dispatcher.DrainAsync(3, Token));
            Assert.False(first.Disposed.Task.IsCompleted);
            Assert.Equal(1, probe.Sends);
            await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            {
                var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
                Assert.NotNull(rows[0].LeaseId);
                Assert.Equal(1, rows[0].Attempts);
                Assert.All(rows, row => Assert.Null(row.DeliveredAtUtc));
                Assert.All(rows.Skip(1), row => Assert.Equal(0, row.Attempts));
            }, Token);

            if (failLate) first.Completion.TrySetException(new IOException("Late broker failure"));
            else first.Completion.TrySetResult();
            await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            // Scope disposal and slot return are adjacent but not atomic; observe eventual admission without sleeps.
            Assert.Equal(1, await DrainWhenAvailableAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(2, probe.Sends);
            await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            {
                var rows = await db.Set<OutboxMessage>().OrderBy(row => row.Sequence).ToListAsync(ct);
                Assert.Null(rows[0].DeliveredAtUtc);
                Assert.NotNull(rows[0].LeaseId);
                Assert.NotNull(rows[1].DeliveredAtUtc);
                Assert.Equal(0, rows[2].Attempts);
            }, Token);
        }
        finally
        {
            first.Completion.TrySetResult();
            await caller.CancelAsync();
            try { await draining; }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task DrainAsync_WhenNoRowsAreClaimed_ShouldReturnReservedCapacity()
    {
        var time = new OutboxTestTimeProvider();
        var probe = new SendProbe(blockFirst: false);
        await using var app = await CreateAsync(time, probe);
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();
        Assert.Equal(0, await dispatcher.DrainAsync(3, Token));
        await SeedAsync(app, time, 3);
        Assert.Equal(3, await dispatcher.DrainAsync(3, Token));
    }

    private static async Task<int> DrainWhenAvailableAsync(OutboxDispatcher<TestRepositoryDbContext> dispatcher)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var delivered = await dispatcher.DrainAsync(1, deadline.Token);
            if (delivered > 0) return delivered;
            await Task.Yield();
        }
    }

    private static Task<MonicaTestApplication> CreateAsync(OutboxTestTimeProvider time, SendProbe probe) =>
        new RepositoryScenarioFactory(configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.RemoveAll<IEventTransport>();
            services.AddScoped<IEventTransport>(_ => new ScopedTransport(probe));
        }, configureOutbox: options => options.DeliveryLease = TimeSpan.FromSeconds(30))
        .CreateAsync(cancellationToken: Token);

    private static async Task SeedAsync(MonicaTestApplication app, TimeProvider time, int count) =>
        await app.SeedAsync<TestRepositoryDbContext, int>(async (db, ct) =>
        {
            db.AddRange(Enumerable.Range(0, count).Select(index => new OutboxMessage
            {
                MessageId = $"message-{index}", Source = "tests", EventName = "tests.delivery", TopicName = "tests",
                Scope = EventSubscriptionScope.Distributed, Body = "{}"u8.ToArray(), CreatedAtUtc = time.GetUtcNow().UtcDateTime
            }));
            return await db.SaveChangesAsync(ct);
        }, Token);

    private sealed class SendProbe(bool blockFirst = true)
    {
        private int _sends;
        public int Sends => Volatile.Read(ref _sends);
        public Channel<Attempt> Started { get; } = Channel.CreateUnbounded<Attempt>();

        public Attempt Start()
        {
            var attempt = new Attempt();
            if (Interlocked.Increment(ref _sends) != 1 || !blockFirst) attempt.Completion.TrySetResult();
            Started.Writer.TryWrite(attempt);
            return attempt;
        }
    }

    private sealed class Attempt
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScopedTransport(SendProbe probe) : IEventTransport, IAsyncDisposable
    {
        private Attempt? _attempt;
        public Task SendAsync(EventMessage message, CancellationToken cancellationToken) =>
            (_attempt = probe.Start()).Completion.Task;

        public ValueTask DisposeAsync()
        {
            _attempt?.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
