using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Monica.Authority.Identity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Extensions;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Doubles;
using Test.Monica.Repository.Hosting;
using Xunit;

namespace Test.Monica.Repository.Persistence;

public sealed class PersistencePolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveChanges_WhenInputsAreDeterministic_ShouldApplyRealAuditPolicy()
    {
        var now = new DateTimeOffset(2031, 3, 5, 10, 20, 30, TimeSpan.Zero);
        await using var app = await new RepositoryScenarioFactory(configure: services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTime(now));
            services.AddSingleton<ICurrentUser>(new TestCurrentUser("actor", "Operator"));
        }).CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var row = new SoftDeleteAuditRow { Title = "creation" };
            db.Add(row);
            await db.SaveChangesAsync(Token);
            Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Equal(now.UtcDateTime, row.CreationTime);
            Assert.Equal("actor", row.CreatorId);
            var originalStamp = row.ConcurrencyStamp;
            db.Entry(row).Property(x => x.Title).CurrentValue = "first edit";
            db.Entry(row).Property(x => x.Title).IsModified = true;
            await db.SaveChangesAsync(Token);
            Assert.Equal(1, row.EntityVersion);
            Assert.NotEqual(originalStamp, row.ConcurrencyStamp);
            Assert.Equal(row.ConcurrencyStamp, db.Entry(row).Property(x => x.ConcurrencyStamp).OriginalValue);
            Assert.Equal(now.UtcDateTime, row.LastModificationTime);
            db.Entry(row).Property(x => x.Title).CurrentValue = "second edit";
            db.Entry(row).Property(x => x.Title).IsModified = true;
            await db.SaveChangesAsync(Token);
            Assert.Equal(2, row.EntityVersion);
            Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
        }, cancellationToken: Token);
    }

    [Fact]
    public async Task SaveChanges_WhenConcurrencyConflicts_ShouldPreserveExceptionAndFaultContext()
    {
        await using var app = await new RepositoryScenarioFactory(outbox: false).CreateAsync(cancellationToken: Token);
        var id = await app.SeedAsync<TestRepositoryDbContext, Guid>(async (db, ct) =>
        {
            var row = new SoftDeleteAuditRow { Title = "seed" };
            db.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, Token);
        await using var staleScope = app.CreateScope(Token);
        var stale = staleScope.Resolve<TestRepositoryDbContext>();
        var loaded = await stale.SoftDeleteRows.SingleAsync(x => x.Id == id, Token);
        await app.ExecuteAsync(async scope =>
        {
            var row = await scope.Resolve<TestRepositoryDbContext>().SoftDeleteRows.SingleAsync(x => x.Id == id, Token);
            row.Title = "winner";
        }, cancellationToken: Token);
        loaded.Title = "loser";
        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Token));
        Assert.Contains(exception.Entries, entry => ReferenceEquals(entry.Entity, loaded));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task SaveChanges_WhenAcceptAllIsFalse_ShouldRejectBeforeWriting()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await using var scope = app.CreateScope(Token);
        var db = scope.Resolve<TestRepositoryDbContext>();
        db.Add(new HardDeleteRow { Title = "not saved" });
        await Assert.ThrowsAsync<NotSupportedException>(() => db.SaveChangesAsync(false, Token));
        Assert.Throws<NotSupportedException>(() => db.SaveChanges(false));
        Assert.Equal(0, await db.HardDeleteRows.AsNoTracking().CountAsync(Token));
        await db.SaveChangesAsync(Token);
        Assert.Equal(1, await db.HardDeleteRows.AsNoTracking().CountAsync(Token));
    }

    [Fact]
    public async Task Remove_WhenSoftDeleted_ShouldDiscardEditsAndPreserveTenantFilter()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var visible = new SoftDeleteAuditRow { Title = "original", TenantId = 1 };
            db.AddRange(visible, new SoftDeleteAuditRow { Title = "other tenant", TenantId = 2 });
            await db.SaveChangesAsync(Token);
            visible.Title = "discard this";
            db.Remove(visible);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            Assert.Equal(0, await db.SoftDeleteRows.CountAsync(ct));
            var deleted = Assert.Single(await db.SoftDeleteRows.IncludeSoftDeleted().ToListAsync(ct));
            Assert.Equal("original", deleted.Title);
            Assert.True(deleted.IsDeleted);
            Assert.Equal(2, await db.SoftDeleteRows.IgnoreQueryFilters().CountAsync(ct));
        }, Token);
    }

    [Theory]
    [InlineData(CascadeTiming.Immediate)]
    [InlineData(CascadeTiming.OnSaveChanges)]
    [InlineData(CascadeTiming.Never)]
    public async Task Remove_WhenRootIsSoftDeleted_ShouldRetainOwnedData(CascadeTiming timing)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var row = new SoftDeleteAuditRow { Title = "owned", Detail = new RowDetail { Value = "retain" } };
            db.Add(row);
            await db.SaveChangesAsync(Token);
            db.ChangeTracker.CascadeDeleteTiming = timing;
            db.ChangeTracker.DeleteOrphansTiming = timing;
            row.Detail.Value = "unflushed edit";
            db.Remove(row);
        }, cancellationToken: Token);
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
        {
            var deleted = await db.SoftDeleteRows.IncludeSoftDeleted().SingleAsync(ct);
            Assert.True(deleted.IsDeleted);
            Assert.Equal("retain", deleted.Detail!.Value);
        }, Token);
    }

    [Fact]
    public async Task SaveChanges_WhenOrphanDeletionIsDisabled_ShouldPreserveCallerProtection()
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        var id = await app.SeedAsync<TestRepositoryDbContext, Guid>(async (db, ct) =>
        {
            var row = new HardDeleteRow { Title = "owner", Children = [new RequiredChildRow()] };
            db.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            var row = await db.HardDeleteRows.Include(x => x.Children).SingleAsync(x => x.Id == id, Token);
            db.ChangeTracker.DeleteOrphansTiming = CascadeTiming.Never;
            row.Children.Clear();
        }, cancellationToken: Token));

        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Single((await db.HardDeleteRows.Include(x => x.Children).SingleAsync(ct)).Children), Token);
    }

    [Theory]
    [InlineData(CascadeTiming.Never, CascadeTiming.OnSaveChanges)]
    [InlineData(CascadeTiming.OnSaveChanges, CascadeTiming.Never)]
    public async Task SaveChanges_WhenDeferredAndDisabledCascadesAreMixed_ShouldRejectBeforeWriting(
        CascadeTiming deletes, CascadeTiming orphans)
    {
        await using var app = await new RepositoryScenarioFactory().CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<NotSupportedException>(() => app.ExecuteAsync(scope =>
        {
            var db = scope.Resolve<TestRepositoryDbContext>();
            db.ChangeTracker.CascadeDeleteTiming = deletes;
            db.ChangeTracker.DeleteOrphansTiming = orphans;
            db.Add(new HardDeleteRow { Title = "not saved" });
            return Task.CompletedTask;
        }, cancellationToken: Token));
        await app.VerifyAsync<TestRepositoryDbContext>(async (db, ct) =>
            Assert.Empty(await db.HardDeleteRows.ToListAsync(ct)), Token);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
