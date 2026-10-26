using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.EventBus.Abstractions;
using Monica.Repository.Outbox.Models;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.Outbox;

public sealed class OutboxWorkerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Worker_WhenIdle_ShouldPollDeliveryWithoutRunningMaintenanceEachTime()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new WorkerCommands();
        await using var app = await CreateAsync(time, commands);
        var poll = await time.TakeDelayAsync(PollInterval, Token);
        var cleanup = await time.TakeDelayAsync(CleanupInterval, Token);
        var status = await time.TakeDelayAsync(StatusInterval, Token);

        for (var i = 0; i < 3; i++)
        {
            poll.Fire();
            poll = await time.TakeDelayAsync(PollInterval, Token);
        }
        Assert.Equal(0, commands.CleanupCount);
        Assert.Equal(0, commands.StatusCount);

        status.Fire();
        _ = await time.TakeDelayAsync(StatusInterval, Token);
        Assert.Equal(1, commands.StatusCount);
        Assert.Equal(0, commands.CleanupCount);

        cleanup.Fire();
        _ = await time.TakeDelayAsync(CleanupInterval, Token);
        Assert.Equal(1, commands.CleanupCount);
        Assert.Equal(1, commands.StatusCount);
    }

    [Fact]
    public async Task Worker_WhenDeliveryBatchIsFull_ShouldContinueWithoutAnotherPollDelay()
    {
        var time = new OutboxTestTimeProvider();
        await using var app = await CreateAsync(time, new WorkerCommands());
        var poll = await time.TakeDelayAsync(PollInterval, Token);
        await PublishAsync(app, 5);

        poll.Fire();
        _ = await time.TakeDelayAsync(PollInterval, Token);

        await AssertDeliveredAsync(app, 5);
    }

    [Fact]
    public async Task Worker_WhenCleanupIsBlocked_ShouldKeepDeliveringAndCancelMaintenanceOnShutdown()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new WorkerCommands { BlockCleanup = true };
        await using var app = await CreateAsync(time, commands);
        var poll = await time.TakeDelayAsync(PollInterval, Token);
        var cleanup = await time.TakeDelayAsync(CleanupInterval, Token);
        await PublishAsync(app, 1);

        cleanup.Fire();
        await commands.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
        poll.Fire();
        _ = await time.TakeDelayAsync(PollInterval, Token);

        await AssertDeliveredAsync(app, 1);
        Assert.False(commands.CleanupExited.Task.IsCompleted);
        await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.True(commands.CleanupExited.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Worker_WhenCleanupFails_ShouldDeliverAndRetryMaintenanceAtItsOwnInterval()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new WorkerCommands { FailFirstCleanup = true };
        await using var app = await CreateAsync(time, commands);
        var poll = await time.TakeDelayAsync(PollInterval, Token);
        var cleanup = await time.TakeDelayAsync(CleanupInterval, Token);
        cleanup.Fire();
        cleanup = await time.TakeDelayAsync(CleanupInterval, Token);

        await PublishAsync(app, 1);
        poll.Fire();
        _ = await time.TakeDelayAsync(PollInterval, Token);
        await AssertDeliveredAsync(app, 1);
        Assert.Equal(1, commands.CleanupCount);

        cleanup.Fire();
        _ = await time.TakeDelayAsync(CleanupInterval, Token);
        Assert.Equal(2, commands.CleanupCount);
        Assert.Equal(0, commands.StatusCount);
    }

    private static Task<MonicaTestApplication> CreateAsync(OutboxTestTimeProvider time, WorkerCommands commands)
        => new RepositoryScenarioFactory(interceptor: commands,
            configure: services => services.AddSingleton<TimeProvider>(time),
            configureOutbox: options =>
            {
                options.EnableWorker = true;
                options.BatchSize = 2;
                options.PollInterval = PollInterval;
                options.CleanupInterval = CleanupInterval;
                options.StatusInterval = StatusInterval;
            }).CreateAsync(cancellationToken: Token);

    private static Task PublishAsync(MonicaTestApplication app, int count)
        => app.ExecuteAsync(async scope =>
        {
            for (var i = 0; i < count; i++)
                await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice($"notice-{i}"), cancellationToken: Token);
        }, cancellationToken: Token);

    private static Task AssertDeliveredAsync(MonicaTestApplication app, int count)
        => app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.Set<OutboxMessage>().ToListAsync(ct);
            Assert.Equal(count, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.NotNull(row.DeliveredAtUtc);
                Assert.Equal(1, row.Attempts);
            });
        }, Token);

    private sealed class WorkerCommands : DbCommandInterceptor
    {
        private int _cleanupCount;
        private int _statusCount;
        public int CleanupCount => Volatile.Read(ref _cleanupCount);
        public int StatusCount => Volatile.Read(ref _statusCount);
        public bool BlockCleanup { get; init; }
        public bool FailFirstCleanup { get; init; }
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal))
                Interlocked.Increment(ref _statusCount);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                || !command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal)) return result;
            var count = Interlocked.Increment(ref _cleanupCount);
            if (FailFirstCleanup && count == 1) throw new IOException("Maintenance database unavailable.");
            if (BlockCleanup)
            {
                CleanupEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    CleanupExited.TrySetResult();
                }
            }
            return result;
        }
    }
}
