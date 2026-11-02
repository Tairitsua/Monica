using System.Data.Common;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.Repository.Outbox.Models;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class BatchInboxExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DispatchBatchAsync_WhenReplayedAndRepeatedWithinGroup_ShouldFilterInOneQueryAndKeepMetadataAligned()
    {
        var probe = new BatchInboxProbe();
        var queries = new ReceiptQueryCounter();
        await using var app = await Factory(probe, queries).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b", "c");
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        var id = Subscription(app).Id;
        await receiver.DispatchBatchAsync([messages[0]], id, Token);
        queries.Count = 0;
        await receiver.DispatchBatchAsync([messages[0], messages[1], messages[1], messages[2]], id, Token);
        Assert.Equal(1, queries.Count);
        Assert.Equal(["b", "c"], probe.Payloads.Last());
        Assert.Equal([messages[1].Metadata.MessageId!, messages[2].Metadata.MessageId!], probe.MessageIds.Last());
        await receiver.DispatchBatchAsync(messages, id, Token);
        Assert.Equal(2, probe.Payloads.Count);
        await VerifyAsync(app, 3);
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenReceiptsOverlapAcrossAttempts_ShouldConvergeAfterFreshScopeReplay()
    {
        await using var app = await Factory(new()).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b", "c");
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        var id = Subscription(app).Id;
        EventMessage[][] groups = [[messages[0], messages[1]], [messages[1], messages[2]]];
        await Task.WhenAll(groups.Select(async group =>
        {
            try { await receiver.DispatchBatchAsync(group, id, Token); }
            catch (DbUpdateException) { /* Replay the losing unique-key transaction below. */ }
            catch (SqliteException) { /* SQLite can report writer contention before receipt insertion. */ }
        }));
        foreach (var group in groups) await receiver.DispatchBatchAsync(group, id, Token);
        await VerifyAsync(app, 3);
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenProducerSourcesDiffer_ShouldPreserveIndependentIdentities()
    {
        await using var app = await Factory(new()).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b");
        messages[1] = messages[1] with { Metadata = messages[1].Metadata with
            { MessageId = messages[0].Metadata.MessageId, Source = "another-producer" } };
        await app.Services.GetRequiredService<IEventReceiveDispatcher>().DispatchBatchAsync(messages, Subscription(app).Id, Token);
        await VerifyAsync(app, 2);
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenHandlerFailsAfterFlush_ShouldRollBackRowsReceiptsAndOutboxAndRetryInFreshScope()
    {
        var probe = new BatchInboxProbe { FailAfterFlush = true };
        await using var app = await Factory(probe).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b");
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<EventMessageRetryPolicy>().ExecuteAsync(
                () => receiver.DispatchBatchAsync(messages, Subscription(app).Id, Token), Token));
        Assert.Equal(2, probe.ContextIds.Distinct().Count());
        await VerifyAsync(app, 0);
        probe.FailAfterFlush = false;
        await receiver.DispatchBatchAsync(messages, Subscription(app).Id, Token);
        await VerifyAsync(app, 2);
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenCancelledAfterFlush_ShouldRollBackAndNeverTreatCancellationAsSuccess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var probe = new BatchInboxProbe { AfterFlush = cancellation.Cancel };
        await using var app = await Factory(probe).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Services.GetRequiredService<IEventReceiveDispatcher>()
            .DispatchBatchAsync(messages, Subscription(app).Id, cancellation.Token));
        await VerifyAsync(app, 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchBatchAsync_WhenEnvelopeInvalid_ShouldRejectBeforeWriting(bool invalidRoute)
    {
        var probe = new BatchInboxProbe();
        await using var app = await Factory(probe).CreateAsync(cancellationToken: Token);
        var messages = await PrepareAsync(app, "a", "b");
        messages[1] = messages[1] with { Metadata = invalidRoute
            ? messages[1].Metadata with { TopicName = "wrong-topic" }
            : messages[1].Metadata with { MessageId = null } };
        await Assert.ThrowsAnyAsync<Exception>(() => app.Services.GetRequiredService<IEventReceiveDispatcher>()
            .DispatchBatchAsync(messages, Subscription(app).Id, Token));
        Assert.Empty(probe.ContextIds);
        await VerifyAsync(app, 0);
    }

    [Fact]
    public async Task DispatchTestBatchAsync_WhenHeld_ShouldRunExplicitlyWithoutActivatingSubscription()
    {
        await using var app = await Factory(new(), manual: true).CreateAsync(cancellationToken: Token);
        var subscription = Subscription(app);
        var messages = await PrepareAsync(app, "a", "b");
        var receiver = app.Services.GetRequiredService<IEventReceiveDispatcher>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.DispatchBatchAsync(messages, subscription.Id, Token));
        await receiver.DispatchTestBatchAsync(messages, subscription.Id, Token);
        await receiver.DispatchTestBatchAsync(messages, subscription.Id, Token);
        Assert.Equal(EventSubscriptionState.Held, subscription.State);
        await VerifyAsync(app, 2);
    }

    [Fact]
    public async Task SubscribeAsync_WhenBatchGroupAlreadyOwned_ShouldRejectMixedModesButAllowAnotherOwner()
    {
        await using var app = await Factory(new()).CreateAsync(cancellationToken: Token);
        var registry = app.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var descriptor = new EventSubscriptionDescriptor
        {
            EventType = typeof(BatchInboxNotice), TopicName = Subscription(app).TopicName,
            Scope = EventSubscriptionScope.Distributed,
            HandlerFactory = new IocEventHandlerFactory(app.Services.GetRequiredService<IServiceScopeFactory>(), typeof(SingleBatchInboxHandler))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SubscribeAsync(descriptor, Token));
        var separate = await registry.SubscribeAsync(descriptor with { WorkloadOwner = "other-owner" }, Token);
        Assert.False(separate.IsBatch);
    }

    private static RepositoryScenarioFactory Factory(BatchInboxProbe probe, IInterceptor? interceptor = null, bool manual = false)
        => new(inbox: true, interceptor: interceptor, manual: manual, configure: services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<BatchInboxHandler>();
        });

    private static IEventSubscription Subscription(MonicaTestApplication app)
        => app.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll()
            .Single(subscription => subscription.HandlerType == typeof(BatchInboxHandler));

    private static async Task<EventMessage[]> PrepareAsync(MonicaTestApplication app, params string[] values)
    {
        await using var scope = app.CreateScope(Token);
        return values.Select(value => scope.Resolve<IEventMessageFactory>().Prepare(typeof(BatchInboxNotice),
            new BatchInboxNotice(value), EventSubscriptionScope.Distributed)).ToArray();
    }

    private static Task VerifyAsync(MonicaTestApplication app, int count) => app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
    {
        Assert.Equal(count, await db.SoftDeleteRows.CountAsync(ct));
        Assert.Equal(count, await db.Set<InboxReceipt>().CountAsync(ct));
        Assert.Equal(count, await db.Set<OutboxMessage>().CountAsync(ct));
    }, Token);

    private sealed class ReceiptQueryCounter : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("MonicaInbox", StringComparison.Ordinal)) Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

