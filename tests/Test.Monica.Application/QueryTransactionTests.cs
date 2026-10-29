using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Monica.Application.Abstractions;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.Invocation.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Contracts;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;
using Xunit;

namespace Test.Monica.Application;

public sealed class QueryTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_WhenStandaloneOrIndependent_ShouldNotOpenTransaction(bool independent)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var api = scope.Resolve<ITestingQueryApi>();
        if (independent) api = api.WithTransaction(ApiTransaction.Independent);
        var result = await api.ReadTransaction(new(), token);
        Assert.Equal(ResStatus.Ok, result.Status);
        Assert.False(result.Data!.HasTransaction);
    }

    [Fact]
    public async Task SharedQuery_WhenParentFlushedUncommittedWrites_ShouldReadSameTransaction()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        await manager.RunAsync(async () =>
        {
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "uncommitted" });
            await manager.Current!.FlushAsync(token);
            var result = await scope.Resolve<ITestingQueryApi>().ReadTransaction(new(), token);
            Assert.True(result.Data!.HasTransaction);
            Assert.Equal(1, result.Data.Rows);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token);
    }

    [Theory]
    [InlineData(ResStatus.BadRequest)]
    [InlineData(ResStatus.Unauthorized)]
    [InlineData(ResStatus.Forbidden)]
    [InlineData(ResStatus.NotFound)]
    [InlineData(ResStatus.Conflict)]
    [InlineData(ResStatus.PayloadTooLarge)]
    [InlineData(ResStatus.UnsupportedMediaType)]
    public async Task SharedQuery_WhenBusinessRejectionIsHandled_ShouldCommitParentAndOutbox(ResStatus rejection)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        await scope.Resolve<IUnitOfWorkManager>().RunAsync(async () =>
        {
            var result = await scope.Resolve<ITestingQueryApi>().ReadTransaction(new(rejection), token);
            Assert.Equal(rejection, result.Status);
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "handled query rejection" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new TransactionCompleted(), cancellationToken: token);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
        {
            Assert.Single(await db.Entries.ToListAsync(ct));
            Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, token);
    }

    [Theory]
    [InlineData(ResStatus.TooManyRequests)]
    [InlineData(ResStatus.InternalError)]
    [InlineData(ResStatus.BadGateway)]
    [InlineData(ResStatus.ServiceUnavailable)]
    [InlineData(ResStatus.GatewayTimeout)]
    [InlineData(ResStatus.Unknown)]
    public async Task SharedQuery_WhenExecutionFailureIsHandled_ShouldStillRollbackParent(ResStatus rejection)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(async () =>
        {
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "must roll back" });
            await manager.Current!.FlushAsync(token);
            var result = await scope.Resolve<ITestingQueryApi>().ReadTransaction(new(rejection), token);
            Assert.Equal(rejection, result.Status);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Empty(await db.Entries.ToListAsync(ct)), token);
    }

    [Theory]
    [InlineData(QueryFault.Exception)]
    [InlineData(QueryFault.Cancellation)]
    [InlineData(QueryFault.CaughtSql)]
    public async Task SharedQuery_WhenFaultIsCaught_ShouldNotRecoverFailedTransaction(QueryFault fault)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(async () =>
        {
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "must roll back" });
            await manager.Current!.FlushAsync(token);
            var invoke = () => scope.Resolve<ITestingQueryApi>().ReadTransaction(new(Fault: fault), token);
            if (fault == QueryFault.CaughtSql)
                Assert.Equal(ResStatus.BadRequest, (await invoke()).Status);
            else if (fault == QueryFault.Cancellation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(invoke);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Empty(await db.Entries.ToListAsync(ct)), token);
    }

    [Fact]
    public async Task Query_WhenConsistencyTransactionIsExplicit_ShouldNotFlushUnrelatedTrackedChanges()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "not owned by this query" });
        var result = await scope.Resolve<IMediator>().Send(new QueryConsistentRead(), token);
        Assert.True(result.Data!.HasTransaction);
        Assert.Equal(0, result.Data.Rows);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Empty(await db.Entries.ToListAsync(ct)), token);
    }
}

public sealed class QueryHandlerReadTransaction(TransactionPrimaryContext db)
    : ApplicationService<QueryReadTransaction, QueryTransactionReceipt>
{
    public override async Task<Res<QueryTransactionReceipt>> Handle(QueryReadTransaction request, CancellationToken cancellationToken)
    {
        if (request.Fault == QueryFault.Exception) throw new InvalidOperationException("Query execution failed.");
        if (request.Fault == QueryFault.Cancellation) throw new OperationCanceledException("Query execution was cancelled.");
        if (request.Fault == QueryFault.CaughtSql)
        {
            try { await db.Database.SqlQueryRaw<int>("SELECT Value FROM MissingQueryTable").ToListAsync(cancellationToken); }
            catch (DbException) { return Res.Fail("A provider error must not become a recoverable business rejection."); }
        }
        if (request.Rejection is { } rejection) return Res.Fail(rejection, "Query rejected.");
        return new QueryTransactionReceipt(db.Database.CurrentTransaction is not null, await db.Entries.CountAsync(cancellationToken));
    }
}

public sealed class QueryHandlerConsistentRead(TransactionPrimaryContext db)
    : ApplicationService<QueryConsistentRead, QueryTransactionReceipt>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = [typeof(TransactionPrimaryContext)],
        IsolationLevel = IsolationLevel.Serializable)]
    public override async Task<Res<QueryTransactionReceipt>> Handle(QueryConsistentRead request, CancellationToken cancellationToken)
        => new QueryTransactionReceipt(db.Database.CurrentTransaction is not null, await db.Entries.CountAsync(cancellationToken));
}
