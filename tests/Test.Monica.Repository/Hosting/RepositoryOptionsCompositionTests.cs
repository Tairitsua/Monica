using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Repository.Hosting;

public sealed class RepositoryOptionsCompositionTests
{
    [Fact]
    public async Task Options_WhenConfiguredByEf_ShouldApplyToScopedAndOwnedFactoryContexts()
    {
        await using var app = await new Factory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var scoped = scope.Resolve<OptionsContext>();
        Assert.Same(scoped, scope.Resolve<OptionsContext>());
        Assert.True(scope.Resolve<DbContextOptions<OptionsContext>>().FindExtension<CoreOptionsExtension>()!.DetailedErrorsEnabled);
        Assert.NotSame(scope.Resolve<DbContextOptions<OptionsContext>>(), scope.Resolve<DbContextOptions<OptionsContext>>());
        Assert.Same(scoped.Session.Connection, scoped.Database.GetDbConnection());
        Assert.Equal(QueryTrackingBehavior.NoTracking, scoped.ChangeTracker.QueryTrackingBehavior);

        var factory = scope.Resolve<IDbContextFactory<OptionsContext>>();
        Session firstSession;
        Session secondSession;
        await using (var first = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        await using (var second = factory.CreateDbContext())
        {
            firstSession = first.Session;
            secondSession = second.Session;
            Assert.NotSame(scoped.Session, firstSession);
            Assert.NotSame(firstSession, secondSession);
            Assert.Same(firstSession.Connection, first.Database.GetDbConnection());
            Assert.Same(secondSession.Connection, second.Database.GetDbConnection());
            Assert.Equal(QueryTrackingBehavior.NoTracking, first.ChangeTracker.QueryTrackingBehavior);
            Assert.Equal(QueryTrackingBehavior.NoTracking, second.ChangeTracker.QueryTrackingBehavior);
            Assert.Equal(42, await first.Database.SqlQueryRaw<int>("SELECT 42 AS Value")
                .SingleAsync(TestContext.Current.CancellationToken));
        }
        Assert.True(firstSession.Disposed);
        Assert.True(secondSession.Disposed);
        Assert.False(scoped.Session.Disposed);
    }

    private sealed class Factory : MonicaTestApplicationFactory<ModuleRepository>
    {
        protected override void ConfigureMonica(IMonicaBuilder monica)
            => monica.AddRepository().AddRepositoryDbContext<OptionsContext>((provider, options) =>
                options.UseSqlite(provider.GetRequiredService<Session>().Connection));

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddScoped<Session>();
            services.ConfigureDbContext<OptionsContext>(options =>
                options.EnableDetailedErrors().UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        }
    }

    public sealed class OptionsContext(DbContextOptions<OptionsContext> options, ICachedServiceProvider services, Session session)
        : RepositoryDbContext<OptionsContext>(options, services)
    {
        public Session Session { get; } = session;
    }

    public sealed class Session : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            Connection.Dispose();
        }
    }
}