[EventName("tests.batch-inbox.v1")]
public sealed record BatchInboxNotice(string Value);

public sealed class BatchInboxProbe
{
    public bool FailAfterFlush { get; set; }
    public Action? AfterFlush { get; set; }
    public ConcurrentQueue<Guid> ContextIds { get; } = new();
    public ConcurrentQueue<string[]> Payloads { get; } = new();
    public ConcurrentQueue<string[]> MessageIds { get; } = new();
}

[Inbox("tests.batch-inbox")]
public sealed class BatchInboxHandler(TestRepositoryDbContext db, BatchInboxProbe probe,
    IEventBatchDeliveryContext batch, IEventDeliveryContext single) : IDistributedBatchEventHandler<BatchInboxNotice>
{
    public async Task HandleBatchAsync(IReadOnlyList<BatchInboxNotice> events, CancellationToken cancellationToken)
    {
        Assert.Null(single.Current);
        Assert.Equal(events.Count, batch.Current.Count);
        probe.ContextIds.Enqueue(db.ContextId.InstanceId);
        probe.Payloads.Enqueue(events.Select(item => item.Value).ToArray());
        probe.MessageIds.Enqueue(batch.Current.Select(item => item.MessageId!).ToArray());
        db.SoftDeleteRows.AddRange(events.Select(item => new SoftDeleteAuditRow { Title = item.Value }));
        await db.SaveChangesAsync(cancellationToken);
        probe.AfterFlush?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (probe.FailAfterFlush) throw new InvalidOperationException("Failed after the transaction flushed.");
    }
}

public sealed class SingleBatchInboxHandler : IDistributedEventHandler<BatchInboxNotice>
{
    public Task HandleEventAsync(BatchInboxNotice eventData, CancellationToken cancellationToken) => Task.CompletedTask;
}
