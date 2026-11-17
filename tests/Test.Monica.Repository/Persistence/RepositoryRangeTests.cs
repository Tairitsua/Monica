using Microsoft.EntityFrameworkCore;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Abstractions;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.Persistence;

public sealed class RepositoryRangeTests
{
    [Fact]
    public async Task AddAndRemoveRange_WhenAggregatesAreAudited_ShouldRetainNormalPoliciesAndEntityProjections()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: token);
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().AddRange(
                [new() { Id = new Guid("019a8c82-1d00-7000-8000-000000000065"), Title = "first" }, new() { Id = new Guid("019a8c82-1d00-7000-8000-000000000066"), Title = "second" }]);
            return Task.CompletedTask;
        }, cancellationToken: token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var rows = await db.SoftDeleteRows.ToArrayAsync(ct);
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => Assert.NotEqual(default, row.CreationTime));
            Assert.Equal(2, await db.Set<OutboxMessage>().CountAsync(ct));
        }, token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            scope.Resolve<IRepository<SoftDeleteAuditRow>>().RemoveRange(await db.SoftDeleteRows.ToArrayAsync(token));
        }, cancellationToken: token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Empty(await db.SoftDeleteRows.ToArrayAsync(ct));
            var rows = await db.SoftDeleteRows.IgnoreQueryFilters().ToArrayAsync(ct);
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => { Assert.True(row.IsDeleted); Assert.NotNull(row.DeletionTime); });
            Assert.Equal(4, await db.Set<OutboxMessage>().CountAsync(ct));
        }, token);
    }
}
