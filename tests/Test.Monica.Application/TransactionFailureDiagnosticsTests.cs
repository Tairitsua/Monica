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
    [Fact]
    public async Task Abort_WhenCaughtQueryExceptionIsSupplied_ShouldPreserveCauseAndRejectFurtherWork()
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
            var original = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<ITestingQueryApi>()
                .ReadTransaction(new(Fault: QueryFault.Exception), token));
            Assert.Null(manager.Current.FirstFailure);

            var aborted = Assert.Throws<TransactionRollbackOnlyException>(() => manager.Current.Abort(
                ExecutionTransactionFailure.FromException("RequiredQuery", original)));
            first = Assert.IsType<ExecutionTransactionFailure>(manager.Current.FirstFailure);
            Assert.Same(original, first.Exception);
            Assert.Same(original, aborted.InnerException);
            Assert.Equal(activity.TraceId.ToString(), first.TraceId);
            Assert.NotNull(first.SpanId);
            Assert.NotEqual(default, first.OccurredAt);

            var later = Assert.Throws<TransactionRollbackOnlyException>(() => manager.Current.Abort(new("Later", "Do not replace the cause.")));
            var outbox = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => scope.Resolve<IDistributedEventBus>()
                .PublishAsync(new TransactionCompleted(), cancellationToken: token));
            var save = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => db.SaveChangesAsync(token));
            var flush = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.Current.FlushAsync(token));
            var nestedRan = false;
            var nested = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(() =>
            {
                nestedRan = true;
                return Task.CompletedTask;
            }, cancellationToken: token));
            Assert.False(nestedRan);
            foreach (var rejected in new[] { aborted, later, outbox, save, flush, nested })
            {
                Assert.Same(first, rejected.FirstFailure);
                Assert.Same(original, rejected.InnerException);
                Assert.Equal(manager.Current.Id, rejected.TransactionId);
            }

            var diagnostics = new ExceptionDiagnosticDocument();
            ExceptionDiagnosticProjection.Capture(diagnostics, outbox);
            ExceptionDiagnosticProjection.Capture(diagnostics, flush);
            Assert.True(diagnostics.Contains(original));
            var cause = Assert.Single(diagnostics.Exceptions, entry => entry.Type == original.GetType().FullName);
            Assert.Contains(cause.StackTrace, frame => frame.Contains(nameof(QueryHandlerReadTransaction), StringComparison.Ordinal));
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        Assert.Same(first, committed.FirstFailure);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
        {
            Assert.Empty(await db.Entries.ToListAsync(ct));
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, token);
        await using var nextScope = app.CreateScope(token);
        Assert.Null(nextScope.Resolve<IExecutionTransaction>().FirstFailure);
    }

    [Fact]
    public async Task Abort_WhenBusinessFailureIsSupplied_ShouldStopImmediatelyAndSnapshotItsReason()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var scope = app.CreateScope(token);
        var manager = scope.Resolve<IUnitOfWorkManager>();
        var afterAbort = false;
        var result = Result.Failure("Required child declined.", ResultStatus.Conflict);
        var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(async () =>
        {
            scope.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "must roll back" });
            await manager.Current!.FlushAsync(token);
            await AbortRequiredChildAsync();
            afterAbort = true;
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token));
        result.Message = "changed after abort";
        result.Status = ResultStatus.Ok;
        Assert.False(afterAbort);
        Assert.Null(rejected.InnerException);
        Assert.Equal(ResultStatus.Conflict, rejected.FirstFailure.ResultStatus);
        Assert.Equal("Required child declined.", rejected.FirstFailure.Reason);
        Assert.Equal("RequiredChild", rejected.FirstFailure.Operation);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Empty(await db.Entries.ToListAsync(ct)), token);

        Task AbortRequiredChildAsync()
        {
            manager.Current!.Abort(ExecutionTransactionFailure.FromResult("Business", result) with { Operation = "RequiredChild" });
            return Task.CompletedTask;
        }
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
            Assert.Equal(ResultStatus.NotFound, (await api.ReadTransaction(new(ResultStatus.NotFound), token)).Status);
            var independent = await api.WithTransaction(ApiTransaction.Independent)
                .ReadTransaction(new(Fault: QueryFault.Exception), token);
            Assert.Equal(ResultStatus.InternalError, independent.Status);
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
