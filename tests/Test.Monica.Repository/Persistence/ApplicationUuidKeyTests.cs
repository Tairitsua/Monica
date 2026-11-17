using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Monica.Modules;
using Monica.Repository.Bulk.Abstractions;
using Test.Monica.Repository.Bulk;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.Persistence;

public sealed class ApplicationUuidKeyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RepositoryScenarioFactory Factory() => new(outbox: false,
        configureRepository: repository => repository.UseBulkInsert<TestRepositoryDbContext, SqliteNativeInsertProvider>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_WhenIdentityMissing_ShouldRejectWithoutAssigningOne(bool native)
    {
        await using var app = await Factory().CreateAsync(cancellationToken: Token);
        var row = new ApplicationUuidRow { Title = "invalid" };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            Assert.Equal(ValueGenerated.Never, db.Model.FindEntityType(typeof(ApplicationUuidRow))!.FindProperty("Id")!.ValueGenerated);
            if (native) await scope.Resolve<IBulkWriter>().Insert(db, new[] { row }).ExecuteAsync(Token);
            else { db.Add(row); await db.SaveChangesAsync(Token); }
        }, cancellationToken: Token));
        Assert.Contains(nameof(ApplicationUuidRow), failure.Message);
        Assert.Equal(Guid.Empty, row.Id);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) => Assert.Equal(0, await db.UuidRows.CountAsync(ct)), Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_WhenIdentityProvided_ShouldPreserveNewAndRestoredUuids(bool native)
    {
        await using var app = await Factory().CreateAsync(cancellationToken: Token);
        var newId = Guid.CreateVersion7();
        var restoredId = Guid.Parse("16a36cc5-9855-45f5-8f70-bb9f32771873");
        var rows = new[] { new ApplicationUuidRow { Id = newId, Title = "new" }, new ApplicationUuidRow { Id = restoredId, Title = "restored" } };
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            if (native) await scope.Resolve<IBulkWriter>().Insert(db, rows).ExecuteAsync(Token);
            else { db.AddRange(rows); await db.SaveChangesAsync(Token); }
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal("new", (await db.UuidRows.SingleAsync(row => row.Id == newId, ct)).Title);
            Assert.Equal("restored", (await db.UuidRows.SingleAsync(row => row.Id == restoredId, ct)).Title);
        }, Token);
    }
}
