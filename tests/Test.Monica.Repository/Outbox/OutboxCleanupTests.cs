using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Outbox.Services;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.Outbox;

public sealed class OutboxCleanupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CleanupAsync_WhenBacklogged_ShouldDeleteExpiredRowsInServerSideBatchesOnly()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new CleanupCommands();
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 7);
        commands.Recording = true;

        Assert.Equal(7, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().CleanupAsync(Token));

        Assert.Equal(new[] { 3, 3, 1 }, commands.Deleted);
        Assert.Equal(3, commands.Commands.Count);
        Assert.All(commands.Commands, sql => Assert.StartsWith("DELETE", sql));
        commands.Recording = false;
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var remaining = await db.Set<OutboxMessage>().Select(x => x.MessageId).OrderBy(x => x).ToListAsync(ct);
            Assert.Equal(new[] { "at-cutoff", "pending", "recent" }, remaining);
        }, Token);
    }

    [Fact]
    public async Task CleanupAsync_WhenBudgetExpires_ShouldCommitBoundedBatchesAndResumeNextPass()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new CleanupCommands { AfterDelete = () => time.Advance(TimeSpan.FromSeconds(1)) };
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 10);
        commands.Recording = true;
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();

        Assert.Equal(6, await dispatcher.CleanupAsync(Token));
        Assert.Equal(new[] { 3, 3 }, commands.Deleted);
        commands.Recording = false;
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(4, await db.Set<OutboxMessage>().CountAsync(x => x.MessageId.StartsWith("expired-"), ct)), Token);

        // The cutoff advances only for the next pass, so the former boundary row now expires too.
        Assert.Equal(5, await dispatcher.CleanupAsync(Token));
    }

    [Fact]
    public async Task CleanupAsync_WhenCancelledBetweenBatches_ShouldPreserveCommittedProgress()
    {
        var time = new OutboxTestTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var commands = new CleanupCommands { AfterCommit = cancellation.Cancel };
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 7);
        commands.Recording = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().CleanupAsync(cancellation.Token));

        Assert.Equal(new[] { 3 }, commands.Deleted);
        commands.Recording = false;
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(4, await db.Set<OutboxMessage>().CountAsync(x => x.MessageId.StartsWith("expired-"), ct)), Token);
    }

    [Fact]
    public async Task CleanupAsync_WhenCancelledBeforeBatchCommit_ShouldRollBackThatBatch()
    {
        var time = new OutboxTestTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var commands = new CleanupCommands { AfterDelete = cancellation.Cancel };
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 7);
        commands.Recording = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().CleanupAsync(cancellation.Token));

        Assert.Equal(new[] { 3 }, commands.Deleted);
        commands.Recording = false;
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(7, await db.Set<OutboxMessage>().CountAsync(x => x.MessageId.StartsWith("expired-"), ct)), Token);
    }

    [Fact]
    public async Task CleanupAsync_WhenLaterBatchFails_ShouldRetainEarlierCommitsAndAllowFreshScopeRetry()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new CleanupCommands { FailOnDelete = 2 };
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 7);
        commands.Recording = true;
        var dispatcher = app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>();

        await Assert.ThrowsAsync<IOException>(() => dispatcher.CleanupAsync(Token));

        Assert.Equal(new[] { 3 }, commands.Deleted);
        commands.Recording = false;
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(4, await db.Set<OutboxMessage>().CountAsync(x => x.MessageId.StartsWith("expired-"), ct)), Token);
        Assert.Equal(4, await dispatcher.CleanupAsync(Token));
    }

    [Fact]
    public async Task CleanupAsync_WhenNothingExpires_ShouldExecuteOneStatementWithoutLoadingMessages()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new CleanupCommands();
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 0);
        commands.Recording = true;

        Assert.Equal(0, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().CleanupAsync(Token));

        Assert.StartsWith("DELETE", Assert.Single(commands.Commands));
        Assert.Equal(0, Assert.Single(commands.Deleted));
    }

    [Fact]
    public async Task CleanupAsync_WhenSelectingExpiredRows_ShouldUseExistingCoveringIndexWithoutSorting()
    {
        var time = new OutboxTestTimeProvider();
        var commands = new CleanupCommands();
        await using var app = await CreateAsync(time, commands);
        await SeedAsync(app, time, expiredCount: 2);
        commands.Recording = true;
        Assert.Equal(2, await app.Services.GetRequiredService<OutboxDispatcher<TestRepositoryDbContext>>().CleanupAsync(Token));
        commands.Recording = false;

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            await db.Database.OpenConnectionAsync(ct);
            await using var plan = db.Database.GetDbConnection().CreateCommand();
            plan.CommandText = "EXPLAIN QUERY PLAN " + Assert.Single(commands.Commands);
            foreach (var (name, value) in commands.Parameters)
            {
                var parameter = plan.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                plan.Parameters.Add(parameter);
            }
            await using var reader = await plan.ExecuteReaderAsync(ct);
            var steps = new List<string>();
            while (await reader.ReadAsync(ct)) steps.Add(reader.GetString(3));
            Assert.Contains(steps, step => step.Contains("COVERING INDEX", StringComparison.Ordinal)
                && step.Contains("DeliveredAtUtc", StringComparison.Ordinal));
            Assert.DoesNotContain(steps, step => step.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
        }, Token);
    }

    [Theory]
    [InlineData("CleanupBatchSize", 0)]
    [InlineData("CleanupBatchSize", -1)]
    [InlineData("CleanupInterval", 0)]
    [InlineData("CleanupInterval", -1)]
    [InlineData("CleanupTimeBudget", 0)]
    [InlineData("CleanupTimeBudget", -1)]
    [InlineData("StatusInterval", 0)]
    [InlineData("StatusInterval", -1)]
    public async Task AddOutbox_WhenMaintenanceLimitsAreInvalid_ShouldRejectConfiguration(string option, int value)
    {
        var factory = new RepositoryScenarioFactory(configureOutbox: options =>
        {
            switch (option)
            {
                case "CleanupBatchSize": options.CleanupBatchSize = value; break;
                case "CleanupInterval": options.CleanupInterval = TimeSpan.FromSeconds(value); break;
                case "CleanupTimeBudget": options.CleanupTimeBudget = TimeSpan.FromSeconds(value); break;
                case "StatusInterval": options.StatusInterval = TimeSpan.FromSeconds(value); break;
            }
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => factory.CreateAsync(cancellationToken: Token));
    }

    private static Task<MonicaTestApplication> CreateAsync(OutboxTestTimeProvider time, CleanupCommands commands)
        => new RepositoryScenarioFactory(interceptor: commands,
            configure: services => services.AddSingleton<TimeProvider>(time),
            configureOutbox: options =>
            {
                options.BatchSize = 1;
                options.CleanupBatchSize = 3;
                options.CleanupTimeBudget = TimeSpan.FromSeconds(2);
            }).CreateAsync(cancellationToken: Token);

    private static async Task SeedAsync(MonicaTestApplication app, TimeProvider time, int expiredCount)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-7);
        await app.SeedAsync<TestRepositoryDbContext, int>(async (db, ct) =>
        {
            var rows = new List<OutboxMessage>
            {
                Row("pending", null), Row("recent", cutoff.AddDays(1)), Row("at-cutoff", cutoff)
            };
            rows.AddRange(Enumerable.Range(0, expiredCount).Select(i => Row($"expired-{i}", cutoff.AddMinutes(-i - 1))));
            db.AddRange(rows);
            return await db.SaveChangesAsync(ct);
        }, Token);

        OutboxMessage Row(string id, DateTime? delivered) => new()
        {
            MessageId = id, Source = "test", EventName = "test", TopicName = "test",
            CreatedAtUtc = cutoff.AddDays(-10), DeliveredAtUtc = delivered, Body = new byte[128]
        };
    }

    private sealed class CleanupCommands : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public bool Recording { get; set; }
        public int FailOnDelete { get; init; }
        public Action? AfterDelete { get; init; }
        public Action? AfterCommit { get; init; }
        private readonly HashSet<DbTransaction> _cleanupTransactions = [];
        public List<string> Commands { get; } = [];
        public List<int> Deleted { get; } = [];
        public List<(string Name, object? Value)> Parameters { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Recording && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Recording && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal))
            {
                Commands.Add(command.CommandText);
                Parameters.Clear();
                Parameters.AddRange(command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)));
                Assert.NotNull(command.Transaction);
                _cleanupTransactions.Add(command.Transaction);
                if (Commands.Count == FailOnDelete) throw new IOException("Cleanup database command failed.");
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Recording && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal))
            {
                Deleted.Add(result);
                AfterDelete?.Invoke();
            }
            return ValueTask.FromResult(result);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_cleanupTransactions.Remove(transaction)) AfterCommit?.Invoke();
            return Task.CompletedTask;
        }
    }
}
