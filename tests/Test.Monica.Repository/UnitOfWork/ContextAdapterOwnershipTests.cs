using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.Modularity.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Hosting;
using Test.Monica.Repository.Persistence;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class ContextAdapterOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalSave_WhenEnlisted_ShouldBelongToTheLogicalOperation(bool fail)
    {
        var observer = new SaveTransactionObserver();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<OwnerContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var owner = scope.ServiceProvider.GetRequiredService<OwnerContext>();
            await using var physical = CreatePhysical(scope.ServiceProvider, connection, owner, observer);
            var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var execute = () => manager.RunAsync(async () =>
            {
                await physical.Database.UseTransactionAsync(owner.Database.CurrentTransaction!.GetDbTransaction(), TestContext.Current.CancellationToken);
                physical.Add(new GeneratedKeyRow { Title = "physical" });
                await physical.SaveChangesAsync(TestContext.Current.CancellationToken);
                Assert.True(physical.Database.AutoSavepointsEnabled);
                if (fail) throw new ApplicationException("rollback");
            }, cancellationToken: TestContext.Current.CancellationToken);
            if (fail) await Assert.ThrowsAsync<ApplicationException>(execute);
            else await execute();
            await Assert.ThrowsAsync<InvalidOperationException>(() => physical.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, observer.Savepoints);
        Assert.Equal(0, observer.Releases);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.Equal(fail ? 0 : 1, await verification.ServiceProvider.GetRequiredService<OwnerContext>()
            .Rows.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalAccess_WhenNotEnlisted_ShouldFailBeforeAQueryOrSave(bool shareConnection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var other = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = CreateHost(connection);
        await using (var setup = host.Services.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<OwnerContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using var scope = host.Services.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<OwnerContext>();
        await using var physical = CreatePhysical(scope.ServiceProvider, shareConnection ? connection : other, owner);
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => manager.RunAsync(async () =>
        {
            owner.Rows.Add(new GeneratedKeyRow { Title = "must roll back" });
            await manager.Current!.FlushAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(() => physical.Rows.CountAsync(TestContext.Current.CancellationToken));
            physical.Add(new GeneratedKeyRow { Title = "rejected" });
            var rejected = await Assert.ThrowsAsync<TransactionRollbackOnlyException>(() => physical.SaveChangesAsync(TestContext.Current.CancellationToken));
            Assert.IsType<NotSupportedException>(rejected.InnerException);
        }, cancellationToken: TestContext.Current.CancellationToken));
        await using var verification = host.Services.CreateAsyncScope();
        Assert.Empty(await verification.ServiceProvider.GetRequiredService<OwnerContext>().Rows
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    private static IHost CreateHost(SqliteConnection connection)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMonicaTestSeams();
        builder.AddMonica(monica => monica.AddRepository().AddRepositoryDbContext<OwnerContext>((_, db) => db.UseSqlite(connection)));
        return builder.Build();
    }

    private static PhysicalContext CreatePhysical(IServiceProvider services, SqliteConnection connection, DbContext owner,
        SaveTransactionObserver? observer = null)
    {
        var options = new DbContextOptionsBuilder<PhysicalContext>().UseSqlite(connection);
        if (observer is not null) options.AddInterceptors(observer);
        return new(options.Options, services.GetRequiredService<ICachedServiceProvider>(), owner);
    }

    public sealed class OwnerContext(DbContextOptions<OwnerContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<OwnerContext>(options, services)
    {
        public DbSet<GeneratedKeyRow> Rows => Set<GeneratedKeyRow>();
    }

    public sealed class PhysicalContext(DbContextOptions<PhysicalContext> options, ICachedServiceProvider services, DbContext owner)
        : RepositoryDbContext<PhysicalContext>(options, services), IRepositoryContextAdapter
    {
        public DbSet<GeneratedKeyRow> Rows => Set<GeneratedKeyRow>();
        public bool CoordinatesSave => false;
        public DbContext TransactionOwner => owner;
    }
}
