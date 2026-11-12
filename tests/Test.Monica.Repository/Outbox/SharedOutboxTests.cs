using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Modules;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Repository.Outbox;

public sealed class SharedOutboxTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SharedLocal_WhenReceiverHasOnlySomeHandlers_ShouldNotClaimUntilTheCompleteSetIsAvailable()
    {
        await using var database = await OpenDatabaseAsync();
        await using var producer = await new SharedFactory(database.ConnectionString).CreateAsync(cancellationToken: Token);
        await using var receiver = await new SharedFactory(database.ConnectionString).CreateAsync(cancellationToken: Token);
        await SubscribeAsync<FirstHandler>(producer);
        await SubscribeAsync<SecondHandler>(producer);
        await SubscribeAsync<FirstHandler>(receiver);
        await PublishLocalAsync(producer);

        Assert.Equal(0, await receiver.DrainOutboxAsync<FirstContext>(cancellationToken: Token));
        await AssertPendingWithoutClaimAsync(receiver);
        var second = await SubscribeAsync<SecondHandler>(receiver);
        await second.DeactivateAsync();
        Assert.Equal(0, await receiver.DrainOutboxAsync<FirstContext>(cancellationToken: Token));
        await AssertPendingWithoutClaimAsync(receiver);
        await second.ReactivateAsync();

        Assert.Equal(1, await receiver.DrainOutboxAsync<FirstContext>(cancellationToken: Token));
        var calls = receiver.Services.GetRequiredService<Calls>();
        Assert.Equal(1, calls.First);
        Assert.Equal(1, calls.Second);
        Assert.Equal(0, producer.Services.GetRequiredService<Calls>().First);
    }

    [Fact]
    public async Task SharedDistributed_WhenTransportIsMissing_ShouldSkipWithoutRetryAndAnotherHostCanDeliver()
    {
        await using var database = await OpenDatabaseAsync();
        await using var capable = await new SharedFactory(database.ConnectionString, keyedTransport: true).CreateAsync(cancellationToken: Token);
        await using var incapable = await new SharedFactory(database.ConnectionString).CreateAsync(cancellationToken: Token);
        await capable.ExecuteAsync(async scope =>
        {
            var services = scope.Resolve<IServiceProvider>();
            var message = services.GetRequiredService<IEventMessageFactory>().Prepare(typeof(SharedNotice),
                new SharedNotice("keyed"), EventSubscriptionScope.Distributed, serviceKey: "special");
            await services.GetRequiredService<ITransactionalEventSink>().StageAsync(() => [message], Token);
        }, cancellationToken: Token);
        Assert.Equal(0, await incapable.DrainOutboxAsync<FirstContext>(cancellationToken: Token));
        await AssertPendingWithoutClaimAsync(incapable);
        Assert.Equal(1, await capable.DrainOutboxAsync<SecondContext>(cancellationToken: Token));
        Assert.Equal(1, capable.Services.GetRequiredService<Calls>().Transport);
    }

    [Fact]
    public async Task SharedLocal_WhenSubscriptionsAreDelegates_ShouldRejectBeforeCommit()
    {
        await using var database = await OpenDatabaseAsync();
        await using var app = await new SharedFactory(database.ConnectionString).CreateAsync(cancellationToken: Token);
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILocalEventBus>()
            .SubscribeAsync<SharedNotice>((_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PublishLocalAsync(app));
        Assert.Contains("typed handlers", error.Message);
        await app.VerifyAsync<FirstContext>(async (db, ct) => Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct)), Token);
    }

    [Fact]
    public async Task SharedStore_WhenLegacyRowsExist_ShouldLeaveThemUnclaimed()
    {
        await using var database = await OpenDatabaseAsync();
        await using var app = await new SharedFactory(database.ConnectionString).CreateAsync(cancellationToken: Token);
        await app.SeedAsync<FirstContext, int>(async (db, ct) =>
        {
            db.Add(new OutboxMessage { MessageId = "legacy", Source = "tests", EventName = "test",
                TopicName = "test", Scope = EventSubscriptionScope.Distributed, CreatedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(ct);
        }, Token);
        Assert.Equal(0, await app.DrainOutboxAsync<FirstContext>(cancellationToken: Token));
        await AssertPendingWithoutClaimAsync(app);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedWorker_WhenStoreOrPolicyDiffers_ShouldRejectStartup(bool differentDatabase)
    {
        await using var database = await OpenDatabaseAsync();
        await using var other = await OpenDatabaseAsync();
        var factory = new SharedFactory(database.ConnectionString, worker: true,
            secondConnection: differentDatabase ? other.ConnectionString : null, differentPolicy: !differentDatabase);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: Token));
        Assert.Contains(differentDatabase ? "different physical stores" : "conflicting delivery or maintenance", error.ToString());
    }

    [Fact]
    public async Task SharedWorker_WhenContextsShareOneStore_ShouldRunOneScanPerPoll()
    {
        await using var database = await OpenDatabaseAsync();
        var clock = new OutboxTestTimeProvider();
        var commands = new ScanCommands();
        await using var app = await new SharedFactory(database.ConnectionString, worker: true, clock: clock,
            commands: commands).CreateAsync(cancellationToken: Token);
        var poll = await clock.TakeDelayAsync(TimeSpan.FromSeconds(5), Token);
        _ = await clock.TakeDelayAsync(TimeSpan.FromMinutes(1), Token);
        _ = await clock.TakeDelayAsync(TimeSpan.FromMinutes(2), Token);
        Assert.Equal(1, commands.Scans);
        poll.Fire();
        _ = await clock.TakeDelayAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, commands.Scans);
    }

    private static Task PublishLocalAsync(MonicaTestApplication app) => app.ExecuteAsync(async scope =>
        await scope.Resolve<ILocalEventBus>().PublishAsync(new SharedNotice("shared"), cancellationToken: Token), cancellationToken: Token);

    private static async Task<IEventSubscription> SubscribeAsync<THandler>(MonicaTestApplication app) where THandler : IEventHandler
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILocalEventBus>().SubscribeAsync<SharedNotice, THandler>();
    }

    private static Task AssertPendingWithoutClaimAsync(MonicaTestApplication app) => app.VerifyAsync<FirstContext>(async (db, ct) =>
    {
        var row = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LeaseId);
        Assert.Null(row.DeliveredAtUtc);
        Assert.Null(row.LastError);
    }, Token);

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection($"Data Source=shared-outbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync(Token);
        return connection;
    }

    private sealed class SharedFactory(string connection, bool worker = false, bool keyedTransport = false,
        string? secondConnection = null, bool differentPolicy = false, OutboxTestTimeProvider? clock = null,
        ScanCommands? commands = null) : MonicaTestApplicationFactory<FirstContext>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddHostedService(options => options.FailFastOnStartupError = true);
            builder.AddEventBus(options => options.DisableAutoDiscovery = true).UseNoOpDistributedEventBus();
            var repository = builder.AddRepository()
                .AddRepositoryDbContext<FirstContext>((_, db) => db.UseSqlite(connection))
                .AddRepositoryDbContext<SecondContext>((_, db) => db.UseSqlite(secondConnection ?? connection), DbContextProviderType.Default);
            repository.AddOutbox<FirstContext>(ConfigureOutbox);
            repository.AddOutbox<SecondContext>(options =>
            {
                ConfigureOutbox(options);
                options.ExcludeFromMigrations = true;
                options.LeaseTable.ExcludeFromMigrations = true;
                if (differentPolicy) options.DeliveredRetention = TimeSpan.FromDays(2);
            });
        }

        private void ConfigureOutbox(RepositoryOutboxOptions options)
        {
            options.SharedStoreKey = "test-shared-store";
            options.EnableWorker = worker;
            options.StatusInterval = TimeSpan.FromMinutes(2);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.UseTestDatabase<FirstContext>((_, options) =>
            {
                options.UseSqlite(connection);
                if (commands is not null) options.AddInterceptors(commands);
            });
            services.UseTestDatabase<SecondContext>((_, options) =>
            {
                options.UseSqlite(secondConnection ?? connection);
                if (commands is not null) options.AddInterceptors(commands);
            });
            services.AddSingleton<Calls>();
            services.AddScoped<FirstHandler>();
            services.AddScoped<SecondHandler>();
            if (clock is not null) services.AddSingleton<TimeProvider>(clock);
            if (keyedTransport) services.AddKeyedScoped<IEventTransport>("special",
                (sp, _) => new CountingTransport(sp.GetRequiredService<Calls>()));
        }
    }

    public sealed class FirstContext(DbContextOptions<FirstContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<FirstContext>(options, services);
    public sealed class SecondContext(DbContextOptions<SecondContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<SecondContext>(options, services);

    [Outbox, EventName("tests.shared-notice.v1")]
    public sealed record SharedNotice(string Value);
    public sealed class Calls { public int First; public int Second; public int Transport; }
    public sealed class FirstHandler(Calls calls) : ILocalEventHandler<SharedNotice>
    {
        public Task HandleEventAsync(SharedNotice eventData, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls.First); return Task.CompletedTask; }
    }
    public sealed class SecondHandler(Calls calls) : ILocalEventHandler<SharedNotice>
    {
        public Task HandleEventAsync(SharedNotice eventData, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls.Second); return Task.CompletedTask; }
    }
    private sealed class CountingTransport(Calls calls) : IEventTransport
    {
        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls.Transport); return Task.CompletedTask; }
    }
    private sealed class ScanCommands : DbCommandInterceptor
    {
        private int _scans;
        public int Scans => Volatile.Read(ref _scans);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("MonicaOutbox", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT", StringComparison.Ordinal)) Interlocked.Increment(ref _scans);
            return ValueTask.FromResult(result);
        }
    }
}
