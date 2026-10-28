using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Snowflake.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class ExecutionTransactionPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenMethodRequiresIsolation_ShouldUseItOrRejectIncompatibleNestedRequirements(bool incompatible)
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new TransactionPolicyFactory().CreateAsync(cancellationToken: token);
        await using var scope = application.CreateScope(token);
        if (!incompatible)
        {
            Assert.Equal(System.Data.IsolationLevel.Serializable,
                await scope.Resolve<IMediator>().Send(new RequiredIsolation(), token));
            return;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<IUnitOfWorkManager>().RunAsync(async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<IUnitOfWorkManager>().RunAsync(
                () => throw new Xunit.Sdk.XunitException("An incompatible nested transaction must not execute."),
                new UnitOfWorkScopeOptions(IsolationLevel: System.Data.IsolationLevel.RepeatableRead), token));
        }, new UnitOfWorkScopeOptions(IsolationLevel: System.Data.IsolationLevel.Serializable), token));
    }

    private sealed record RequiredIsolation : IRequest<System.Data.IsolationLevel>;

    private sealed class RequiredIsolationHandler(TestRepositoryDbContext db, IUnitOfWorkManager manager)
        : IRequestHandler<RequiredIsolation, System.Data.IsolationLevel>
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic, IsolationLevel = System.Data.IsolationLevel.Serializable)]
        public Task<System.Data.IsolationLevel> Handle(RequiredIsolation request, CancellationToken cancellationToken)
            => manager.RunAsync(() => Task.FromResult(db.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel),
                new UnitOfWorkScopeOptions(IsolationLevel: System.Data.IsolationLevel.Serializable), cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenMethodDeclaresNone_ShouldPersistOnlyThroughEnclosingUnitOfWork(bool enclosing)
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await new TransactionPolicyFactory().CreateAsync(cancellationToken: token);
        bool sawEnclosingUnitOfWork;

        if (enclosing)
        {
            sawEnclosingUnitOfWork = await application.ExecuteAsync(
                scope => scope.Resolve<IMediator>().Send(new StageWriteRequest("enclosing"), token),
                cancellationToken: token);
        }
        else
        {
            await using var scope = application.CreateScope(token);
            sawEnclosingUnitOfWork = await scope.Resolve<IMediator>().Send(new StageWriteRequest("uncommitted"), token);
        }

        Assert.Equal(enclosing, sawEnclosingUnitOfWork);
        await application.VerifyAsync<TestRepositoryDbContext>(async (db, cancellationToken) =>
        {
            var rows = await db.HardDeleteRows.ToListAsync(cancellationToken);
            if (enclosing)
            {
                Assert.Equal("enclosing", Assert.Single(rows).Title);
            }
            else
            {
                Assert.Empty(rows);
            }
        }, token);
    }

    private sealed record StageWriteRequest(string Title) : IRequest<bool>;

    private sealed class StageWriteHandler(
        IRepository<HardDeleteRow> repository,
        IUnitOfWorkManager manager) : IRequestHandler<StageWriteRequest, bool>
    {
        [ExecutionTransaction(ExecutionTransactionMode.None)]
        public Task<bool> Handle(StageWriteRequest request, CancellationToken cancellationToken)
        {
            repository.Add(new HardDeleteRow { Title = request.Title });
            return Task.FromResult(manager.Current is not null);
        }
    }

    private sealed class TransactionPolicyFactory : MonicaTestApplicationFactory<StageWriteHandler>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddRepository()
                .AddRepositoryDbContext<TestRepositoryDbContext>((_, options) => options.UseSqlite("Data Source=unused"));
            builder.AddMediator();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<ISnowflakeIdGenerator, SequentialTestIdGenerator>();
            services.AddScoped<IRequestHandler<StageWriteRequest, bool>, StageWriteHandler>();
            services.AddScoped<IRequestHandler<RequiredIsolation, System.Data.IsolationLevel>, RequiredIsolationHandler>();
            services.UseTestDatabase<TestRepositoryDbContext>();
        }
    }
}
