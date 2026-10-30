using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Authority.Identity.Services;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Services;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Testing.Hosting;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Contracts;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;
using Xunit;

namespace Test.Monica.Application.Invocation;

public sealed class LocalApiOperationScopeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shared_WhenNonTransactionalJobCallsCommands_ShouldOwnEachScopeAndCommitSeparately(
        bool application, bool failSecond)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        using var identity = app.Services.GetRequiredService<CurrentPrincipalContext>().Use(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "job-caller")], "test")));
        var callerScope = caller.Resolve<TransactionScope>();
        var manager = caller.Resolve<IUnitOfWorkManager>();
        var ledger = app.Services.GetRequiredService<TransactionLedger>();
        var commands = caller.Resolve<ITestingCommandApi>();
        await RunJobAsync(caller, async ct =>
        {
            var context = app.Services.GetRequiredService<ExecutionScopeContext>();
            var parent = context.Services;
            var first = await commands.TransactionChild(new(), ct);
            Assert.Equal(ResStatus.Ok, first.Status);
            Assert.Equal("job-caller", first.Data!.User);
            Assert.Equal(first.Data.ScopeId, first.Data.GrandchildScopeId);
            Assert.NotEqual(callerScope.Id, first.Data.ScopeId);
            Assert.True(Assert.Single(ledger.ChildExecutions).Scope.IsDisposed);
            Assert.Same(parent, context.Services);
            Assert.Null(manager.Current);

            var second = await commands.TransactionChild(new(Fail: failSecond), ct);
            Assert.Equal(failSecond ? ResStatus.BadRequest : ResStatus.Ok, second.Status);
            Assert.Equal(2, ledger.ChildExecutions.Count);
            Assert.NotEqual(ledger.ChildExecutions[0].Scope.Id, ledger.ChildExecutions[1].Scope.Id);
            Assert.All(ledger.ChildExecutions, execution =>
            {
                Assert.True(execution.Scope.IsDisposed);
                Assert.Equal(ct, execution.Token);
            });
            Assert.Equal("job-caller", ledger.ChildUser);
            Assert.False(callerScope.IsDisposed);
            Assert.Same(parent, context.Services);
            Assert.False(caller.Resolve<IExecutionTransaction>().IsOperationActive);
        });
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
            Assert.Equal(failSecond ? 1 : 2, await db.Entries.CountAsync(ct)), token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_WhenNonTransactionalJobCallsQueries_ShouldUseFreshScopesWithoutTransactions(bool application)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        var queries = caller.Resolve<ITestingQueryApi>();
        await RunJobAsync(caller, async ct =>
        {
            var first = await queries.TransactionScope(new(), ct);
            var second = await queries.TransactionScope(new(), ct);
            Assert.Equal(ResStatus.Ok, first.Status);
            Assert.Equal(ResStatus.Ok, second.Status);
            Assert.NotEqual(first.Data, second.Data);
            Assert.NotEqual(caller.Resolve<TransactionScope>().Id, first.Data);
            var read = await queries.ReadTransaction(new(), ct);
            Assert.Equal(ResStatus.Ok, read.Status);
            Assert.False(read.Data!.HasTransaction);
            Assert.Null(caller.Resolve<IUnitOfWorkManager>().Current);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_WhenNestedCallThrowsWithoutParentTransaction_ShouldPropagateAndAllowNextOperation(bool application)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        await RunJobAsync(caller, async ct =>
        {
            var context = app.Services.GetRequiredService<ExecutionScopeContext>();
            var parent = context.Services;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => caller.Resolve<ITestingQueryApi>()
                .ReadTransaction(new(Fault: QueryFault.Exception), ct));
            Assert.Equal("Query execution failed.", exception.Message);
            Assert.Same(parent, context.Services);
            var next = await caller.Resolve<ITestingCommandApi>().TransactionChild(new(), ct);
            Assert.Equal(ResStatus.Ok, next.Status);
        });
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Single(await db.Entries.ToListAsync(ct)), token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_WhenNestedCallCancelsWithoutParentTransaction_ShouldRestoreCallerAndAllowNextOperation(bool application)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        app.Services.GetRequiredService<TransactionLedger>().ChildCancellation = cancellation;
        await RunJobAsync(caller, async ct =>
        {
            var context = app.Services.GetRequiredService<ExecutionScopeContext>();
            var parent = context.Services;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.Resolve<ITestingQueryApi>()
                .TransactionScope(new(Cancel: true), cancellation.Token));
            Assert.Same(parent, context.Services);
            Assert.Null(caller.Resolve<IExecutionTransaction>().FirstFailure);
            var next = await caller.Resolve<ITestingCommandApi>().TransactionChild(new(), ct);
            Assert.Equal(ResStatus.Ok, next.Status);
        });
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Single(await db.Entries.ToListAsync(ct)), token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shared_WhenLogicalUnitOfWorkHasNoDatabaseYet_ShouldJoinAndRespectItsOutcome(bool application, bool failParent)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        var boundary = caller.Resolve<IExecutionTransaction>();
        var result = await caller.Resolve<IUnitOfWorkManager>().RunAsync(async () =>
        {
            Assert.True(boundary.IsOperationActive);
            Assert.False(boundary.IsActive);
            var child = await caller.Resolve<ITestingCommandApi>().TransactionChild(new(), token);
            Assert.Equal(ResStatus.Ok, child.Status);
            Assert.Equal(caller.Resolve<TransactionScope>().Id, child.Data!.ScopeId);
            Assert.Equal(child.Data.ScopeId, child.Data.GrandchildScopeId);
            Assert.True(boundary.IsActive);
            Assert.False(Assert.Single(app.Services.GetRequiredService<TransactionLedger>().ChildExecutions).Scope.IsDisposed);
            return failParent ? Res.Fail("Parent rejected after enlistment.") : Res.Ok();
        }, new UnitOfWorkScopeOptions([]), token);
        Assert.Equal(failParent ? ResStatus.BadRequest : ResStatus.Ok, result.Status);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
            Assert.Equal(failParent ? 0 : 1, await db.Entries.CountAsync(ct)), token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_WhenCallFailsBeforeDatabaseEnlistment_ShouldKeepLogicalUnitOfWorkRollbackOnly(bool application)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await cancellation.CancelAsync();
        var boundary = caller.Resolve<IExecutionTransaction>();
        var failure = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => caller.Resolve<IUnitOfWorkManager>()
            .RunAsync(async () =>
            {
                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.Resolve<ITestingCommandApi>()
                    .TransactionChild(new(), cancellation.Token));
                Assert.Same(canceled, boundary.FirstFailure!.Exception);
                Assert.True(boundary.IsOperationActive);
                Assert.False(boundary.IsActive);
                Assert.Same(caller.ServiceProvider,
                    app.Services.GetRequiredService<ExecutionScopeContext>().GetActiveUnitOfWorkServices());
            }, new UnitOfWorkScopeOptions([]), token));
        Assert.Equal("ApiInvocation", failure.FirstFailure.Source);
        Assert.Equal(0, app.Services.GetRequiredService<TransactionLedger>().ChildCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_WhenCallerReusesCompletedUnitOfWork_ShouldRejectBeforeStartingAnotherOperation(bool application)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new OperationScopeFactory(application).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        await caller.Resolve<IUnitOfWorkManager>().RunAsync(() => Task.CompletedTask, new UnitOfWorkScopeOptions([]), token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => caller.Resolve<ITestingCommandApi>()
            .TransactionChild(new(), token));
        Assert.Contains("operation scope has ended", exception.Message);
        Assert.Equal(0, app.Services.GetRequiredService<TransactionLedger>().ChildCalls);
    }

    private static Task RunJobAsync(MonicaTestScope caller, Func<CancellationToken, Task> work)
    {
        caller.Resolve<ScriptedRecurringJob>().Work = work;
        return new JobExecutor().ExecuteRecurringJobAsync(new JobExecutionContext
        {
            InstanceId = "scope-test",
            ServiceProvider = caller.ServiceProvider,
            JobType = typeof(ScriptedRecurringJob),
            CancellationToken = caller.CancellationToken
        });
    }

    private sealed class OperationScopeFactory(bool application) : TransactionFactory(application: application)
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddScoped<ScriptedRecurringJob>();
        }
    }

    private sealed class ScriptedRecurringJob : IRecurringJob
    {
        public required Func<CancellationToken, Task> Work { private get; set; }
        public Task ExecuteAsync(CancellationToken cancellationToken) => Work(cancellationToken);
    }
}
