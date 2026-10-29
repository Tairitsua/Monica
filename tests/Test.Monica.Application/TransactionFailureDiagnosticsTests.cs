using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.Execution;
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

public sealed class TransactionFailureDiagnosticsTests
{
    [Theory]
    [InlineData(QueryFault.Exception)]
    [InlineData(QueryFault.Cancellation)]
    [InlineData(QueryFault.CaughtSql)]
    public async Task SharedQuery_WhenFaultIsCaught_ShouldPreserveFirstCauseThroughOutboxSaveFlushAndCommit(QueryFault fault)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        using var activity = new Activity("transaction diagnostics").SetIdFormat(ActivityIdFormat.W3C).Start();
        var manager = scope.Resolve<IUnitOfWorkManager>();
        ExecutionTransactionFailure? first = null;
        var committed = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(async () =>
        {
            var db = scope.Resolve<TransactionPrimaryContext>();
            db.Entries.Add(new OperationEntry { Name = "must roll back" });
            await manager.Current!.FlushAsync(token);
            var invoke = () => scope.Resolve<ITestingQueryApi>().ReadTransaction(new(Fault: fault), token);
            Exception? original = null;
            if (fault == QueryFault.CaughtSql)
                Assert.Equal(ResStatus.BadRequest, (await invoke()).Status);
            else if (fault == QueryFault.Cancellation)
                original = await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);
            else
                original = await Assert.ThrowsAsync<InvalidOperationException>(invoke);

            first = Assert.IsType<ExecutionTransactionFailure>(manager.Current.FirstFailure);
            Assert.Contains(nameof(QueryHandlerReadTransaction), first.Operation);
            Assert.False(string.IsNullOrWhiteSpace(first.OperationKey));
            Assert.NotNull(first.InvocationId);
            Assert.Equal(activity.TraceId.ToString(), first.TraceId);
            Assert.NotNull(first.SpanId);
            Assert.NotEqual(default, first.OccurredAt);
            if (fault == QueryFault.CaughtSql)
            {
                Assert.IsAssignableFrom<DbException>(first.Exception);
                Assert.Equal("DatabaseCommand", first.Source);
                Assert.Equal(typeof(TransactionPrimaryContext).FullName, first.DbContextType);
                Assert.NotNull(first.CommandId);
            }
            else Assert.Same(original, first.Exception);

            // A later explicit mark or failed action must never replace the first evidence.
            manager.Current.MarkRollbackOnly(new("Later", "Do not overwrite the query failure."));
            var outbox = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => scope.Resolve<IDistributedEventBus>()
                .PublishAsync(new TransactionCompleted(), cancellationToken: token));
            var save = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => db.SaveChangesAsync(token));
            var flush = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.Current.FlushAsync(token));
            foreach (var rejected in new[] { outbox, save, flush })
            {
                Assert.Same(first, rejected.FirstFailure);
                Assert.Same(first.Exception, rejected.InnerException);
                Assert.Equal(manager.Current.Id, rejected.TransactionId);
                Assert.Contains(first.Reason, rejected.Message);
            }

            var diagnostics = new ExceptionDiagnosticDocument();
            ExceptionDiagnosticProjection.Capture(diagnostics, outbox);
            ExceptionDiagnosticProjection.Capture(diagnostics, flush);
            Assert.True(diagnostics.Contains(first.Exception!));
            var cause = Assert.Single(diagnostics.Exceptions, entry => entry.Type == first.Exception!.GetType().FullName);
            Assert.NotEmpty(cause.StackTrace);
            Assert.Contains(cause.StackTrace, frame => frame.Contains(nameof(QueryHandlerReadTransaction), StringComparison.Ordinal));
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        Assert.Same(first, committed.FirstFailure);
        Assert.Same(first!.Exception, committed.InnerException);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
        {
            Assert.Empty(await db.Entries.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, token);
        await using var nextScope = app.CreateScope(token);
        Assert.Null(nextScope.Resolve<IExecutionTransaction>().FirstFailure);
    }

    [Fact]
    public async Task SharedQuery_WhenFailureIsReturned_ShouldSnapshotStatusAndMessageWithoutInventingException()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(async () =>
        {
            var result = await scope.Resolve<ITestingQueryApi>().ReadTransaction(new(ResStatus.ServiceUnavailable), token);
            var first = Assert.IsType<ExecutionTransactionFailure>(manager.Current!.FirstFailure);
            result.Message = "changed after the failure";
            result.Status = ResStatus.Ok;
            var outbox = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => scope.Resolve<IDistributedEventBus>()
                .PublishAsync(new TransactionCompleted(), cancellationToken: token));
            Assert.Same(first, outbox.FirstFailure);
            Assert.Equal(ResStatus.ServiceUnavailable, first.ResultStatus);
            Assert.Equal("Query rejected.", first.Reason);
            Assert.Null(outbox.InnerException);
            Assert.Contains("ServiceUnavailable", outbox.Message);
            Assert.Contains(nameof(QueryHandlerReadTransaction), outbox.Message);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
    }

    [Fact]
    public async Task Query_WhenBusinessRejectionOrIndependentFaultIsHandled_ShouldLeaveParentDiagnosticsHealthy()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        await manager.RunAsync(async () =>
        {
            var api = scope.Resolve<ITestingQueryApi>();
            Assert.Equal(ResStatus.NotFound, (await api.ReadTransaction(new(ResStatus.NotFound), token)).Status);
            var independent = await api.WithTransaction(ApiTransaction.Independent)
                .ReadTransaction(new(Fault: QueryFault.Exception), token);
            Assert.Equal(ResStatus.InternalError, independent.Status);
            Assert.Null(manager.Current!.FirstFailure);
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "healthy" });
            await scope.Resolve<IDistributedEventBus>().PublishAsync(new TransactionCompleted(), cancellationToken: token);
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
        {
            Assert.Single(await db.Entries.ToListAsync(ct));
            Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, token);
    }

    [Fact]
    public async Task RunAsync_WhenOriginalFailureEscapes_ShouldRethrowSameException()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var original = new ApplicationException("original failure");
        var thrown = await Assert.ThrowsAsync<ApplicationException>(() => scope.Resolve<IUnitOfWorkManager>()
            .RunAsync(() => Task.FromException(original), new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        Assert.Same(original, thrown);
        Assert.Same(original, scope.Resolve<IExecutionTransaction>().FirstFailure!.Exception);
    }
}
