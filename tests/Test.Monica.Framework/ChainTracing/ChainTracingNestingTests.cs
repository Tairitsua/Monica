using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Results;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Services;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

/// <summary>
/// Verifies chain-tree integrity for nested, parallel, and out-of-order scopes, including database
/// nodes that intentionally skip ambient-scope tracking.
/// </summary>
public sealed class ChainTracingNestingTests
{
    [Fact]
    public void NestedScope_WithDatabaseChild_ShouldCompleteDatabaseWithoutDisturbingParents()
    {
        var tracing = CreateTracing();

        var action = tracing.BeginTrace("Action", "Controller");
        var exec = tracing.BeginTrace("Handle", "Handler");
        var sql = tracing.BeginTrace("SELECT 1", null, type: EChainTracingType.Database);

        tracing.EndTrace(sql, "Success[1ms]");

        // The still-running ancestors must remain active so later nodes attach below them.
        tracing.GetChainDepth().Should().Be(2, "database completion must not close its parents");
        tracing.GetCurrentChain()!.IsolatedNodes.Should().BeNullOrEmpty();

        var sql2 = tracing.BeginTrace("SELECT 2", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql2);

        tracing.EndTrace(exec, "Result(Ok)");
        tracing.EndTrace(action, "Result(Ok)");

        var chain = tracing.GetCurrentChain()!;
        chain.IsolatedNodes.Should().BeNullOrEmpty("nothing closed out of order");
        chain.Root!.Operation.Should().Be("Action");
        chain.Root.Children!.Should().ContainSingle(c => c.Operation == "Handle")
            .Which.Children!.Should().HaveCount(2, "both SQL commands must appear under their handler");
    }

    [Fact]
    public async Task ParallelScopes_FromSameParent_ShouldAttachAsSiblingsWithoutInterference()
    {
        var tracing = CreateTracing();

        var parent = tracing.BeginTrace("Parent", "P");
        var first = string.Empty;
        var second = string.Empty;

        await Task.WhenAll(
            Task.Run(async () =>
            {
                first = tracing.BeginTrace("First", "A");
                await Task.Yield();
                tracing.EndTrace(first, "ok");
            }, TestContext.Current.CancellationToken),
            Task.Run(async () =>
            {
                second = tracing.BeginTrace("Second", "B");
                await Task.Yield();
                tracing.EndTrace(second, "ok");
            }, TestContext.Current.CancellationToken));

        tracing.EndTrace(parent, "ok");

        var chain = tracing.GetCurrentChain()!;
        chain.IsolatedNodes.Should().BeNullOrEmpty("parallel siblings are legitimate, not out-of-order");
        chain.Root!.Children!.Select(c => c.Operation).Should().BeEquivalentTo(["First", "Second"]);
        chain.Root.Children.Should().OnlyContain(c => c.EndTime != null && c.IsFailed == null);
    }

    [Fact]
    public void OutOfOrderAncestorClose_ShouldIsolateLeakedScopeButKeepTreeIntact()
    {
        var tracing = CreateTracing();

        var parent = tracing.BeginTrace("Parent", "P");
        var outer = tracing.BeginTrace("Outer", "A");
        var inner = tracing.BeginTrace("Inner", "B");

        // The ancestor closes first; the still-open descendant leaks and must surface as isolated.
        tracing.EndTrace(outer, "closed early");
        tracing.EndTrace(inner, "late completion");
        tracing.EndTrace(parent, "ok");

        var chain = tracing.GetCurrentChain()!;
        chain.IsolatedNodes.Should().ContainSingle(n => n.Operation == "Inner");
        chain.Root!.Children!.Should().ContainSingle(c => c.Operation == "Outer")
            .Which.Children!.Should().ContainSingle(c => c.Operation == "Inner",
                "isolation reports the leak but never removes tree nodes");
    }

    [Fact]
    public void DeepNesting_WithMultipleDatabaseCommandsPerLevel_ShouldBuildFullTree()
    {
        var tracing = CreateTracing();

        var action = tracing.BeginTrace("Action", "Controller");
        var service = tracing.BeginTrace("Service", "AppService");
        var sql1 = tracing.BeginTrace("SELECT a", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql1);

        var domain = tracing.BeginTrace("Domain", "DomainService");
        var sql2 = tracing.BeginTrace("SELECT b", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql2);
        tracing.EndTrace(domain);

        var sql3 = tracing.BeginTrace("SELECT c", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql3);

        tracing.EndTrace(service);
        tracing.EndTrace(action);

        var chain = tracing.GetCurrentChain()!;
        chain.IsolatedNodes.Should().BeNullOrEmpty();

        var root = chain.Root!;
        root.Operation.Should().Be("Action");
        var serviceNode = root.Children!.Should().ContainSingle(c => c.Operation == "Service").Subject;
        serviceNode.Children!.Select(c => c.Operation).Should().Equal(["SELECT a", "Domain", "SELECT c"]);
        serviceNode.Children.Should().ContainSingle(c => c.Operation == "Domain")
            .Which.Children!.Should().ContainSingle(c => c.Operation == "SELECT b");
    }

