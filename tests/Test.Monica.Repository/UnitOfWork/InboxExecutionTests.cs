using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class InboxExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManualTestDelivery_PreservesInboxDeduplicationAndSelectedConsumerBoundary()
    {
        await using var app = await new RepositoryScenarioFactory(inbox: true, manual: true).CreateAsync(cancellationToken: Token);
        var message = await PrepareAsync(app);
        var registry = app.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var selected = registry.GetAll().Single(subscription => subscription.HandlerType == typeof(FirstInboxHandler));
        Assert.Equal(EventSubscriptionState.Held, selected.State);
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        await receiver.DispatchTestAsync(message, [selected.Id], Token);
        await receiver.DispatchTestAsync(message, [selected.Id], Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(1, await db.HardDeleteRows.CountAsync(ct));
            var receipt = Assert.Single(await db.Set<InboxReceipt>().ToListAsync(ct));
            Assert.Equal("tests.inbox.a", receipt.Consumer);
            Assert.Equal(message.Metadata.MessageId, receipt.MessageId);
        }, Token);
        Assert.Equal(EventSubscriptionState.Held, selected.State);
    }

    [Fact]
    public async Task Receive_WhenTwoConsumersShareDatabase_ShouldRunEachOnce()
    {
        await using var app = await new RepositoryScenarioFactory(inbox: true).CreateAsync(cancellationToken: Token);
        var message = await PrepareAsync(app);
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        await receiver.DispatchAsync(message, Token);
        await receiver.DispatchAsync(message, Token);

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(2, await db.HardDeleteRows.CountAsync(ct));
            var receipts = await db.Set<InboxReceipt>().OrderBy(x => x.Consumer).ToListAsync(ct);
            Assert.Equal(["tests.inbox.a", "tests.inbox.b"], receipts.Select(x => x.Consumer));
            Assert.All(receipts, receipt => Assert.Equal(message.Metadata.MessageId, receipt.MessageId));
        }, Token);
    }

    [Fact]
    public async Task Receive_WhenIdentityIsMissing_ShouldRejectInboxHandler()
    {
        await using var app = await new RepositoryScenarioFactory(inbox: true).CreateAsync(cancellationToken: Token);
        var message = await PrepareAsync(app);
        var withoutIdentity = message with { Metadata = message.Metadata with { MessageId = null } };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(withoutIdentity, Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Empty(await db.Set<InboxReceipt>().ToListAsync(ct)), Token);
    }

    [Fact]
    public async Task Receive_WhenBusinessRejectedAfterFlush_ShouldRollBackBeforePropagatingToTransport()
    {
        await using var app = await new RepositoryScenarioFactory(
            inbox: true,
            configure: services => services.AddScoped<RejectingInboxHandler>()).CreateAsync(cancellationToken: Token);
        EventMessage message;
        await using (var scope = app.CreateScope(Token))
        {
            message = scope.Resolve<IEventMessageFactory>().Prepare(
                typeof(RejectedInboxNotice), new RejectedInboxNotice("rejected"), EventSubscriptionScope.Distributed);
        }

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            app.Services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, Token));

        Assert.Equal("The notice violates a business rule.", exception.Message);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<InboxReceipt>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task Receive_WhenDuplicateDeliveriesOverlap_ShouldCommitOneReceiptPerConsumer()
    {
        await using var app = await new RepositoryScenarioFactory(inbox: true).CreateAsync(cancellationToken: Token);
        var message = await PrepareAsync(app);
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await receiver.DispatchAsync(message, Token);
                return true;
            }
            catch (DbUpdateException)
            {
                // The losing concurrent transaction is retried from a fresh delivery scope.
                return false;
            }
            catch (SqliteException)
            {
                // SQLite may report writer contention before the unique-key insert completes.
                return false;
            }
        }));
        Assert.Contains(true, attempts);
        await receiver.DispatchAsync(message, Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(2, await db.Set<InboxReceipt>().CountAsync(ct));
            Assert.Equal(2, await db.HardDeleteRows.CountAsync(ct));
        }, Token);
    }

    private static async Task<EventMessage> PrepareAsync(MonicaTestApplication app)
    {
        await using var scope = app.CreateScope(Token);
        return scope.Resolve<IEventMessageFactory>().Prepare(typeof(InboxNotice), new InboxNotice("received"),
            EventSubscriptionScope.Distributed);
    }
}

[EventName("tests.inbox.v1")]
public sealed record InboxNotice(string Value);

[Inbox("tests.inbox.a")]
public sealed class FirstInboxHandler(TestRepositoryDbContext db) : IDistributedEventHandler<InboxNotice>
{
    public Task HandleEventAsync(InboxNotice eventData, CancellationToken cancellationToken)
    {
        db.HardDeleteRows.Add(new HardDeleteRow { Title = "a:" + eventData.Value });
        return Task.CompletedTask;
    }
}

[Inbox("tests.inbox.b")]
public sealed class SecondInboxHandler(TestRepositoryDbContext db) : IDistributedEventHandler<InboxNotice>
{
    public Task HandleEventAsync(InboxNotice eventData, CancellationToken cancellationToken)
    {
        db.HardDeleteRows.Add(new HardDeleteRow { Title = "b:" + eventData.Value });
        return Task.CompletedTask;
    }
}

[EventName("tests.inbox.rejected.v1")]
public sealed record RejectedInboxNotice(string Value);

[Inbox("tests.inbox.rejected")]
public sealed class RejectingInboxHandler(TestRepositoryDbContext db) : IDistributedEventHandler<RejectedInboxNotice>
{
    public async Task HandleEventAsync(RejectedInboxNotice eventData, CancellationToken cancellationToken)
    {
        db.HardDeleteRows.Add(new HardDeleteRow { Title = eventData.Value });
        await db.SaveChangesAsync(cancellationToken);
        throw new BusinessException("The notice violates a business rule.");
    }
}
