using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.EfCore.DbContext;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.EntityFrameworkCore.Extensions;
using Monica.EntityFrameworkCore.Models;
using Monica.EventBus.Kafka.Providers.EfCore;
using Monica.JobScheduler.EfCore;
using Monica.Modules;
using Monica.Repository.Inbox.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.EntityFrameworkCore;

public sealed class InfrastructureContextLayoutTests
{
    [Theory]
    [InlineData("postgres", "infra")]
    [InlineData("gauss", "infra")]
    [InlineData("sqlite", null)]
    public async Task Model_WhenConfiguredThroughRepository_ShouldApplyToEveryInfrastructureContext(string provider, string? schema)
    {
        var factory = new LayoutFactory(provider);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        Assert.All(scope.Resolve<ConfigurationDbContext>().Model.GetEntityTypes(), e => Assert.Equal(schema, e.GetSchema()));
        Assert.All(scope.Resolve<JobSchedulerDbContext>().Model.GetEntityTypes(), e => Assert.Equal(schema, e.GetSchema()));
        Assert.All(scope.Resolve<KafkaConsoleDbContext>().Model.GetEntityTypes(), e => Assert.Equal(schema, e.GetSchema()));
        var repository = scope.Resolve<LayoutRepositoryContext>();
        Assert.Equal(schema, repository.Model.FindEntityType(typeof(OutboxMessage))!.GetSchema());
        Assert.Equal(schema, repository.Model.FindEntityType(typeof(InboxReceipt))!.GetSchema());
        Assert.Null(repository.Model.FindEntityType(typeof(BusinessRow))!.GetSchema());
        Assert.False(scope.Resolve<ConfigurationDbContext>().GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.IsSensitiveDataLoggingEnabled);
    }

    [Fact]
    public async Task Model_WhenMigrationOwnershipAndShardsDiffer_ShouldKeepAllModelVariants()
    {
        foreach (var excluded in new[] { false, true })
        {
            await using var app = await new LayoutFactory("postgres", excluded).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
            await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
            var factory = scope.Resolve<IDbContextFactory<LayoutRepositoryContext>>();
            await using var first = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await using var second = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            second.Shard = "B";
            Assert.Equal("Business_A", first.Model.FindEntityType(typeof(BusinessRow))!.GetTableName());
            Assert.Equal("Business_B", second.Model.FindEntityType(typeof(BusinessRow))!.GetTableName());
            var designModel = first.GetService<IDesignTimeModel>().Model;
            Assert.Equal(excluded, designModel.FindEntityType(typeof(OutboxMessage))!.IsTableExcludedFromMigrations());
            Assert.Equal(excluded, designModel.FindEntityType(typeof(InboxReceipt))!.IsTableExcludedFromMigrations());
            Assert.Equal(excluded, designModel.GetEntityTypes().Single(e => e.GetTableName() == "MonicaLeases").IsTableExcludedFromMigrations());
            var schedulerModel = scope.Resolve<JobSchedulerDbContext>().GetService<IDesignTimeModel>().Model;
            Assert.Equal(excluded, schedulerModel.GetEntityTypes().Single(e => e.GetTableName() == "MonicaLeases").IsTableExcludedFromMigrations());
        }
    }

    private sealed class LayoutFactory(string provider, bool exclude = false) : MonicaTestApplicationFactory<LayoutFactory>
    {
        protected override void ConfigureMonica(IMonicaBuilder monica)
        {
            monica.AddEventBus().UseNoOpDistributedEventBus();
            monica.AddRepository()
                .AddRepositoryDbContext<ConfigurationDbContext>(Configure)
                .AddRepositoryDbContext<JobSchedulerDbContext>(Configure)
                .AddRepositoryDbContext<KafkaConsoleDbContext>(Configure)
                .AddRepositoryDbContext<LayoutRepositoryContext>(Configure)
                .AddOutbox<LayoutRepositoryContext>(o =>
                {
                    o.EnableWorker = false;
                    o.ExcludeFromMigrations = exclude;
                    o.LeaseTable.ExcludeFromMigrations = exclude;
                })
                .AddInbox<LayoutRepositoryContext>(o => o.ExcludeFromMigrations = exclude);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.Configure<ModuleJobSchedulerEfCoreOption>(o => o.LeaseTable.ExcludeFromMigrations = exclude);
        }

        private void Configure(IServiceProvider _, DbContextOptionsBuilder options)
        {
            switch (provider)
            {
                case "postgres": options.UseNpgsql("Host=localhost;Database=unused"); break;
                case "gauss": options.UseGaussDB("Host=localhost;Database=unused"); break;
                default: options.UseSqlite("Data Source=:memory:"); break;
            }
            options.UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra"));
        }
    }

    public sealed class LayoutRepositoryContext(DbContextOptions<LayoutRepositoryContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<LayoutRepositoryContext>(options, services)
    {
        public string Shard { get; set; } = "A";
        protected override object ModelCacheKey => (base.ModelCacheKey, Shard);
        protected override void OnModelCreatingExtend(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BusinessRow>().ToTable($"Business_{Shard}");
            base.OnModelCreatingExtend(modelBuilder);
        }
    }

    public sealed class BusinessRow { public int Id { get; set; } }
}
