using System.Collections.Concurrent;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Execution;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

/// <summary>
/// Exercises real EF async interceptor composition and the business execution boundary used by jobs
/// and event handlers. A second interceptor is essential: EF aggregates their callbacks asynchronously.
/// </summary>
public sealed class ChainTracingDatabaseExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SqlWithoutBusinessScope_ShouldNotCreateAnOrphanChain(bool asynchronous, bool fail)
    {
        var factory = new TracingFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        var database = scope.Resolve<TracingDbContext>();
        var query = database.Database.SqlQueryRaw<int>(fail ? "SELECT MissingColumn AS Value" : "SELECT 42 AS Value");

        var action = async () => asynchronous
            ? await query.SingleAsync(TestContext.Current.CancellationToken)
            : query.Single();

        if (fail) await action.Should().ThrowAsync<SqliteException>();
        else (await action()).Should().Be(42);

        tracing.GetCurrentChain().Should().BeNull();
        factory.Logs.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task TaskBusinessExecution_ShouldCompleteRootAndSqlOnEveryOutcome(string outcome)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var factory = new TracingFactory(cancelOnCommand: outcome == "cancellation" ? cancellation : null);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        var database = scope.Resolve<TracingDbContext>();
        var pipeline = scope.Resolve<IExecutionPipeline>();
        ChainTraceContext? captured = null;

        var action = () => pipeline.ExecuteAsync(Descriptor(), "input", null, async () =>
        {
            captured = tracing.GetCurrentChain();
            var value = await database.Database.SqlQueryRaw<int>(outcome == "failure"
                    ? "SELECT MissingColumn AS Value" : "SELECT 42 AS Value")
                .SingleAsync(cancellation.Token);
            value.Should().Be(42);
        }, cancellation.Token);

        Exception? thrown = null;
        if (outcome == "failure") thrown = (await action.Should().ThrowAsync<SqliteException>()).Which;
        else if (outcome == "cancellation") thrown = (await action.Should().ThrowAsync<OperationCanceledException>()).Which;
        else await action();

        captured.Should().NotBeNull();
        captured!.EndTime.Should().NotBeNull();
        captured.NodeMap.Should().HaveCount(outcome == "success" ? 4 : 3);
        captured.NodeMap.Values.Should().OnlyContain(node => node.EndTime != null);
        captured.Root!.Exception.Should().BeSameAs(thrown);
        var sql = captured.Root.Children!.Single(node => node.Database?.Phase == DatabaseTracePhase.CommandExecute);
        sql.Type.Should().Be(EChainTracingType.Database);
        if (thrown is not null)
        {
            captured.Root.IsFailed.Should().BeTrue();
            sql.IsFailed.Should().BeTrue();
            if (outcome == "failure") sql.Exception.Should().BeSameAs(thrown);
            else sql.Result.Should().Contain("Canceled");
        }
        else sql.IsFailed.Should().NotBeTrue();

        tracing.GetCurrentChain().Should().BeNull("the operation owns its async-local root");
        factory.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task SequentialBusinessExecutions_ShouldAggregateOnlyInsideTheirOwnChain()
    {
        var factory = new TracingFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        var database = scope.Resolve<TracingDbContext>();
        var pipeline = scope.Resolve<IExecutionPipeline>();
        var chains = new List<ChainTraceContext>();

        for (var operation = 0; operation < 2; operation++)
        {
            await pipeline.ExecuteAsync(Descriptor(), "input", null, async () =>
            {
                chains.Add(tracing.GetCurrentChain()!);
                for (var command = 0; command < 3; command++)
                    (await database.Database.SqlQueryRaw<int>("SELECT 42 AS Value")
                        .SingleAsync(TestContext.Current.CancellationToken)).Should().Be(42);
            }, TestContext.Current.CancellationToken);
        }

        chains[0].Should().NotBeSameAs(chains[1]);
        foreach (var chain in chains)
        {
            chain.EndTime.Should().NotBeNull();
            chain.Root!.Children!.Should().HaveCount(3).And.OnlyContain(node => node.RepeatCount == 2);
            chain.NodeMap.Values.Should().OnlyContain(node => node.EndTime != null);
        }
        factory.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task NestedBusinessExecution_ShouldLeaveTheCallerScopeActive()
    {
        var factory = new TracingFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        using var parent = tracing.BeginScope("Caller", "Test");
        var caller = tracing.GetCurrentNode();

        await scope.Resolve<IExecutionPipeline>().ExecuteAsync(Descriptor(), "input", null, async () =>
        {
            await scope.Resolve<TracingDbContext>().Database.SqlQueryRaw<int>("SELECT 42 AS Value")
                .SingleAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        tracing.GetCurrentNode().Should().BeSameAs(caller);
        tracing.GetCurrentChain()!.EndTime.Should().BeNull();
        caller!.EndTime.Should().BeNull();
        var execution = caller.Children!.Should().ContainSingle().Subject;
        execution.EndTime.Should().NotBeNull();
        execution.Children!.Should().HaveCount(3).And.OnlyContain(node => node.EndTime != null);
        factory.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task NonBusinessExecution_ShouldNotStartAChainForPollingSql()
    {
        var factory = new TracingFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();

        await scope.Resolve<IExecutionPipeline>().ExecuteAsync(Descriptor(isBusinessOperation: false), "input", null, async () =>
        {
            await scope.Resolve<TracingDbContext>().Database.SqlQueryRaw<int>("SELECT 42 AS Value")
                .SingleAsync(TestContext.Current.CancellationToken);
            tracing.GetCurrentChain().Should().BeNull();
        }, TestContext.Current.CancellationToken);

        factory.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task SqlCaptureLimit_ShouldSkipNodesWithoutBreakingTheExecution()
    {
        var factory = new TracingFactory(maxNodeCount: 1);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var tracing = scope.Resolve<IChainTracing>();
        ChainTraceContext? captured = null;

        await scope.Resolve<IExecutionPipeline>().ExecuteAsync(Descriptor(), "input", null, async () =>
        {
            captured = tracing.GetCurrentChain();
            for (var i = 0; i < 3; i++)
                (await scope.Resolve<TracingDbContext>().Database.SqlQueryRaw<int>("SELECT 42 AS Value")
                    .SingleAsync(TestContext.Current.CancellationToken)).Should().Be(42);
        }, TestContext.Current.CancellationToken);

        captured!.NodeMap.Should().ContainSingle();
        captured.Root!.EndTime.Should().NotBeNull();
        factory.Logs.Warnings.Should().ContainSingle().Which.Should().Contain("node count");
    }

    private static ExecutionDescriptor Descriptor(bool isBusinessOperation = true)
        => ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.chain-tracing.background"), typeof(ChainTracingDatabaseExecutionTests),
            null, isBusinessOperation, ExecutionTransactionMode.None);

    private sealed class TracingFactory(CancellationTokenSource? cancelOnCommand = null, int maxNodeCount = 1000)
        : MonicaTestApplicationFactory<ModuleChainTracing>
    {
        public TraceLogs Logs { get; } = new();

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
            => builder.AddChainTracing(options => options.MaxNodeCount = maxNodeCount)
                .UseExecutionTracing().UseEntityFrameworkCoreTracing();

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDbContext<TracingDbContext>(options => options.UseSqlite("Data Source=:memory:")
                .AddInterceptors(new SecondCommandInterceptor(cancelOnCommand)));
        }
    }

    private sealed class SecondCommandInterceptor(CancellationTokenSource? cancelOnCommand) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            cancelOnCommand?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TracingDbContext(DbContextOptions<TracingDbContext> options) : DbContext(options);

    private sealed class TraceLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new TraceLogger(categoryName, Warnings);
        public void Dispose() { }

        private sealed class TraceLogger(string category, ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel)
                => logLevel >= LogLevel.Warning && category.Contains(".ChainTracing.", StringComparison.Ordinal);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
