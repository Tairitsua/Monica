using Microsoft.EntityFrameworkCore;
using Monica.Core.Execution;
using Microsoft.AspNetCore.Mvc;
using Monica.Core.Execution.Mvc;
using Monica.Core.Results;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class UnitOfWorkRepositoryCommitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ResultStatus.Created, true, false)]
    [InlineData(ResultStatus.Ok, true, true)]
    [InlineData(ResultStatus.Conflict, true, false)]
    [InlineData(ResultStatus.BadRequest, true, true)]
    public async Task MvcOutcome_WhenReturned_ShouldCommitRegardlessOfEnvelopeStatus(ResultStatus status, bool committed, bool json)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "mvc" });
            var envelope = (status switch
            {
                ResultStatus.Created => Result.Created(),
                ResultStatus.Ok => Result.Success(),
                _ => Result.Failure("outcome", status)
            }).WithMessage("outcome");
            IActionResult result = json ? new JsonResult(envelope) : new ObjectResult(envelope);
            return Task.FromResult(MvcActionExecutionResult.ShortCircuit(result));
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(committed ? 1 : 0, await db.SoftDeleteRows.CountAsync(ct));
            Assert.Equal(committed ? 1 : 0, await db.Set<OutboxMessage>().CountAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WhenHandlerFails_ShouldRollBackStagedAndFlushedWrites(bool flush)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var expected = new InvalidOperationException("handler failed");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "must roll back" });
            if (flush) await scope.Resolve<TestRepositoryDbContext>().SaveChangesAsync(Token);
            throw expected;
        }, cancellationToken: Token));
        Assert.Same(expected, thrown);
        await app.ExecuteAsync(_ => Task.CompletedTask, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(0, await db.SoftDeleteRows.CountAsync(ct));
            Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WhenResultFails_ShouldCommitStagedAndFlushedWrites(bool flush)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var result = await app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "failure result" });
            if (flush) await scope.Resolve<IUnitOfWorkManager>().Current!.FlushAsync(Token);
            return Result.Failure("declined");
        }, cancellationToken: Token);
        Assert.Equal(ResultStatus.BadRequest, result.Status);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Single(await db.SoftDeleteRows.ToListAsync(ct));
            Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task RunAsync_WhenNestedFailureIsCaught_ShouldCommit()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var manager = scope.Resolve<IUnitOfWorkManager>();
            try
            {
                await manager.RunAsync(async () =>
                {
                    scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "nested" });
                    await manager.Current!.FlushAsync(Token);
                    throw new ApplicationException("nested failure");
                }, cancellationToken: Token);
            }
            catch (ApplicationException) { }
            Assert.Null(manager.Current!.FirstFailure);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Single(await db.SoftDeleteRows.ToListAsync(ct)), Token);
    }

    [Fact]
    public async Task RunAsync_WhenNestedWorkCatchesAbort_ShouldRejectBeforeReturningItsResult()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var afterChild = false;
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
        {
            var manager = scope.Resolve<IUnitOfWorkManager>();
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "aborted child" });
            await manager.Current!.FlushAsync(Token);
            await manager.RunAsync(() =>
            {
                Assert.Throws<TransactionRollbackOnlyException>(() => manager.Current.Abort(new("RequiredChild", "Cannot continue.")));
                return Task.FromResult(Result.Failure("handled abort"));
            }, cancellationToken: Token);
            afterChild = true;
        }, cancellationToken: Token));
        Assert.False(afterChild);
        Assert.Equal("RequiredChild", rejected.FirstFailure.Source);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.SoftDeleteRows.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenOperationEnds_ShouldRejectScopeReuseAndDirectWrites(bool fail)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await using var scope = app.CreateScope(Token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        var operation = () => manager.RunAsync(() => fail
            ? Task.FromException(new ApplicationException("failed"))
            : Task.CompletedTask, cancellationToken: Token);
        if (fail) await Assert.ThrowsAsync<ApplicationException>(operation);
        else await operation();
        Assert.Null(manager.Current);
        Assert.Throws<InvalidOperationException>(() => scope.Resolve<TestRepositoryDbContext>().ValidateDatabaseAccess());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(() => Task.CompletedTask, cancellationToken: Token));
        scope.Resolve<TestRepositoryDbContext>().Add(new SoftDeleteAuditRow { Title = "stale" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<TestRepositoryDbContext>().SaveChangesAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<TestRepositoryDbContext>().SoftDeleteRows
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Title, "stale SQL"), Token));
    }

    [Fact]
    public async Task ValidateDatabaseAccess_WhenOperationIsRollbackOnly_ShouldRejectNativeAccess()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.ValidateDatabaseAccess();
            Assert.Throws<TransactionRollbackOnlyException>(() => scope.Resolve<IUnitOfWorkManager>().Current!.Abort());
            var rejected = Assert.Throws<TransactionRollbackOnlyException>(db.ValidateDatabaseAccess);
            Assert.Equal("Explicit", rejected.FirstFailure.Source);
            return Task.CompletedTask;
        }, cancellationToken: Token));
    }

    [Fact]
    public async Task ExecuteAsync_WhenBulkSqlFails_ShouldRollBack()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "original" });
            return Task.CompletedTask;
        }, cancellationToken: Token);
        await Assert.ThrowsAsync<ApplicationException>(() => app.ExecuteAsync(async scope =>
        {
            await scope.Resolve<TestRepositoryDbContext>().SoftDeleteRows.ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Title, "bulk"), Token);
            throw new ApplicationException();
        }, cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal("original", (await db.SoftDeleteRows.SingleAsync(ct)).Title), Token);
    }
}
