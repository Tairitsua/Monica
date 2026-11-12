using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;
using Monica.Framework.ChainTracing.Services;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingDbConnectionInterceptorTests
{
    [Theory]
    [InlineData(false, "Success")]
    [InlineData(true, "Success")]
    [InlineData(false, "Failed")]
    [InlineData(true, "Failed")]
    [InlineData(false, "Canceled")]
    [InlineData(true, "Canceled")]
    public async Task OpenCompletion_WhenCallbackChangesAsyncFlow_ShouldPreserveTimingOutcomeAndOriginalParent(bool asynchronous, string outcome)
    {
        var tracing = new AsyncLocalChainTracingService(Options.Create(new ModuleChainTracingOption()),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
        var interceptor = new ChainTracingDbConnectionInterceptor(new DatabaseTraceRecorder(tracing),
            NullLogger<ChainTracingDbConnectionInterceptor>.Instance);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var connectionId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var chain = await Task.Run(async () =>
        {
            tracing.BeginTrace("Query", "Handler");
            var opening = new ConnectionEventData(null!, null!, connection, null, connectionId, asynchronous, start);
            if (asynchronous) await interceptor.ConnectionOpeningAsync(connection, opening, default, TestContext.Current.CancellationToken);
            else interceptor.ConnectionOpening(connection, opening, default);
            return tracing.GetCurrentChain()!;
        }, TestContext.Current.CancellationToken);
        Assert.Null(tracing.GetCurrentChain());

        if (outcome == "Success")
        {
            var opened = new ConnectionEndEventData(null!, null!, connection, null, connectionId, asynchronous, start, TimeSpan.FromMilliseconds(120));
            if (asynchronous) await interceptor.ConnectionOpenedAsync(connection, opened, TestContext.Current.CancellationToken);
            else interceptor.ConnectionOpened(connection, opened);
            interceptor.ConnectionOpened(connection, opened); // Duplicate terminal callback is ignored.
        }
        else
        {
            Exception exception = outcome == "Canceled" ? new OperationCanceledException() : new InvalidOperationException("connection unavailable");
            var failed = new ConnectionErrorEventData(null!, null!, connection, null, connectionId, exception,
                asynchronous, start, TimeSpan.FromMilliseconds(120));
            if (asynchronous) await interceptor.ConnectionFailedAsync(connection, failed, TestContext.Current.CancellationToken);
            else interceptor.ConnectionFailed(connection, failed);
        }

        var node = Assert.Single(chain.Root!.Children!);
        Assert.Equal(outcome, node.Result);
        Assert.Equal(outcome != "Success", node.IsFailed == true);
        Assert.Equal(120, node.Database!.TotalDurationMs);
        Assert.Equal(120, node.Database.MaxDurationMs);
        Assert.Equal(1, node.Database.ExecutionCount);
        Assert.Equal(0, node.Database.PendingCount);
        Assert.Equal(connectionId, node.Database.ConnectionId);
        Assert.Null(node.Database.PoolWaitMs);
        Assert.Null(node.Database.PhysicalConnectMs);
        Assert.Null(tracing.GetCurrentChain());
    }
}
