using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Modules;
using Monica.Repository.Bulk.Abstractions;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Services;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Snapshots;
using Xunit;

namespace Test.Monica.Repository.Bulk;

public sealed class BulkSnapshotTests
{
    [Fact]
    public async Task Execute_WhenSeveralNativeWritesShareTransaction_ShouldAdvanceOnceAndRollBackLaterFailure()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new SnapshotScenarioFactory(configureRepository: repository =>
            repository.UseBulkInsert<SnapshotTestDbContext, SqliteNativeInsertProvider>()).CreateAsync(cancellationToken: token);
        await app.Services.GetRequiredService<RepositorySnapshotAdministration<SnapshotTestDbContext>>().InitializeAsync(token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            var writer = scope.Resolve<IBulkWriter>();
            await writer.Insert(db, new[] { new MaterialRow { Name = "first" } }).ExecuteAsync(token);
            await writer.Insert(db, new[] { new MaterialRow { Name = "second" } }).ExecuteAsync(token);
            db.Points.Add(new() { Value = "tracked" });
            await db.SaveChangesAsync(token);
        }, cancellationToken: token);
        var committed = await ReadAsync(app, token);
        Assert.Equal(1, committed.Version.Revision);
        Assert.Equal(new[] { "first", "second" }, committed.Value.Names);

        await Assert.ThrowsAsync<ApplicationException>(() => app.ExecuteAsync(async scope =>
        {
            await scope.Resolve<IBulkWriter>().Insert(scope.Resolve<SnapshotTestDbContext>(),
                new[] { new MaterialRow { Name = "rollback" } }).ExecuteAsync(token);
            throw new ApplicationException("later shard failed");
        }, cancellationToken: token));
        var after = await ReadAsync(app, token);
        Assert.Equal(committed.Version, after.Version);
        Assert.Equal(committed.Value.Names, after.Value.Names);
        await app.VerifyAsync<SnapshotTestDbContext>(async (db, ct) => Assert.Equal(2, await db.Rows.CountAsync(ct)), token);
    }

    private static async Task<SnapshotRead<Catalog>> ReadAsync(MonicaTestApplication app, CancellationToken token)
    {
        await using var scope = app.CreateScope(token);
        return await scope.Resolve<IRepositorySnapshot<Catalog>>().ReadFreshAsync(token);
    }
}
