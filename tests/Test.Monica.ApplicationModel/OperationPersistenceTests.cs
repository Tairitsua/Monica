using System.Reflection;
using ApplicationModel.Domain;
using ApplicationModel.Role;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Models;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationModel;

public sealed class OperationPersistenceTests
{
    [Theory]
    [InlineData(false, false, 1, 0)]
    [InlineData(true, false, 0, 1)]
    [InlineData(false, true, 0, 0)]
    public async Task Transaction_SelectsOperationDomainAndHonorsExplicitContextAndRollback(bool history, bool fail, int currentRows, int historyRows)
    {
        await using var app = await new PersistenceFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using (var scope = app.CreateScope(TestContext.Current.CancellationToken))
        {
            var mediator = scope.Resolve<IMediator>();
            if (history) Assert.Equal(1, await mediator.Send(new StoreHistory(), TestContext.Current.CancellationToken));
            else if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new StoreOrder(true), TestContext.Current.CancellationToken));
            else Assert.Equal(1, await mediator.Send(new StoreOrder(false), TestContext.Current.CancellationToken));
        }
        await app.VerifyAsync<OrderingContext>(async (db, ct) => Assert.Equal(currentRows, await db.Orders.CountAsync(ct)), TestContext.Current.CancellationToken);
        await app.VerifyAsync<OrderingHistoryContext>(async (db, ct) => Assert.Equal(historyRows, await db.Orders.CountAsync(ct)), TestContext.Current.CancellationToken);
    }

    private sealed class PersistenceFactory : MonicaTestApplicationFactory<StoreOrder>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(StoreOrder).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryQuery).Assembly];
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = "worker");
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = typeof(PersistenceManifest);
                options.Applications = new Dictionary<string, ApplicationPlacement>
                {
                    ["api"] = new() { Roles = ["ordering.api"] },
                    ["worker"] = new() { Roles = ["ordering.worker"] }
                };
            });
            builder.AddMediator();
            builder.AddRepository()
                .AddRepositoryDbContext<OrderingContext>((_, db) => db.UseSqlite("Data Source=unused"))
                .AddRepositoryDbContext<OrderingHistoryContext>((_, db) => db.UseSqlite("Data Source=unused"));
            builder.AddUnitOfWork(options => options.ForDomain(DomainId.Ordering, typeof(OrderingContext)));
        }
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.UseTestDatabase<OrderingContext>();
            services.UseTestDatabase<OrderingHistoryContext>();
        }
    }
}

public sealed record StoreOrder(bool Fail) : IRequest<int>;
public sealed class StoreOrderHandler(OrderingContext db, IMediator mediator) : IRequestHandler<StoreOrder, int>
{
    public async Task<int> Handle(StoreOrder request, CancellationToken cancellationToken)
    {
        db.Orders.Add(new StoredOrder { Name = "current" });
        // A nested passive call joins the same operation without trying to select another default context.
        _ = await mediator.Send(new OrdinaryQuery(), cancellationToken);
        if (request.Fail) throw new InvalidOperationException("abort");
        return 1;
    }
}
public sealed record StoreHistory : IRequest<int>;
public sealed class StoreHistoryHandler(OrderingHistoryContext db) : IRequestHandler<StoreHistory, int>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = [typeof(OrderingHistoryContext)])]
    public Task<int> Handle(StoreHistory request, CancellationToken cancellationToken)
    {
        db.Orders.Add(new StoredOrder { Name = "history" });
        return Task.FromResult(1);
    }
}

public sealed class PersistenceManifest : IApplicationManifest
{
    private readonly ScenarioManifest _base = new();
    public IReadOnlyList<ApplicationDomainDefinition> Domains => _base.Domains;
    public IReadOnlyList<ApplicationRoleDefinition> Roles => _base.Roles;
    public IReadOnlyList<ApplicationOperationDefinition> Operations => [.. _base.Operations,
        Operation<StoreOrder, StoreOrderHandler>(), Operation<StoreHistory, StoreHistoryHandler>()];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    private static ApplicationOperationDefinition Operation<TRequest, THandler>() => new(typeof(TRequest),
        typeof(THandler).FullName!, typeof(THandler).Assembly.GetName().Name!, "Ordering", "ordering.api", null);
}
