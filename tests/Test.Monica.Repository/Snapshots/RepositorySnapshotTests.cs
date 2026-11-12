using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Services;
using Monica.Repository.UnitOfWork.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Repository.Snapshots;

public sealed class RepositorySnapshotTests
{
    [Fact]
    public async Task Read_WhenFloatingInputIsTinyOrNegativeZero_ShouldPreserveLoaderValueAndDistinctIdentity()
    {
        await using var app = await new SnapshotScenarioFactory(configure: options => options.AddProjection<ScalarDefinition>())
            .CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await using var scope = app.CreateScope(Token);
        var snapshots = scope.Resolve<IRepositorySnapshot<ScalarResult, ScalarInput>>();
        var tiny = await snapshots.ReadFreshAsync(new(1e-30), Token);
        var zero = await snapshots.ReadFreshAsync(new(0d), Token);
        var negative = await snapshots.ReadFreshAsync(new(-0d), Token);
        Assert.Equal(1e-30, tiny.Value.Value);
        Assert.NotEqual(tiny.ProjectionIdentity, zero.ProjectionIdentity);
        Assert.NotEqual(negative.ProjectionIdentity, zero.ProjectionIdentity);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(negative.Value.Value));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task InitializeAsync(MonicaTestApplication app)
    {
        await app.VerifyAsync<SnapshotTestDbContext>(async (db, ct) => await db.Database.EnsureCreatedAsync(ct), Token);
        await app.Services.GetRequiredService<RepositorySnapshotAdministration<SnapshotTestDbContext>>().InitializeAsync(Token);
    }
    private static Task WriteAsync(MonicaTestApplication app, string name)
        => app.ExecuteAsync(scope =>
        {
            scope.Resolve<SnapshotTestDbContext>().Rows.Add(new() { Name = name });
            return Task.CompletedTask;
        }, cancellationToken: Token);
    private static async Task<SnapshotRead<Catalog>> ReadAsync(MonicaTestApplication app)
    {
        await using var scope = app.CreateScope(Token);
        return await scope.Resolve<IRepositorySnapshot<Catalog>>().ReadFreshAsync(Token);
    }

