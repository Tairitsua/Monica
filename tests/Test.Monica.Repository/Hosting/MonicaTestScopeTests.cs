using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Persistence.Abstractions;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.Hosting;

public sealed class MonicaTestScopeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExecuteAsync_WhenArrangedSeparately_ShouldUseFreshContextsAndTheSameDatabase()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        TestRepositoryDbContext? arrange = null;
        TestRepositoryDbContext? act = null;
        var id = await app.SeedAsync<TestRepositoryDbContext, Guid>(async (db, ct) =>
        {
            arrange = db;
            var row = new SoftDeleteAuditRow { Title = "before" };
            db.AddRange(row, new HardDeleteRow { Title = "other type" });
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, Token);

        await app.ExecuteAsync(async scope =>
        {
            act = scope.Resolve<TestRepositoryDbContext>();
            Assert.NotSame(arrange, act);
            Assert.Same(act, await scope.Resolve<IDbContextProvider<TestRepositoryDbContext>>().GetDbContextAsync());
            var repository = scope.Resolve<IRepository<SoftDeleteAuditRow, Guid>>();
            Assert.Same(act, scope.Resolve<IEfEntityStore<SoftDeleteAuditRow>>().Context);
            var first = await repository.GetAsync(id, Token);
            first.Title = "middle";
            var second = await repository.GetAsync(id, Token);
            Assert.Same(first, second);
            second.Title = "after";
        }, cancellationToken: Token);

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.NotSame(act, db);
            Assert.Equal("after", (await db.SoftDeleteRows.SingleAsync(ct)).Title);
            Assert.Equal(1, await db.HardDeleteRows.CountAsync(ct));
        }, Token);
    }

    [Fact]
    public async Task CreateAsync_WhenHostsAreIndependent_ShouldIsolateData()
    {
        var factory = new RepositoryScenarioFactory();
        await using var first = await factory.CreateAsync(cancellationToken: Token);
        await using var second = await factory.CreateAsync(cancellationToken: Token);
        await first.ExecuteAsync(scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().Add(new() { Title = "only first" });
            return Task.CompletedTask;
        }, cancellationToken: Token);
        await second.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Empty(await db.SoftDeleteRows.ToListAsync(ct)), Token);
    }

    [Fact]
    public void UseTestDatabase_WhenContextIsMissing_ShouldRequireProductionRegistration()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.UseTestDatabase<TestRepositoryDbContext>());
    }
}
