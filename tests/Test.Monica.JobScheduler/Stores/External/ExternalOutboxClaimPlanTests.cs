using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

public sealed partial class ExternalOutboxLeaseTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task SharedClaim_WhenDeliveredHistoryPrecedesTheBacklog_ShouldSeekThePendingRange(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var plan = new ClaimPlan();
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), false,
                transport: new DeliveryTransport(), configureOutbox: options =>
                {
                    options.SharedStoreKey = "shared";
                    options.DeliveryConcurrency = 32;
                }, configureDbContext: options => options.AddInterceptors(plan));
            await CreateOutboxSchemaAsync(host, token);
            await using var scope = host.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IUnitOfWorkManager>().RunAsync(async () =>
            {
                await services.GetRequiredService<IDistributedEventBus>()
                    .PublishAsync(new SharedDeliveryNotice(1), cancellationToken: token);
            }, new UnitOfWorkScopeOptions([typeof(ExternalOutboxContext)]), token);

            await using var db = await Factory(host).CreateDbContextAsync(token);
            var seed = await db.Set<OutboxMessage>().SingleAsync(token);
            seed.DeliveredAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MonicaOutbox" ("MessageId", "Source", "EventName", "TopicName", "Scope", "Body",
                    "CreatedAtUtc", "DeliveredAtUtc", "Attempts", "DeliveryTarget")
                SELECT 'row-' || n, 'test', 'notice', 'topic', {(int)EventSubscriptionScope.Distributed},
                    decode(repeat('61', 512), 'hex'), current_timestamp,
                    CASE WHEN n <= 20000 THEN current_timestamp END, 0, {seed.DeliveryTarget}
                FROM generate_series(1, 20800) AS n
                """, token);
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"MonicaOutbox\"", token);

            Assert.Equal(32, await Dispatcher(host).DrainAsync(32, token));
            Assert.Contains(plan.Steps, step => step.Contains("IX_MonicaOutbox_DeliveredAtUtc_Sequence", StringComparison.Ordinal));
        });
    }

    // Explain the exact parameterized UPDATE issued by the dispatcher. Plain EXPLAIN never takes the claim.
    private sealed class ClaimPlan : DbCommandInterceptor
    {
        public List<string> Steps { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (!sql.Contains("FOR UPDATE SKIP LOCKED", StringComparison.Ordinal)) return result;
            try
            {
                command.CommandText = "EXPLAIN " + sql;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) Steps.Add(reader.GetString(0));
            }
            finally { command.CommandText = sql; }
            return result;
        }
    }
}