    [Fact]
    public async Task Commit_WhenBatchHasMultipleFlushesAndProjections_ShouldAdvanceOneVersionWithoutOutbox()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            db.Rows.AddRange(Enumerable.Range(1, 1000).Select(id => new MaterialRow { Id = id, Name = "before" }));
            await db.SaveChangesAsync(Token);
            foreach (var row in db.Rows.Local) row.Name = "after";
            await db.SaveChangesAsync(Token);
        }, cancellationToken: Token);
        var read = await ReadAsync(app);
        Assert.Equal(1, read.Version.Revision);
        Assert.Equal(1000, read.Value.Names.Length);
        Assert.All(read.Value.Names, value => Assert.Equal("after", value));
        await app.VerifyAsync<SnapshotTestDbContext>(async (db, ct) =>
        {
            Assert.Equal(1, await db.Set<RepositorySnapshotVersion>().CountAsync(ct));
            Assert.Null(db.Model.FindEntityType(typeof(global::Monica.Repository.Outbox.Models.OutboxMessage)));
        }, Token);
    }

    [Fact]
    public async Task Commit_WhenAuthorityMissing_ShouldRollBackBusinessRows()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(app, "must roll back"));
        await app.VerifyAsync<SnapshotTestDbContext>(async (db, ct) => Assert.Equal(0, await db.Rows.CountAsync(ct)), Token);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ReadAsync(app));
    }

    [Fact]
    public async Task Commit_WhenWorkFailsAfterFlush_ShouldLeaveVersionAndCacheUnchanged()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var original = await ReadAsync(app);
        await Assert.ThrowsAsync<ApplicationException>(() => app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            db.Rows.Add(new() { Name = "rollback" });
            await db.SaveChangesAsync(Token);
            throw new ApplicationException("abort");
        }, cancellationToken: Token));
        var read = await ReadAsync(app);
        Assert.Equal(original.Version, read.Version);
        Assert.Same(original.Value, read.Value);
    }

    [Fact]
    public async Task Read_WhenDependencyDirtyBeforeOrAfterFlush_ShouldRejectSharedSnapshot()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            db.Rows.Add(new() { Name = "local" });
            var snapshots = scope.Resolve<IRepositorySnapshot<Catalog>>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.ReadFreshAsync(Token));
            await db.SaveChangesAsync(Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.ReadFreshAsync(Token));
        }, cancellationToken: Token);
        Assert.Equal("local", Assert.Single((await ReadAsync(app)).Value.Names));
    }

    [Fact]
    public async Task Commit_WhenEventsSuppressedOrBulkSqlUsed_ShouldStillAdvanceVersion()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await app.ExecuteAsync(scope =>
        {
            using var suppressed = scope.Resolve<EntityEventPublicationScope>().Suspend();
            scope.Resolve<SnapshotTestDbContext>().Rows.Add(new() { Name = "sync" });
            return Task.CompletedTask;
        }, cancellationToken: Token);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            await db.ExecuteSnapshotWriteAsync([typeof(MaterialRow)],
                ct => db.Rows.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Name, "bulk"), ct), Token);
        }, cancellationToken: Token);
        var read = await ReadAsync(app);
        Assert.Equal(2, read.Version.Revision);
        Assert.Equal("bulk", Assert.Single(read.Value.Names));
    }

    [Fact]
    public async Task Read_WhenDateDiffers_ShouldShareAuthorityButIsolateProjectionInputs()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await WriteAsync(app, "A1");
        var catalog = await ReadAsync(app);
        await using var scope = app.CreateScope(Token);
        var maps = scope.Resolve<IRepositorySnapshot<MapResult, MapInput>>();
        var first = await maps.ReadFreshAsync(new(new(2026, 10, 29)), Token);
        var second = await maps.ReadFreshAsync(new(new(2026, 10, 30)), Token);
        var repeated = await maps.ReadFreshAsync(new(new(2026, 10, 29)), Token);
        Assert.Equal(catalog.Version, first.Version);
        Assert.Equal(first.Version, second.Version);
        Assert.NotEqual(first.Value.Content, second.Value.Content);
        Assert.Same(first.Value, repeated.Value);
        Assert.Equal(3, first.ProjectionIdentity.AlgorithmVersion);
        Assert.NotEqual(first.ProjectionIdentity, second.ProjectionIdentity);
    }

    [Fact]
    public async Task Read_WhenSourceChangesDuringBuild_ShouldDiscardAndRebuild()
    {
        var probe = new ProjectionProbe { HoldFirst = true };
        await using var app = await new SnapshotScenarioFactory(probe).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var reading = ReadAsync(app);
        await probe.Started.Task.WaitAsync(Token);
        await WriteAsync(app, "new");
        probe.Release.TrySetResult();
        var read = await reading;
        Assert.Equal("new", Assert.Single(read.Value.Names));
        Assert.Equal(1, read.Version.Revision);
        Assert.Equal(2, probe.Count);
    }

    [Fact]
    public async Task Read_WhenOneWaiterCancels_ShouldPreserveSharedBuildForOtherWaiters()
    {
        var probe = new ProjectionProbe { HoldFirst = true };
        await using var app = await new SnapshotScenarioFactory(probe).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await using var firstScope = app.CreateScope(Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = firstScope.Resolve<IRepositorySnapshot<Catalog>>().ReadFreshAsync(cancel.Token);
        await probe.Started.Task.WaitAsync(Token);
        var second = ReadAsync(app);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        probe.Release.TrySetResult();
        Assert.Empty((await second).Value.Names);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task ReadBounded_WhenValidationExceedsBudget_ShouldRejectNewAndCachedValues()
    {
        var clock = new SnapshotClock();
        var interceptor = new SlowValidationInterceptor(clock);
        var probe = new ProjectionProbe();
        await using var app = await new SnapshotScenarioFactory(probe, clock, interceptor: interceptor)
            .CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await using var scope = app.CreateScope(Token);
        var snapshots = scope.Resolve<IRepositorySnapshot<Catalog>>();
        interceptor.Enabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(1), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(1), Token));

        var fresh = await snapshots.ReadFreshAsync(Token);
        Assert.Empty(fresh.Value.Names);
        Assert.Equal(TimeSpan.FromSeconds(2), fresh.ValidationAge);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task ReadBounded_WhenBudgetExpires_ShouldRevalidateWithoutNotifications()
    {
        var clock = new SnapshotClock();
        await using var app = await new SnapshotScenarioFactory(clock: clock).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var first = await ReadAsync(app);
        await WriteAsync(app, "later");
        await using var scope = app.CreateScope(Token);
        var snapshots = scope.Resolve<IRepositorySnapshot<Catalog>>();
        clock.Advance(TimeSpan.FromSeconds(1));
        var bounded = await snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Same(first.Value, bounded.Value);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("later", Assert.Single((await snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(2), Token)).Value.Names));
    }

    [Fact]
    public async Task Initialize_WhenRepeated_ShouldPreserveExistingEpochAndRevision()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await WriteAsync(app, "existing");
        var first = await ReadAsync(app);
        await InitializeAsync(app);
        Assert.Equal(first.Version, (await ReadAsync(app)).Version);
    }

    [Fact]
    public async Task Read_WhenIndependentHostsShareDatabase_ShouldObserveOtherHostsCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monica-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await using var first = await new SnapshotScenarioFactory(databasePath: path).CreateAsync(cancellationToken: Token);
            await InitializeAsync(first);
            await using var second = await new SnapshotScenarioFactory(databasePath: path).CreateAsync(cancellationToken: Token);
            var before = await ReadAsync(second);
            await WriteAsync(first, "replica-a");
            var after = await ReadAsync(second);
            Assert.NotEqual(before.Version, after.Version);
            Assert.Equal("replica-a", Assert.Single(after.Value.Names));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Commit_WhenOnlyDependentChildChanges_ShouldInvalidateAllProjections()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var before = await ReadAsync(app);
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<SnapshotTestDbContext>().Points.Add(new() { Value = "changed child" });
            return Task.CompletedTask;
        }, cancellationToken: Token);
        var after = await ReadAsync(app);
        Assert.Equal(before.Version.Revision + 1, after.Version.Revision);
        Assert.NotSame(before.Value, after.Value);
    }

    [Fact]
    public async Task Read_WhenEntryCapacityExceeded_ShouldEvictAndRebuildOnlyRequestedProjection()
    {
        var probe = new ProjectionProbe();
        await using var app = await new SnapshotScenarioFactory(probe, maximumEntries: 1).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var first = await ReadAsync(app);
        await using var scope = app.CreateScope(Token);
        await scope.Resolve<IRepositorySnapshot<MapResult, MapInput>>().ReadFreshAsync(new(new(2026, 10, 29)), Token);
        var status = app.Services.GetRequiredService<RepositorySnapshotAdministration<SnapshotTestDbContext>>().GetStatus();
        Assert.Single(status.Entries);
        Assert.Equal("map", status.Entries[0].ProjectionIdentity.Name);
        Assert.NotSame(first.Value, (await ReadAsync(app)).Value);
        Assert.Equal(2, probe.Count);
    }

    [Fact]
    public async Task ReadBounded_WhenNewConstructionFails_ShouldReturnOldValueOnlyWithinExplicitBudget()
    {
        var probe = new ProjectionProbe();
        var clock = new SnapshotClock();
        await using var app = await new SnapshotScenarioFactory(probe, clock).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var first = await ReadAsync(app);
        await WriteAsync(app, "new");
        probe.Fail = true;
        await Assert.ThrowsAsync<ApplicationException>(() => ReadAsync(app));
        clock.Advance(TimeSpan.FromSeconds(1));
        await using var scope = app.CreateScope(Token);
        var snapshots = scope.Resolve<IRepositorySnapshot<Catalog>>();
        var degraded = await snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(2), Token);
        Assert.True(degraded.IsDegraded);
        Assert.Same(first.Value, degraded.Value);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ApplicationException>(() => snapshots.ReadBoundedAsync(TimeSpan.FromSeconds(2), Token));
    }

    [Fact]
    public async Task Read_WhenAuthorityDisappears_ShouldNeverServeDegradedValue()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await ReadAsync(app);
        await app.VerifyAsync<SnapshotTestDbContext>(async (db, ct) => await db.Set<RepositorySnapshotVersion>().ExecuteDeleteAsync(ct), Token);
        await using var scope = app.CreateScope(Token);
        // Fresh observes corruption. Even an otherwise eligible bounded fallback must not hide it.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ReadAsync(app));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => scope.Resolve<IRepositorySnapshot<Catalog>>()
            .ReadBoundedAsync(TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task Read_WhenConstructionCapacityExhausted_ShouldRejectNewIdentityAndKeepExistingBuild()
    {
        var probe = new ProjectionProbe { HoldFirst = true };
        await using var app = await new SnapshotScenarioFactory(probe, configure: options => options.MaximumPendingBuilds = 1)
            .CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var pending = ReadAsync(app);
        await probe.Started.Task.WaitAsync(Token);
        await using var scope = app.CreateScope(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Resolve<IRepositorySnapshot<MapResult, MapInput>>()
            .ReadFreshAsync(new(new(2026, 10, 29)), Token));
        probe.Release.TrySetResult();
        Assert.Empty((await pending).Value.Names);
    }

    [Fact]
    public async Task Dispose_WhenConstructionPending_ShouldCancelAndDrainOwnedWork()
    {
        var probe = new ProjectionProbe { HoldFirst = true };
        var app = await new SnapshotScenarioFactory(probe).CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        var pending = ReadAsync(app);
        await probe.Started.Task.WaitAsync(Token);
        await app.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Write_WhenOutsideManagedOperation_ShouldRejectBeforePersistence()
    {
        await using var app = await new SnapshotScenarioFactory().CreateAsync(cancellationToken: Token);
        await InitializeAsync(app);
        await using (var scope = app.CreateScope(Token))
        {
            var db = scope.Resolve<SnapshotTestDbContext>();
            db.Rows.Add(new() { Name = "unmanaged" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Token));
        }
        Assert.Empty((await ReadAsync(app)).Value.Names);
    }
}
