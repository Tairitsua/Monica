using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Monica.Core.Execution;
using Monica.EventBus.Abstractions;
using Monica.Repository.Outbox.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.Persistence;

public sealed class RepositorySaveTransactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_WhenEmpty_ShouldInvokeCallbacksWithoutDatabaseCommands(bool useAsync, bool managed)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);

        async Task SaveEmptyAsync(TestRepositoryDbContext db)
        {
            observer.Reset();
            var saving = 0;
            var saved = 0;
            db.SavingChanges += (_, _) => saving++;
            db.SavedChanges += (_, _) => saved++;
            Assert.Equal(0, await SaveAsync(db, useAsync));
            Assert.Equal(1, saving);
            Assert.Equal(1, saved);
            Assert.Equal(0, observer.Commands);
            Assert.Equal(0, observer.Starts);
            Assert.Equal(0, observer.Commits);
            Assert.Equal(0, observer.Savepoints);
            Assert.Equal(0, observer.Releases);
            Assert.True(db.Database.AutoSavepointsEnabled);
        }

        if (managed)
            await app.ExecuteAsync(scope => SaveEmptyAsync(scope.Resolve<TestRepositoryDbContext>()), cancellationToken: Token);
        else
        {
            await using var scope = app.CreateScope(Token);
            await SaveEmptyAsync(scope.Resolve<TestRepositoryDbContext>());
        }
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Releases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_WhenManaged_ShouldPersistBusinessAndMessagesWithoutSavepoints(bool useAsync, bool autoSavepoints)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        var key = 0;
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            db.Database.AutoSavepointsEnabled = autoSavepoints;
            var row = new GeneratedKeyRow { Title = "generated" };
            db.Add(row);
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("explicit"), cancellationToken: Token);
            Assert.Equal(1, await SaveAsync(db, useAsync));
            key = row.Id;
            Assert.Equal(autoSavepoints, db.Database.AutoSavepointsEnabled);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            await scope.Resolve<IUnitOfWorkManager>().Current!.FlushAsync(Token);
        }, cancellationToken: Token);

        Assert.True(key > 0);
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Releases);
        Assert.Equal(0, observer.Starts);
        Assert.Equal(1, observer.Commits);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(key, Assert.Single(await db.GeneratedRows.ToListAsync(ct)).Id);
            var messages = await db.Set<OutboxMessage>().ToListAsync(ct);
            Assert.Equal(2, messages.Count);
            var generated = Assert.Single(messages, message => message.EventName == "tests.generated-change.v1");
            Assert.Equal(key, JsonSerializer.Deserialize<GeneratedChanged>(generated.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entity.Id);
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WhenOnlyMessageIsPending_ShouldPersistItWithoutSavepoints(bool useAsync)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("message only"), cancellationToken: Token);
            Assert.False(db.ChangeTracker.HasChanges());
            Assert.Equal(0, await SaveAsync(db, useAsync));
        }, cancellationToken: Token);
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Releases);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_WhenCallerOwnsTransaction_ShouldRetainEfSavepointSetting(bool useAsync, bool autoSavepoints)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        await using (var scope = app.CreateScope(Token))
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Token);
            observer.Reset();
            db.Database.AutoSavepointsEnabled = autoSavepoints;
            db.Add(new HardDeleteRow { Title = "caller owned" });
            Assert.Equal(1, await SaveAsync(db, useAsync));
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Equal(autoSavepoints, db.Database.AutoSavepointsEnabled);
            Assert.Equal(autoSavepoints ? 1 : 0, observer.Savepoints);
            Assert.Equal(autoSavepoints ? 1 : 0, observer.Releases);
            Assert.Equal(0, observer.Starts);
            Assert.Equal(0, observer.Commits);
            await transaction.RollbackAsync(Token);
        }
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct)), Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_WhenFailureIsCaught_ShouldFaultContextAndRollBackWholeOperation(bool useAsync, bool failOutbox)
    {
        var observer = new SaveObserver();
        var failure = new ApplicationException(failOutbox ? "outbox save failed" : "business save callback failed");
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            if (failOutbox)
                observer.Saving = context =>
                {
                    if (context.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.State == EntityState.Added))
                        throw failure;
                };
            else
                observer.Saved = _ => throw failure;
            db.Add(new GeneratedKeyRow { Title = "must roll back" });
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => SaveAsync(db, useAsync)));
            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            var retry = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => SaveAsync(db, useAsync));
            Assert.Same(failure, retry.InnerException);
        }, cancellationToken: Token));

        Assert.Same(failure, rejected.InnerException);
        Assert.True(observer.Commands > 0);
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Releases);
        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, observer.Rollbacks);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.GeneratedRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_WhenCallbackMarksRollbackOnly_ShouldThrowBeforeReturning(bool useAsync, bool empty)
    {
        var observer = new SaveObserver();
        var failure = new ExecutionTransactionFailure("SaveCallback", "The save callback rejected the operation.");
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        ExecutionTransactionFailure? firstFailure = null;
        var saves = 0;
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var operation = scope.Resolve<IUnitOfWorkManager>().Current!;
            observer.Reset();
            observer.Saving = _ => saves++;
            observer.Saved = _ =>
            {
                operation.MarkRollbackOnly(failure);
                firstFailure = operation.FirstFailure;
            };
            if (!empty)
            {
                db.Add(new HardDeleteRow { Title = "must roll back" });
                await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("must not flush"), cancellationToken: Token);
            }

            var saveFailure = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => SaveAsync(db, useAsync));
            Assert.Same(firstFailure, saveFailure.FirstFailure);
            Assert.Null(saveFailure.InnerException);
            Assert.Equal(1, saves);
            Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());
            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        }, cancellationToken: Token));

        Assert.Same(firstFailure, rejected.FirstFailure);
        Assert.Equal(failure.Source, rejected.FirstFailure.Source);
        Assert.Equal(failure.Reason, rejected.FirstFailure.Reason);
        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, observer.Rollbacks);
        if (empty) Assert.Equal(0, observer.Commands);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WhenOutboxSaveCallbackMarksRollbackOnly_ShouldThrowBeforeReturning(bool useAsync)
    {
        var observer = new SaveObserver();
        var failure = new ExecutionTransactionFailure("SaveCallback", "The Outbox save callback rejected the operation.");
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        ExecutionTransactionFailure? firstFailure = null;
        var saves = 0;
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var operation = scope.Resolve<IUnitOfWorkManager>().Current!;
            observer.Reset();
            observer.Saved = context =>
            {
                saves++;
                if (context.ChangeTracker.Entries<OutboxMessage>().Any())
                {
                    operation.MarkRollbackOnly(failure);
                    firstFailure = operation.FirstFailure;
                }
            };
            db.Add(new HardDeleteRow { Title = "must roll back" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new IntegrationNotice("must roll back"), cancellationToken: Token);

            var saveFailure = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => SaveAsync(db, useAsync));
            Assert.Same(firstFailure, saveFailure.FirstFailure);
            Assert.Null(saveFailure.InnerException);
            Assert.Equal(2, saves);
            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        }, cancellationToken: Token));

        Assert.Same(firstFailure, rejected.FirstFailure);
        Assert.Equal(failure.Source, rejected.FirstFailure.Source);
        Assert.Equal(failure.Reason, rejected.FirstFailure.Reason);
        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, observer.Rollbacks);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task RunAsync_WhenFinalSaveMarksRollbackOnly_ShouldRejectCommit()
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        var failure = new ExecutionTransactionFailure("SaveCallback", "The final save rejected the operation.");
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            observer.Saved = _ => scope.Resolve<IUnitOfWorkManager>().Current!.MarkRollbackOnly(failure);
            db.Add(new HardDeleteRow { Title = "late rejection" });
            return Task.CompletedTask;
        }, cancellationToken: Token));
        Assert.Equal(failure.Source, rejected.FirstFailure.Source);
        Assert.Equal(failure.Reason, rejected.FirstFailure.Reason);
        Assert.True(observer.Commands > 0);
        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, observer.Rollbacks);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct)), Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WhenCallbackAddsToEmptyContext_ShouldPersistTheNewEntry(bool useAsync)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            observer.Saving = context =>
            {
                observer.Saving = null;
                context.Add(new HardDeleteRow { Title = "added by callback" });
            };
            Assert.False(db.ChangeTracker.HasChanges());
            Assert.Equal(1, await SaveAsync(db, useAsync));
        }, cancellationToken: Token);
        Assert.Equal(0, observer.Savepoints);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal("added by callback", Assert.Single(await db.HardDeleteRows.ToListAsync(ct)).Title), Token);
    }

    [Fact]
    public async Task SaveChanges_WhenEmptyAndCanceled_ShouldPreventCommitWithoutDatabaseCommands()
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancellation.Token));
            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        }, cancellationToken: Token));
        Assert.IsAssignableFrom<OperationCanceledException>(rejected.InnerException);
        Assert.Equal(0, observer.Commands);
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, observer.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WhenProjectionHasNoManagedTransaction_ShouldRejectBeforeWriting(bool useAsync)
    {
        var observer = new SaveObserver();
        await using var app = await new RepositoryScenarioFactory(interceptor: observer).CreateAsync(cancellationToken: Token);
        await using (var scope = app.CreateScope(Token))
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            observer.Reset();
            db.Add(new GeneratedKeyRow { Title = "requires managed ownership" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(db, useAsync));
            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.Equal(0, observer.Commands);
            Assert.Equal(0, observer.Starts);
            Assert.Equal(0, observer.Savepoints);
        }
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.GeneratedRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    private static async Task<int> SaveAsync(DbContext context, bool useAsync)
        => useAsync ? await context.SaveChangesAsync(Token) : context.SaveChanges();

    private sealed class SaveObserver : SaveTransactionObserver, ISaveChangesInterceptor, IDbCommandInterceptor
    {
        public Action<DbContext>? Saving { get; set; }
        public Action<DbContext>? Saved { get; set; }
        public int Commands { get; private set; }

        public new void Reset()
        {
            base.Reset();
            Commands = 0;
        }

        public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Saving?.Invoke(eventData.Context!);
            return result;
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SavingChanges(eventData, result));

        public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            Saved?.Invoke(eventData.Context!);
            return result;
        }

        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SavedChanges(eventData, result));

        public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands++;
            return result;
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReaderExecuting(command, eventData, result));

        public InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            Commands++;
            return result;
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NonQueryExecuting(command, eventData, result));

        public InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            Commands++;
            return result;
        }

        public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ScalarExecuting(command, eventData, result));
    }
}
