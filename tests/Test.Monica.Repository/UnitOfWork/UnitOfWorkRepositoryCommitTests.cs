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
    [InlineData(ResStatus.Created, true, false)]
    [InlineData(ResStatus.Ok, true, true)]
    [InlineData(ResStatus.Conflict, false, false)]
    [InlineData(ResStatus.BadRequest, false, true)]
    public async Task MvcOutcome_WhenReturned_ShouldHonorEnvelopeStatus(ResStatus status, bool committed, bool json)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "mvc" });
            IActionResult result = json ? new JsonResult(new Res("outcome", status)) : new ObjectResult(new Res("outcome", status));
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

    [Fact]
    public async Task ExecuteAsync_WhenResultFails_ShouldRollBack()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var result = await app.ExecuteAsync(async scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "failure result" });
            await scope.Resolve<IUnitOfWorkManager>().Current!.FlushAsync(Token);
            return Res.Fail("declined");
        }, cancellationToken: Token);
        Assert.Equal(ResStatus.BadRequest, result.Status);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(0, await db.SoftDeleteRows.CountAsync(ct)), Token);
    }

    [Fact]
    public async Task RunAsync_WhenNestedFailureIsCaught_ShouldPreventCommit()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var rollback = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => app.ExecuteAsync(async scope =>
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
        }, cancellationToken: Token));
        Assert.Equal("nested failure", Assert.IsType<ApplicationException>(rollback.InnerException).Message);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal(0, await db.SoftDeleteRows.CountAsync(ct)), Token);
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
            scope.Resolve<IUnitOfWorkManager>().Current!.MarkRollbackOnly();
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
