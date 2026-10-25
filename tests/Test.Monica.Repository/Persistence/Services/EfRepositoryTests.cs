using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Monica.WebApi.AutoControllers.Services;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.Persistence.Services;

public sealed class EfRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly RepositoryScenarioFactory _factory = new(outbox: false, configure: services =>
    {
        services.AddScoped<EfRepository<TestRepositoryDbContext, HardDeleteRow, long>>();
        services.AddScoped<DetailsRepository>();
        services.AddScoped<DetailsCrudService>();
    });

    [Fact]
    public async Task Details_WhenGraphIsNotConfigured_ShouldKeepReadsDetached()
    {
        await using var app = await _factory.CreateAsync(cancellationToken: Token);
        var id = await SeedRowsAsync(app);

        await app.ExecuteAsync(async scope =>
        {
            var repository = scope.Resolve<EfRepository<TestRepositoryDbContext, HardDeleteRow, long>>();
            var row = await repository.Details.SingleAsync(row => row.Id == id, Token);

            Assert.Empty(row.Children);
            Assert.Empty(scope.Resolve<TestRepositoryDbContext>().ChangeTracker.Entries());
            row.Title = "detached edit";
        }, cancellationToken: Token);

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Equal("visible", (await db.HardDeleteRows.SingleAsync(row => row.Id == id, ct)).Title), Token);
    }

    [Fact]
    public async Task Details_WhenGraphIsConfigured_ShouldRetainQueryFiltersAndLeavePlainQueriesUnchanged()
    {
        await using var app = await _factory.CreateAsync(cancellationToken: Token);
        var id = await SeedRowsAsync(app);
        await using var scope = app.CreateScope(Token);
        var repository = scope.Resolve<DetailsRepository>();

        var plain = await repository.Query.SingleAsync(Token);
        var details = await repository.Details.SingleAsync(Token);

        Assert.Equal(id, plain.Id);
        Assert.Empty(plain.Children);
        Assert.Equal(id, details.Id);
        Assert.Equal(2, details.Children.Count);
        Assert.Empty(scope.Resolve<TestRepositoryDbContext>().ChangeTracker.Entries());
    }

    [Fact]
    public async Task ApplyDetails_WhenCrudRequestsGraph_ShouldPreserveTrackedIdentityAndOperationCommit()
    {
        await using var app = await _factory.CreateAsync(cancellationToken: Token);
        var id = await SeedRowsAsync(app);

        await app.ExecuteAsync(async scope =>
        {
            var repository = scope.Resolve<DetailsRepository>();
            var db = scope.Resolve<TestRepositoryDbContext>();
            var detached = await scope.Resolve<DetailsCrudService>().ReadAsync(Token);

            Assert.Equal(id, detached.Id);
            Assert.Equal(2, detached.Children.Count);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Same(db, repository.Context);

            var tracked = await repository.GetAsync(id, Token);
            Assert.NotSame(detached, tracked);
            Assert.Same(tracked, await repository.FindAsync(id, Token));
            Assert.Empty(tracked.Children);

            IRepositoryDetailsConfigurator<HardDeleteRow> configurator = repository;
            var loaded = await configurator.ApplyDetails(db.HardDeleteRows.AsTracking().Where(row => row.Id == id))
                .SingleAsync(Token);
            Assert.Same(tracked, loaded);
            Assert.Equal(2, loaded.Children.Count);
            loaded.Title = "committed edit";
        }, cancellationToken: Token);

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var row = await db.HardDeleteRows.Include(row => row.Children).SingleAsync(row => row.Id == id, ct);
            Assert.Equal("committed edit", row.Title);
            Assert.Equal(2, row.Children.Count);
        }, Token);
    }

    private static Task<long> SeedRowsAsync(MonicaTestApplication app)
        => app.SeedAsync<TestRepositoryDbContext, long>(async (db, ct) =>
        {
            var row = new HardDeleteRow
            {
                Title = "visible",
                Children = [new RequiredChildRow(), new RequiredChildRow()]
            };
            db.AddRange(row, new HardDeleteRow { Title = "hidden" });
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, Token);

    private sealed class DetailsRepository(TestRepositoryDbContext context)
        : EfRepository<TestRepositoryDbContext, HardDeleteRow, long>(context)
    {
        public override IQueryable<HardDeleteRow> Query => base.Query.Where(row => row.Title != "hidden");

        public override IQueryable<HardDeleteRow> ApplyDetails(IQueryable<HardDeleteRow> query)
            => query.Include(row => row.Children);
    }

    private sealed class DetailsCrudService(DetailsRepository repository)
        : AbstractKeyCrudApplicationService<HardDeleteRow, HardDeleteRow, HardDeleteRow, long, object, object, object>(repository)
    {
        protected override bool WithDetail() => true;

        public async Task<HardDeleteRow> ReadAsync(CancellationToken cancellationToken)
            => await (await CreateFilteredQueryAsync(new object())).SingleAsync(cancellationToken);
    }
}
