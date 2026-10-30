using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingEfOptionsTests
{
    [Theory]
    [InlineData("scoped")]
    [InlineData("factory")]
    [InlineData("pool")]
    [InlineData("pooled-factory")]
    [InlineData("repository")]
    [InlineData("manual")]
    public async Task Query_WhenOptionsComposedThroughSupportedPaths_ShouldTraceOnceAndKeepHostsIsolated(string mode)
    {
        await using var first = await new Factory(mode).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var second = await new Factory(mode).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var firstChain = await Query(first);
        var secondChain = await Query(second);

        Assert.NotSame(firstChain, secondChain);
        Assert.NotEqual(firstChain.Root!.TraceId, secondChain.Root!.TraceId);
        Assert.Equal(4, firstChain.NodeMap.Count);
        Assert.Equal(4, secondChain.NodeMap.Count);

        async Task<ChainTraceContext> Query(MonicaTestApplication app)
        {
            await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
            var tracing = scope.Resolve<IChainTracing>();
            using var parent = tracing.BeginScope("Query", "OptionsTest");
            await using var database = mode switch
            {
                "factory" or "pooled-factory" => await scope.Resolve<IDbContextFactory<OptionsContext>>()
                    .CreateDbContextAsync(TestContext.Current.CancellationToken),
                "repository" => (DbContext)await scope.Resolve<IDbContextFactory<RepositoryContext>>()
                    .CreateDbContextAsync(TestContext.Current.CancellationToken),
                "manual" => CreateManual(scope.ServiceProvider),
                _ => scope.Resolve<OptionsContext>()
            };
            Assert.Equal(42, await Read(database));

            var chain = tracing.GetCurrentChain()!;
            var leaves = chain.Root!.Children!;
            Assert.Equal(3, leaves.Count);
            Assert.Equal(Enum.GetValues<DatabaseTracePhase>(), leaves.Select(node => node.Database!.Phase));
            foreach (var node in leaves)
            {
                Assert.NotNull(node.EndTime);
                Assert.True(node.DurationMs >= 0);
                Assert.True(node.StartOffsetMs >= 0);
                Assert.False(node.IsFailed == true);
                Assert.Equal(1, node.Database!.ExecutionCount);
                Assert.Equal(0, node.Database.PendingCount);
                Assert.Null(node.Database.PoolWaitMs);
                Assert.Null(node.Database.PhysicalConnectMs);
            }
            Assert.Equal("Closed", leaves.Single(node => node.Database!.Phase == DatabaseTracePhase.ReaderConsume).Result);
            parent.EndWithSuccess();
            return chain;
        }
    }

    [Fact]
    public async Task StreamingQuery_WhenReaderStillOpen_ShouldExposePendingConsumptionWithoutInventingOpenTime()
    {
        await using var app = await new Factory("scoped").CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var database = scope.Resolve<OptionsContext>();
        // Opening outside the business scope is intentionally not represented as a request-local wait.
        await database.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        using var parent = tracing.BeginScope("Stream", "OptionsTest");

        var reader = database.Database.SqlQueryRaw<int>("SELECT 1 AS Value UNION ALL SELECT 2 AS Value")
            .AsAsyncEnumerable().GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(1, reader.Current);
            var leaves = tracing.GetCurrentNode()!.Children!;
            Assert.Equal(2, leaves.Count);
            var consume = Assert.Single(leaves, node => node.Database!.Phase == DatabaseTracePhase.ReaderConsume);
            Assert.Equal(1, consume.Database!.PendingCount);
            Assert.Equal(0, consume.Database.ExecutionCount);
            Assert.Null(consume.DurationMs);
            Assert.Null(consume.EndTime);
        }
        finally { await reader.DisposeAsync(); }

        var closed = Assert.Single(tracing.GetCurrentNode()!.Children!, node => node.Database!.Phase == DatabaseTracePhase.ReaderConsume);
        Assert.Equal(0, closed.Database!.PendingCount);
        Assert.Equal(1, closed.Database.ExecutionCount);
        Assert.Equal("Closed", closed.Result);
        Assert.False(closed.IsFailed == true);
        Assert.NotNull(closed.DurationMs);
        await database.Database.CloseConnectionAsync();
    }

    private static Task<int> Read(DbContext database) => database.Database.SqlQueryRaw<int>("SELECT 42 AS Value")
        .SingleAsync(TestContext.Current.CancellationToken);

    private static OptionsContext CreateManual(IServiceProvider services)
    {
        var options = new DbContextOptionsBuilder<OptionsContext>().UseSqlite("Data Source=:memory:");
        options.UseChainTracing(services).UseChainTracing(services);
        return new OptionsContext(options.Options);
    }

    private sealed class Factory(string mode) : MonicaTestApplicationFactory<ModuleChainTracing>
    {
        protected override void ConfigureMonica(IMonicaBuilder monica)
        {
            monica.AddChainTracing().UseEntityFrameworkCoreTracing().UseEntityFrameworkCoreTracing();
            if (mode == "repository")
                monica.AddRepository().AddRepositoryDbContext<RepositoryContext>((_, options) =>
                    options.UseSqlite("Data Source=:memory:"));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            switch (mode)
            {
                case "scoped":
                    // Explicit attachment plus automatic attachment must remain idempotent.
                    services.AddDbContext<OptionsContext>((provider, options) => options
                        .UseSqlite("Data Source=:memory:").UseChainTracing(provider));
                    break;
                case "factory": services.AddDbContextFactory<OptionsContext>(Configure); break;
                case "pool": services.AddDbContextPool<OptionsContext>(Configure, poolSize: 2); break;
                case "pooled-factory": services.AddPooledDbContextFactory<OptionsContext>(Configure, poolSize: 2); break;
            }
        }

        private static void Configure(DbContextOptionsBuilder options) => options.UseSqlite("Data Source=:memory:");
    }

    public sealed class OptionsContext(DbContextOptions<OptionsContext> options) : DbContext(options);
    public sealed class RepositoryContext(DbContextOptions<RepositoryContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<RepositoryContext>(options, services);
}
