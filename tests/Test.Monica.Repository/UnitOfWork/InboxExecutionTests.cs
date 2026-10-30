using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
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
    public async Task Receive_WhenRetriesExhaustAfterFlush_ShouldRollBackEachAttemptInFreshScope()
    {
        await using var app = await new RepositoryScenarioFactory(
            inbox: true,
            configure: services =>
            {
                services.AddSingleton<InboxRetryProbe>();
                services.AddScoped<RejectingInboxHandler>();
            }).CreateAsync(cancellationToken: Token);
        EventMessage message;
        await using (var scope = app.CreateScope(Token))
        {
            message = scope.Resolve<IEventMessageFactory>().Prepare(
                typeof(RejectedInboxNotice), new RejectedInboxNotice("rejected"), EventSubscriptionScope.Distributed);
        }

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            app.Services.GetRequiredService<EventMessageRetryPolicy>().ExecuteAsync(
                () => app.Services.GetRequiredService<IEventReceiveDispatcher>().DispatchAsync(message, Token), Token));

        Assert.Equal("The notice violates a business rule.", exception.Message);
        var contexts = app.Services.GetRequiredService<InboxRetryProbe>().ContextIds;
        Assert.Equal(2, contexts.Count);
        Assert.Equal(2, contexts.Distinct().Count());
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
public sealed class RejectingInboxHandler(TestRepositoryDbContext db, InboxRetryProbe probe)
    : IDistributedEventHandler<RejectedInboxNotice>
{
    public async Task HandleEventAsync(RejectedInboxNotice eventData, CancellationToken cancellationToken)
    {
        probe.ContextIds.Add(db.ContextId.InstanceId);
        db.HardDeleteRows.Add(new HardDeleteRow { Title = eventData.Value });
        await db.SaveChangesAsync(cancellationToken);
        throw new BusinessException("The notice violates a business rule.");
    }
}

public sealed class InboxRetryProbe
{
    public List<Guid> ContextIds { get; } = [];
}
