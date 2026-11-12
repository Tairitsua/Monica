using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Authority.Identity.Abstractions;
using Monica.Core.Execution;
using Monica.EventBus.Abstractions;
using Monica.Modules;
using Monica.Repository.Bulk.Abstractions;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Outbox.Models;
using Monica.Testing.Doubles;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.Bulk;

public sealed class BulkWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RepositoryScenarioFactory Factory(bool nativeOutbox = false) => new(
        configureRepository: repository => repository.UseBulkInsert<TestRepositoryDbContext, SqliteNativeInsertProvider>(),
        configureOutbox: options => options.UseBulkInsert = nativeOutbox);

    [Fact]
    public async Task Execute_WhenAuditOptedIn_ShouldUseScopedConventionsWithoutChangingOtherOperationsOrGeneratingEvents()
    {
        var clock = new FixedClock();
        await using var app = await Factory().CreateAsync(seams => seams
            .With<TimeProvider>(clock)
            .With<ICurrentUser>(new TestCurrentUser("importer", "Import User")), Token);
        var plain = new SoftDeleteAuditRow { Id = 101, Title = "plain" };
        var audited = new SoftDeleteAuditRow { Id = 102, Title = "audited" };
        var supplied = new SoftDeleteAuditRow
        {
            Id = 103, Title = "supplied", CreatorId = "original", CreationTime = new DateTime(2020, 1, 2)
        };
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var writer = scope.Resolve<IBulkWriter>();
            var operation = writer.Insert(db, new[] { plain });
            _ = operation.WithAuditProperties();
            await operation.ExecuteAsync(Token);
            await writer.Insert(db, new[] { audited, supplied }).WithAuditProperties().ExecuteAsync(Token);
            Assert.Empty(db.ChangeTracker.Entries());
        }, cancellationToken: Token);

        Assert.Equal(default, plain.CreationTime);
        Assert.Null(plain.CreatorId);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, audited.CreationTime);
        Assert.Equal("importer", audited.CreatorId);
        Assert.Equal(new DateTime(2020, 1, 2), supplied.CreationTime);
        Assert.Equal("original", supplied.CreatorId);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(3, await db.SoftDeleteRows.CountAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync(ct));
            Assert.Equal(0, (await db.SoftDeleteRows.SingleAsync(row => row.Id == 102, ct)).EntityVersion);
        }, Token);
    }

    [Fact]
    public async Task Execute_WhenEmptyOrOutsideTransaction_ShouldAvoidIOAndRequireManagedOwnershipForWrites()
    {
        await using var app = await Factory().CreateAsync(cancellationToken: Token);
        await using var scope = app.CreateScope(Token);
        var db = scope.Resolve<TestRepositoryDbContext>();
        var writer = scope.Resolve<IBulkWriter>();
        await writer.Insert(db, Array.Empty<HardDeleteRow>()).ExecuteAsync(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.Insert(db, Array.Empty<HardDeleteRow>())
            .ExecuteAsync(new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.Insert(db,
            new[] { new HardDeleteRow { Id = 10, Title = "outside" } }).ExecuteAsync(Token));
        Assert.Empty(app.Services.GetRequiredService<SqliteNativeInsertProvider>().InsertedTypes);
    }

    [Fact]
    public async Task Execute_WhenProviderMissing_ShouldRejectWithoutEFInsertion()
    {
        await using var app = await new RepositoryScenarioFactory(outbox: false).CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(scope =>
            scope.Resolve<IBulkWriter>().Insert(scope.Resolve<TestRepositoryDbContext>(),
                new[] { new HardDeleteRow { Id = 10, Title = "unsupported" } }).ExecuteAsync(Token), cancellationToken: Token));
        await AssertNoRowsAsync(app);
    }

    [Fact]
    public async Task Execute_WhenDuplicateOrTrackedKeysConflict_ShouldRejectBeforeProviderIO()
    {
        await using var app = await Factory().CreateAsync(cancellationToken: Token);
        await app.SeedAsync<TestRepositoryDbContext, int>(async (db, ct) =>
        {
            db.Add(new HardDeleteRow { Id = 10, Title = "existing" });
            return await db.SaveChangesAsync(ct);
        }, Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var writer = scope.Resolve<IBulkWriter>();
            await Assert.ThrowsAsync<ArgumentException>(() => writer.Insert(db, new[]
            {
                new HardDeleteRow { Id = 11 }, new HardDeleteRow { Id = 11 }
            }).ExecuteAsync(Token));
            var tracked = await db.HardDeleteRows.SingleAsync(Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.Insert(db,
                new[] { new HardDeleteRow { Id = tracked.Id } }).ExecuteAsync(Token));
            tracked.Title = "pending";
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.Insert(db,
                new[] { new HardDeleteRow { Id = 12 } }).ExecuteAsync(Token));
            Assert.Empty(scope.Resolve<SqliteNativeInsertProvider>().InsertedTypes);
        }, cancellationToken: Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WhenOutboxUsesNativeInsert_ShouldStoreIndependentMessagesOnceAndRetainGeneratedSequence(bool synchronous)
    {
        await using var app = await Factory(nativeOutbox: true).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.Add(new GeneratedKeyRow { Title = "tracked" });
            await scope.Resolve<IBulkWriter>().Insert(db, new[] { new HardDeleteRow { Id = 200, Title = "native" } })
                .WithAuditProperties().ExecuteAsync(Token);
            await scope.Resolve<IDistributedEventBus>().BulkPublishAsync(
                new[] { new IntegrationNotice("one"), new IntegrationNotice("two") }, cancellationToken: Token);
            if (synchronous) db.SaveChanges();
            else await db.SaveChangesAsync(Token);
            Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());
            Assert.Empty(db.ChangeTracker.Entries<HardDeleteRow>());
            // A second flush must not reinsert messages already written by the first flush.
            await db.SaveChangesAsync(Token);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Single(await db.HardDeleteRows.ToArrayAsync(ct));
            Assert.Single(await db.GeneratedRows.ToArrayAsync(ct));
            var messages = await db.Set<OutboxMessage>().OrderBy(message => message.Sequence).ToArrayAsync(ct);
            Assert.Equal(3, messages.Length);
            Assert.Equal(3, messages.Select(message => message.MessageId).Distinct().Count());
            Assert.All(messages, message => { Assert.True(message.Sequence > 0); Assert.Equal(0, message.Attempts); });
            var notices = messages.Where(message => message.EventName == "tests.notice.v1").ToArray();
            Assert.Equal(new[] { "one", "two" }, notices.Select(message =>
                JsonDocument.Parse(message.Body).RootElement.GetProperty("value").GetString()));
        }, Token);
        var provider = app.Services.GetRequiredService<SqliteNativeInsertProvider>();
        Assert.Equal(1, provider.InsertedTypes.Count(type => type == typeof(OutboxMessage)));
        Assert.Equal(synchronous ? 1 : 0, provider.SynchronousCalls);
    }

    [Fact]
    public async Task Execute_WhenLaterWorkFailsAfterFlush_ShouldRollBackTrackedNativeAndOutboxRows()
    {
        await using var app = await Factory(nativeOutbox: true).CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<ApplicationException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.Add(new GeneratedKeyRow { Title = "tracked" });
            await scope.Resolve<IBulkWriter>().Insert(db, new[] { new HardDeleteRow { Id = 200, Title = "native" } })
                .ExecuteAsync(Token);
            await db.SaveChangesAsync(Token);
            throw new ApplicationException("later operation failed");
        }, cancellationToken: Token));
        await AssertNoRowsAsync(app, outbox: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Execute_WhenFailureOrCancellationIsCaughtAfterNativeIO_ShouldRemainRollbackOnly(bool failOutbox, bool cancel)
    {
        await using var app = await Factory(nativeOutbox: true).CreateAsync(cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var original = new ApplicationException("native boundary failure");
        app.Services.GetRequiredService<SqliteNativeInsertProvider>().AfterInsert = type =>
        {
            if ((type == typeof(OutboxMessage)) != failOutbox) return;
            if (cancel) cancellation.Cancel();
            else throw original;
        };
        var failure = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.Add(new GeneratedKeyRow { Title = "tracked" });
            try
            {
                if (failOutbox) await db.SaveChangesAsync(Token);
                else await scope.Resolve<IBulkWriter>().Insert(db,
                    new[] { new HardDeleteRow { Id = 200, Title = "native" } }).ExecuteAsync(cancellation.Token);
            }
            catch (Exception exception) when (exception == original || exception is OperationCanceledException) { }
        }, cancellationToken: Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        else Assert.Same(original, failure.InnerException);
        await AssertNoRowsAsync(app, outbox: true);
    }

    [Fact]
    public async Task Execute_WhenAuditFails_ShouldMarkRollbackOnlyBeforeNativeIO()
    {
        await using var app = await Factory().CreateAsync(seams => seams.With<IAuditPropertySetter>(new FailingAudit()), Token);
        await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            await Assert.ThrowsAsync<ApplicationException>(() => scope.Resolve<IBulkWriter>()
                .Insert(scope.Resolve<TestRepositoryDbContext>(), new[] { new HardDeleteRow { Id = 200 } })
                .WithAuditProperties().ExecuteAsync(Token));
        }, cancellationToken: Token));
        Assert.Empty(app.Services.GetRequiredService<SqliteNativeInsertProvider>().InsertedTypes);
    }

    private static Task AssertNoRowsAsync(MonicaTestApplication app, bool outbox = false)
        => app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToArrayAsync(ct));
            Assert.Empty(await db.GeneratedRows.ToArrayAsync(ct));
            if (outbox) Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync(ct));
        }, Token);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingAudit : IAuditPropertySetter
    {
        public void SetCreationProperties(object targetObject) => throw new ApplicationException("audit input failed");
        public void SetModificationProperties(object targetObject) => throw new NotSupportedException();
        public void SetDeletionProperties(object targetObject) => throw new NotSupportedException();
        public void IncrementEntityVersionProperty(object targetObject) => throw new NotSupportedException();
    }
}