    [Fact]
    public void FailedDatabaseCommand_ShouldMarkNodeFailedButKeepChainIntact()
    {
        var tracing = CreateTracing();

        var action = tracing.BeginTrace("Action", "Controller");
        var sql = tracing.BeginTrace("UPDATE x", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql, "Error", success: false, exception: new InvalidOperationException("boom"));

        var sql2 = tracing.BeginTrace("SELECT y", null, type: EChainTracingType.Database);
        tracing.EndTrace(sql2);
        tracing.EndTrace(action, "failed");

        var chain = tracing.GetCurrentChain()!;
        chain.IsolatedNodes.Should().BeNullOrEmpty();
        chain.Root!.Children!.Should().HaveCount(2);
        chain.Root.Children[0].IsFailed.Should().BeTrue();
    }

    [Fact]
    public void MergedRemoteCorrelation_ShouldSurviveScopeCompletion_AndAppearOnNodeFields()
    {
        var tracing = CreateTracing();
        var remote = Result.Failure("downstream rejected", ResultStatus.InternalError);
        remote.SetError(new ResultError("dependency.failed", "trace-remote-1", "RemoteService", "Query"));
        remote.SetMetadata(ResultMetadataKeys.RemoteService, "flight-route-api");

        var node = tracing.BeginTrace("Invoke remote", "Proxy");
        tracing.MergeRemoteChain(node, remote);
        tracing.EndTrace(node, "Result(InternalError)", success: false);

        var completed = tracing.GetCurrentChain()!.Root!;
        completed.RemoteTraceId.Should().Be("trace-remote-1");
        completed.RemoteService.Should().Be("flight-route-api");
        var endExtra = JsonSerializer.Serialize(completed.EndExtraInfo);
        endExtra.Should().Contain("dependency.failed");
    }

    [Fact]
    public void SuccessfulRemoteCall_ShouldCorrelateTargetAndDownstreamTrace()
    {
        var tracing = CreateTracing();
        var remote = Result.Success<string>("payload");
        // Metadata values cross process boundaries as JSON elements; correlation must read either form.
        remote.SetMetadata(ResultMetadataKeys.TraceId, JsonSerializer.SerializeToElement("trace-remote-2"));
        remote.SetMetadata(ResultMetadataKeys.RemoteService,
            JsonSerializer.SerializeToElement("flight-route-api"));

        var node = tracing.BeginTrace("Invoke remote", "Proxy", type: EChainTracingType.RemoteService);
        tracing.MergeRemoteChain(node, remote);
        tracing.EndTrace(node, "String(Ok)");

        var completed = tracing.GetCurrentChain()!.Root!;
        completed.IsRemoteCall.Should().BeTrue();
        completed.RemoteTraceId.Should().Be("trace-remote-2");
        completed.RemoteService.Should().Be("flight-route-api");
        completed.EndExtraInfo.Should().BeNull("a successful call has no error origin to record");
    }

    [Fact]
    public void ChainRoot_ShouldCarryConfiguredServiceIdentity()
    {
        var tracing = new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption { ServiceName = "flight-api" }),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));

        var root = tracing.BeginTrace("Action", "Controller");
        var child = tracing.BeginTrace("Invoke remote", "Proxy");
        tracing.EndTrace(child);
        tracing.EndTrace(root);

        var chain = tracing.GetCurrentChain()!;
        chain.Root!.Service.Should().Be("flight-api");
        chain.Root.Children!.Should().ContainSingle(c => c.Operation == "Invoke remote")
            .Which.Service.Should().BeNull("only the root carries the owning host identity");
    }

    [Fact]
    public void LateNode_WithoutAmbientParent_ShouldAttachToExistingRoot()
    {
        var tracing = CreateTracing();

        var root = tracing.BeginTrace("Root", "R");
        tracing.EndTrace(root);

        // A node started after every scope closed (for example a stray database command) must not be lost.
        var late = tracing.BeginTrace("SELECT late", null, type: EChainTracingType.Database);
        tracing.EndTrace(late);

        var chain = tracing.GetCurrentChain()!;
        chain.Root!.Children!.Select(c => c.Operation).Should().Equal(["SELECT late"]);
    }

    private static AsyncLocalChainTracingService CreateTracing()
    {
        return new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption()),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
    }
}
