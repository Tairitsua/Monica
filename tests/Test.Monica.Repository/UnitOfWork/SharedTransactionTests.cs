using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.Modularity.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Snowflake.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Persistence;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class SharedTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenContextsShareConnection_ShouldUseOneAtomicTransaction(bool fail)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection, connection);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (var operation = host.Services.CreateAsyncScope())
        {
            var manager = operation.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var execute = () => manager.RunAsync(async () =>
            {
                var first = operation.ServiceProvider.GetRequiredService<TestRepositoryDbContext>();
                var second = operation.ServiceProvider.GetRequiredService<SecondDbContext>();
                first.Add(new HardDeleteRow { Title = "first" });
                second.Add(new HardDeleteRow { Title = "second" });
                await manager.Current!.FlushAsync(TestContext.Current.CancellationToken);
                if (fail) throw new ApplicationException();
            }, new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext), typeof(SecondDbContext)]),
                TestContext.Current.CancellationToken);
            if (fail) await Assert.ThrowsAsync<ApplicationException>(execute);
            else await execute();
        }
        await using var verify = host.Services.CreateAsyncScope();
        Assert.Equal(fail ? 0 : 2, await verify.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
            .HardDeleteRows.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhenConnectionsAreIndependent_ShouldRejectBeforeHandler()
    {
        await using var first = new SqliteConnection("Data Source=:memory:");
        await using var second = new SqliteConnection("Data Source=:memory:");
        using var host = CreateHost(first, second);
        await using var scope = host.Services.CreateAsyncScope();
        var invoked = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
            .RunAsync(() => { invoked = true; return Task.CompletedTask; },
                new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext), typeof(SecondDbContext)]),
                TestContext.Current.CancellationToken));
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_WhenStoresAreAmbiguous_ShouldRequireSelection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        using var host = CreateHost(connection, connection);
        await using var scope = host.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
            .RunAsync(() => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhenIndependentStoreIsExplicitlySelected_ShouldCommitOnlyThatStore()
    {
        await using var first = new SqliteConnection("Data Source=:memory:");
        await using var second = new SqliteConnection("Data Source=:memory:");
        await first.OpenAsync(TestContext.Current.CancellationToken);
        await second.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(first, second, DbContextProviderType.Default);
        await using (var setup = host.Services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await setup.ServiceProvider.GetRequiredService<SecondDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }
        await using (var operation = host.Services.CreateAsyncScope())
        {
            var db = operation.ServiceProvider.GetRequiredService<SecondDbContext>();
            await operation.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().RunAsync(() =>
            {
                db.Add(new HardDeleteRow { Title = "independent" });
                return Task.CompletedTask;
            }, new UnitOfWorkScopeOptions([typeof(SecondDbContext)]), TestContext.Current.CancellationToken);
        }
        await using var verify = host.Services.CreateAsyncScope();
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<TestRepositoryDbContext>().HardDeleteRows.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ServiceProvider.GetRequiredService<SecondDbContext>().HardDeleteRows.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WhenMethodSelectsContext_ShouldUseSelectionUnlessScopeOptionsOverrideIt(bool overrideSelection)
    {
        var token = TestContext.Current.CancellationToken;
        await using var first = new SqliteConnection("Data Source=:memory:");
        await using var second = new SqliteConnection("Data Source=:memory:");
        await first.OpenAsync(token);
        await second.OpenAsync(token);
        using var host = CreateHost(first, second, DbContextProviderType.Default);
        await using (var setup = host.Services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>().Database.EnsureCreatedAsync(token);
            await setup.ServiceProvider.GetRequiredService<SecondDbContext>().Database.EnsureCreatedAsync(token);
        }
        await using (var operation = host.Services.CreateAsyncScope())
        {
            var handler = new SelectedContextHandler(
                operation.ServiceProvider.GetRequiredService<TestRepositoryDbContext>(),
                operation.ServiceProvider.GetRequiredService<SecondDbContext>());
            var descriptor = ExecutionDescriptor.ForMethod<ExecutionUnit, ExecutionUnit>(
                new ExecutionPoint("test.context-selection"),
                typeof(SelectedContextHandler),
                typeof(SelectedContextHandler).GetMethod(nameof(SelectedContextHandler.ExecuteAsync)),
                isBusinessOperation: true,
                transactionMode: ExecutionTransactionMode.Automatic);
            var features = new ExecutionFeatureCollection();
            if (overrideSelection)
            {
                features.Set(new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext)]));
            }
            await operation.ServiceProvider.GetRequiredService<IExecutionPipeline>().ExecuteAsync(
                descriptor,
                ExecutionUnit.Value,
                handler,
                handler.ExecuteAsync,
                token,
                features);
        }
        await using var verify = host.Services.CreateAsyncScope();
        Assert.Equal(overrideSelection ? 1 : 0, await verify.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
            .HardDeleteRows.CountAsync(token));
        Assert.Equal(overrideSelection ? 0 : 1, await verify.ServiceProvider.GetRequiredService<SecondDbContext>()
            .HardDeleteRows.CountAsync(token));
    }

    [Fact]
    public async Task RunAsync_WhenPrimaryWasFlushedFirst_ShouldFlushLaterParticipantProjectionOnOwner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection, connection, withOutbox: true);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (var operation = host.Services.CreateAsyncScope())
        {
            var services = operation.ServiceProvider;
            var manager = services.GetRequiredService<IUnitOfWorkManager>();
            await manager.RunAsync(() =>
            {
                services.GetRequiredService<TestRepositoryDbContext>().Add(new HardDeleteRow { Title = "primary" });
                services.GetRequiredService<SecondDbContext>().Add(new HardDeleteRow { Title = "secondary" });
                return Task.CompletedTask;
            }, new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext), typeof(SecondDbContext)]),
                TestContext.Current.CancellationToken);
        }
        await using var verify = host.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<TestRepositoryDbContext>();
        Assert.Equal(2, await db.HardDeleteRows.CountAsync(TestContext.Current.CancellationToken));
        var message = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tests.notice.v1", message.EventName);
    }

    [Fact]
    public async Task ReceivedHandler_WhenSelectingSharedContexts_ShouldCommitInboxBusinessAndOutgoingRowsTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection, connection, withOutbox: true, withInbox: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (var subscriptionScope = host.Services.CreateAsyncScope())
            await subscriptionScope.ServiceProvider.GetRequiredService<IDistributedEventBus>()
                .SubscribeAsync<SharedIncomingNotice, SharedInboxHandler>();
        EventMessage message;
        await using (var scope = host.Services.CreateAsyncScope())
            message = scope.ServiceProvider.GetRequiredService<IEventMessageFactory>().Prepare(
                typeof(SharedIncomingNotice), new SharedIncomingNotice("transactional"), EventSubscriptionScope.Distributed);
        var receiver = host.Services.GetRequiredService<IEventReceiveDispatcher>();
        await receiver.DispatchAsync(message, TestContext.Current.CancellationToken);
        await receiver.DispatchAsync(message, TestContext.Current.CancellationToken);
        await using var verify = host.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<TestRepositoryDbContext>();
        Assert.Equal(2, await db.HardDeleteRows.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.Set<InboxReceipt>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.Set<OutboxMessage>().ToListAsync(TestContext.Current.CancellationToken));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WhenNoUnitOfWorkParticipantIsRegistered_ShouldAllowReadsWithoutTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection, connection,
            secondParticipation: DbContextProviderType.Default,
            withOutbox: true,
            firstParticipation: DbContextProviderType.Default);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (var operation = host.Services.CreateAsyncScope())
        {
            var manager = operation.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var db = operation.ServiceProvider.GetRequiredService<TestRepositoryDbContext>();
            await manager.RunAsync(() => db.HardDeleteRows.CountAsync(TestContext.Current.CancellationToken),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTransactionIsOpen_ShouldStillRejectNonParticipantRead()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection, connection,
            secondParticipation: DbContextProviderType.Default,
            withOutbox: true,
            firstParticipation: DbContextProviderType.Default);
        await using (var setup = host.Services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<TestRepositoryDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await setup.ServiceProvider.GetRequiredService<SecondDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }
        await using var operation = host.Services.CreateAsyncScope();
        var manager = operation.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var nonParticipant = operation.ServiceProvider.GetRequiredService<SecondDbContext>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(
            () => nonParticipant.HardDeleteRows.CountAsync(TestContext.Current.CancellationToken),
            new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext)]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateDatabaseAccess_WhenContextIsNotSelected_ShouldRejectAndRollbackEvenWhenCaught()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        using var host = CreateHost(connection, connection);
        await using var operation = host.Services.CreateAsyncScope();
        var manager = operation.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var rollback = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(() =>
        {
            operation.ServiceProvider.GetRequiredService<TestRepositoryDbContext>().ValidateDatabaseAccess();
            var nonParticipant = operation.ServiceProvider.GetRequiredService<SecondDbContext>();
            var original = Assert.Throws<InvalidOperationException>(nonParticipant.ValidateDatabaseAccess);
            Assert.Same(original, manager.Current!.FirstFailure!.Exception);
            return Task.CompletedTask;
        }, new UnitOfWorkScopeOptions([typeof(TestRepositoryDbContext)]), token));
        Assert.Equal("Participation", rollback.FirstFailure.Source);
        Assert.Equal(typeof(SecondDbContext).FullName, rollback.FirstFailure.DbContextType);
    }

    private static IHost CreateHost(SqliteConnection first, SqliteConnection second,
        DbContextProviderType secondParticipation = DbContextProviderType.UnitOfWork,
        bool withOutbox = false,
        bool withInbox = false,
        DbContextProviderType firstParticipation = DbContextProviderType.UnitOfWork)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            if (withInbox) monica.ConfigureTypeDiscovery(options => options.Add(typeof(SharedInboxHandler).Assembly));
            var repository = monica.AddRepository()
                .AddRepositoryDbContext<TestRepositoryDbContext>((_, db) => db.UseSqlite(first), firstParticipation)
                .AddRepositoryDbContext<SecondDbContext>((_, db) => db.UseSqlite(second), secondParticipation);
            if (withOutbox)
            {
                monica.AddEventBus().UseNoOpDistributedEventBus();
                repository.AddOutbox<TestRepositoryDbContext>(options => options.EnableWorker = false);
                if (!withInbox)
                    repository.AddEntityEventProjections<SecondDbContext>(options =>
                        options.RegisterEntity<HardDeleteRow, IntegrationNotice>(row => new IntegrationNotice(row.Title)));
            }
            if (withInbox) repository.AddInbox<TestRepositoryDbContext>();
        });
        builder.Services.AddMonicaTestSeams();
        builder.Services.AddSingleton<ISnowflakeIdGenerator, SequentialTestIdGenerator>();
        if (withInbox) builder.Services.AddScoped<SharedInboxHandler>();
        return builder.Build();
    }

    public sealed class SecondDbContext(DbContextOptions<SecondDbContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<SecondDbContext>(options, services)
    {
        public DbSet<HardDeleteRow> HardDeleteRows => Set<HardDeleteRow>();
    }

    private sealed class SelectedContextHandler(TestRepositoryDbContext first, SecondDbContext second)
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = new[] { typeof(SecondDbContext) })]
        public Task ExecuteAsync()
        {
            first.Add(new HardDeleteRow { Title = "first" });
            second.Add(new HardDeleteRow { Title = "second" });
            return Task.CompletedTask;
        }
    }
}

[EventName("tests.shared-incoming.v1")]
public sealed record SharedIncomingNotice(string Value);

[Inbox("tests.shared-handler.v1")]
public sealed class SharedInboxHandler(
    TestRepositoryDbContext primary,
    SharedTransactionTests.SecondDbContext secondary,
    IDistributedEventBus bus) : IDistributedEventHandler<SharedIncomingNotice>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic,
        DbContextTypes = new[] { typeof(TestRepositoryDbContext), typeof(SharedTransactionTests.SecondDbContext) })]
    public async Task HandleEventAsync(SharedIncomingNotice eventData, CancellationToken cancellationToken)
    {
        primary.Add(new HardDeleteRow { Title = "primary:" + eventData.Value });
        secondary.Add(new HardDeleteRow { Title = "secondary:" + eventData.Value });
        await bus.PublishAsync(new IntegrationNotice(eventData.Value), cancellationToken: cancellationToken);
    }
}
