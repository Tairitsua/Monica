using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class EntityEventPublishingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ManualHost_StagesOutboxWithoutStartingConfiguredWorker()
    {
        await using var app = await new RepositoryScenarioFactory(manual: true,
            configureOutbox: options => options.EnableWorker = true).CreateAsync(cancellationToken: Token);
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service.GetType().Name == "OutboxWorker");
        Assert.Contains(app.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.ImplementationType == typeof(TestRepositoryDbContext)
                && resource.Kind == TypeActivationKind.Outbox && resource.Mode == ActivationMode.Manual);
        await app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<HardDeleteRow>>().Add(new() { Title = "manual business" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("pending"), cancellationToken: Token);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(1, await db.HardDeleteRows.CountAsync(ct));
            var message = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
            Assert.Null(message.DeliveredAtUtc);
            Assert.Equal(0, message.Attempts);
            Assert.Null(message.LeaseId);
        }, Token);
    }

    [Fact]
    public async Task Publish_WhenOperationCommits_ShouldPersistAnImmutableSnapshotWithBusinessRows()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var notice = new MutableNotice { Value = "captured" };
        await app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<HardDeleteRow>>().Add(new() { Title = "business" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(notice, cancellationToken: Token);
            notice.Value = "mutated";
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(1, await db.HardDeleteRows.CountAsync(ct));
            var message = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
            Assert.Equal("tests.mutable.v1", message.EventName);
            Assert.Equal("captured", JsonSerializer.Deserialize<MutableNotice>(message.Body, Json)!.Value);
            Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        }, Token);
    }

    [Fact]
    public async Task Publish_WhenOperationRollsBackAfterFlush_ShouldDiscardBusinessAndMessageRows()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<HardDeleteRow>>().Add(new() { Title = "rolled back" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("rolled back"), cancellationToken: Token);
            await scope.Resolve<IUnitOfWorkManager>().Current!.FlushAsync(Token);
            throw new InvalidOperationException("abort");
        }, cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task EntityProjection_WhenKeyIsGenerated_ShouldCaptureFinalValueBeforeCommit()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        int key = 0;
        await app.ExecuteAsync(scope =>
        {
            var row = new GeneratedKeyRow { Title = "generated" };
            scope.Resolve<TestRepositoryDbContext>().Add(row);
            return Task.CompletedTask;
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            key = Assert.Single(await db.GeneratedRows.ToListAsync(ct)).Id;
            var message = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
            var payload = JsonSerializer.Deserialize<GeneratedChanged>(message.Body, Json)!;
            Assert.Equal(key, payload.Entity.Id);
            Assert.Equal("generated", payload.Entity.Title);
        }, Token);
        Assert.True(key > 0);
    }

    [Fact]
    public async Task EntityProjection_WhenAsyncProjectionUsesSynchronousSave_ShouldRejectBeforeWriting()
    {
        await using var app = await new RepositoryScenarioFactory(configureProjections: options =>
            options.RegisterEntity<GeneratedKeyRow, AsyncGeneratedChanged>((_, change, _) =>
                ValueTask.FromResult<AsyncGeneratedChanged?>(new(((GeneratedKeyRow)change.Entry.Entity).Title))))
            .CreateAsync(cancellationToken: Token);
        await using (var scope = app.CreateScope(Token))
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.GeneratedRows.Add(new GeneratedKeyRow { Title = "not written" });
            Assert.Throws<NotSupportedException>(() => db.SaveChanges());
        }
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.GeneratedRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task Publish_OutsideAnActiveOperation_ShouldFailBeforeSending()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await using var scope = app.CreateScope(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("invalid"), cancellationToken: Token));
    }

    [Fact]
    public async Task Publish_WhenSerializationFailsAndCallerCatches_ShouldRollBackTheOperation()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            scope.Resolve<TestRepositoryDbContext>().HardDeleteRows.Add(new HardDeleteRow { Title = "must roll back" });
            await Assert.ThrowsAnyAsync<Exception>(() => scope.Resolve<IDistributedEventBus>()
                .PublishAsync(new UnserializableNotice(() => { }), cancellationToken: Token));
        }, cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task Publish_WhenCanceledAndCallerCatches_ShouldRollBackTheOperation()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            scope.Resolve<TestRepositoryDbContext>().HardDeleteRows.Add(new HardDeleteRow { Title = "must roll back" });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Resolve<IDistributedEventBus>()
                .PublishAsync(new IntegrationNotice("canceled"), cancellationToken: canceled.Token));
        }, cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task Drain_WhenTransportFails_ShouldRetryIdentityAndContinueWithOtherDueMessages()
    {
        var transport = new FlakyTransport();
        var time = new ManualTimeProvider();
        await using var app = await new RepositoryScenarioFactory(configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.RemoveAll<IEventTransport>();
            services.AddSingleton<IEventTransport>(transport);
        }).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var bus = scope.Resolve<IDistributedEventBus>();
            await bus.PublishAsync(new IntegrationNotice("first"), cancellationToken: Token);
            await bus.PublishAsync(new IntegrationNotice("second"), cancellationToken: Token);
        }, cancellationToken: Token);

        Assert.Equal(1, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        Assert.Equal(3, transport.Attempts.Count);
        Assert.Equal(transport.Attempts[0].Metadata.MessageId, transport.Attempts[2].Metadata.MessageId);
        Assert.Equal(transport.Attempts[0].Body.ToArray(), transport.Attempts[2].Body.ToArray());
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.All(await db.Set<OutboxMessage>().ToListAsync(ct), row => Assert.NotNull(row.DeliveredAtUtc)), Token);
    }

    [Fact]
    public async Task Drain_WhenOldestRowHasLiveLease_ShouldSendNextDueRow()
    {
        var transport = new FlakyTransport(failFirst: false);
        await using var app = await new RepositoryScenarioFactory(configure: services =>
        {
            services.RemoveAll<IEventTransport>();
            services.AddSingleton<IEventTransport>(transport);
        }).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var bus = scope.Resolve<IDistributedEventBus>();
            await bus.PublishAsync(new IntegrationNotice("leased"), cancellationToken: Token);
            await bus.PublishAsync(new IntegrationNotice("due"), cancellationToken: Token);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var first = await db.Set<OutboxMessage>().OrderBy(x => x.Sequence).FirstAsync(ct);
            await db.Set<OutboxMessage>().Where(x => x.Sequence == first.Sequence).ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LeaseId, Guid.NewGuid())
                    .SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(5)), ct);
        }, Token);
        Assert.Equal(1, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        Assert.Equal("due", JsonSerializer.Deserialize<IntegrationNotice>(transport.Attempts.Single().Body.Span, Json)!.Value);
    }

    [Fact]
    public async Task Drain_WhenLeaseExpires_ShouldReclaimTheSameStoredMessage()
    {
        var time = new ManualTimeProvider();
        var transport = new FlakyTransport(failFirst: false);
        await using var app = await new RepositoryScenarioFactory(configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.RemoveAll<IEventTransport>();
            services.AddSingleton<IEventTransport>(transport);
        }).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("expired"), cancellationToken: Token),
            cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            await db.Set<OutboxMessage>().ExecuteUpdateAsync(setters =>
                setters.SetProperty(x => x.LeaseId, Guid.NewGuid())
                    .SetProperty(x => x.LeaseUntilUtc, time.GetUtcNow().UtcDateTime.AddSeconds(30)), ct);
        }, Token);
        Assert.Equal(0, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        Assert.Single(transport.Attempts);
    }

    [Fact]
    public async Task Drain_WhenDurableLocalEventHasNoSubscription_ShouldRemainPending()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
            await scope.Resolve<ILocalEventBus>().PublishAsync(new LocalNotice("unhandled"), cancellationToken: Token),
            cancellationToken: Token);
        Assert.Equal(0, await app.DrainOutboxAsync<TestRepositoryDbContext>(cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var row = Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
            Assert.Null(row.DeliveredAtUtc);
            Assert.Equal(1, row.Attempts);
            Assert.False(string.IsNullOrWhiteSpace(row.LastError));
        }, Token);
    }

    [Fact]
    public void EntityProjections_ShouldRejectDuplicateEntityAndEventPair()
    {
        var options = new RepositoryEntityEventOptions();
        options.RegisterEntity<GeneratedKeyRow, GeneratedChanged>(row =>
            new GeneratedChanged(PersistenceChangeKind.Created,
                new GeneratedProjection(row.Id, row.Title)));
        Assert.Throws<InvalidOperationException>(() => options.RegisterEntity<GeneratedKeyRow, GeneratedChanged>(row =>
            new GeneratedChanged(PersistenceChangeKind.Created,
                new GeneratedProjection(row.Id, row.Title))));
    }

    private sealed class FlakyTransport(bool failFirst = true) : IEventTransport
    {
        public List<EventMessage> Attempts { get; } = [];
        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        {
            Attempts.Add(message);
            return failFirst && Attempts.Count == 1
                ? Task.FromException(new IOException("broker accepted but acknowledgment was lost"))
                : Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}

[Outbox, EventName("tests.mutable.v1")]
public sealed class MutableNotice
{
    public string Value { get; set; } = string.Empty;
}

[Outbox, EventName("tests.local-unhandled.v1")]
public sealed record LocalNotice(string Value);

[Outbox, EventName("tests.unserializable.v1")]
public sealed record UnserializableNotice(Action Callback);
