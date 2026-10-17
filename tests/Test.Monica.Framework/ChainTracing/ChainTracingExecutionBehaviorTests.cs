using AwesomeAssertions;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Providers.Execution;
using Monica.Rpc.Abstractions;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingExecutionBehaviorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenResultIsSuccessfulEnvelope_ShouldCompleteSuccessfulTrace()
    {
        var tracing = new TrackingChainTracing();
        var behavior = new ChainTracingExecutionBehavior<string, Res<string>>(tracing);
        var expected = Res.Ok<string>("payload");
        expected.Message = "done";

        var result = await behavior.ExecuteAsync(
            CreateContext<LocalComponent, Res<string>>(),
            () => Task.FromResult(expected));

        result.Should().BeSameAs(expected);
        tracing.BeginCount.Should().Be(1);
        tracing.EndCount.Should().Be(1);
        tracing.EndSuccess.Should().BeTrue();
        tracing.EndResult.Should().Contain("String(Ok)");
        tracing.EndResult.Should().Contain("[done]");
        tracing.EndException.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenResultIsFailedEnvelope_ShouldCompleteFailedTrace()
    {
        var tracing = new TrackingChainTracing();
        var behavior = new ChainTracingExecutionBehavior<string, Res<string>>(tracing);
        var expected = new Res<string>("invalid", ResStatus.BadRequest);

        var result = await behavior.ExecuteAsync(
            CreateContext<LocalComponent, Res<string>>(),
            () => Task.FromResult(expected));

        result.Should().BeSameAs(expected);
        tracing.EndSuccess.Should().BeFalse();
        tracing.EndResult.Should().Contain("String(BadRequest)");
        tracing.EndException.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalThrows_ShouldCompleteTraceWithSameExceptionAndRethrow()
    {
        var tracing = new TrackingChainTracing();
        var behavior = new ChainTracingExecutionBehavior<string, Res<string>>(tracing);
        var expected = new InvalidOperationException("terminal failed");

        var action = () => behavior.ExecuteAsync(
            CreateContext<LocalComponent, Res<string>>(),
            () => Task.FromException<Res<string>>(expected));

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
        tracing.EndCount.Should().Be(1);
        tracing.EndSuccess.Should().BeFalse();
        tracing.EndException.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResultIsNotEnvelope_ShouldBypassTracing()
    {
        var tracing = new TrackingChainTracing();
        var behavior = new ChainTracingExecutionBehavior<string, string>(tracing);

        var result = await behavior.ExecuteAsync(
            CreateContext<LocalComponent, string>(),
            () => Task.FromResult("result"));

        result.Should().Be("result");
        tracing.BeginCount.Should().Be(0);
        tracing.EndCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenComponentIsRemoteApi_ShouldMergeRemoteTraceBeforeCompletion()
    {
        var tracing = new TrackingChainTracing();
        var behavior = new ChainTracingExecutionBehavior<string, Res<string>>(tracing);
        var expected = Res.Ok<string>("remote result");

        var result = await behavior.ExecuteAsync(
            CreateContext<RemoteComponent, Res<string>>(),
            () => Task.FromResult(expected));

        result.Should().BeSameAs(expected);
        tracing.BeginType.Should().Be(EChainTracingType.RemoteService);
        tracing.MergedResponse.Should().BeSameAs(expected);
        tracing.CallOrder.Should().Equal("begin", "merge", "end");
    }

    private static ExecutionContext<string> CreateContext<TComponent, TResult>()
    {
        var method = typeof(TComponent).GetMethod(nameof(LocalComponent.Execute))!;
        var descriptor = ExecutionDescriptor.ForMethod<string, TResult>(
            new ExecutionPoint("test.chain-tracing"),
            typeof(TComponent),
            method,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        return new ExecutionContext<string>(
            descriptor,
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class TrackingChainTracing : IChainTracing
    {
        private const string TRACE_ID = "test-trace";
        private bool _containsTrace;

        public int BeginCount { get; private set; }

        public int EndCount { get; private set; }

        public bool? EndSuccess { get; private set; }

        public string? EndResult { get; private set; }

        public Exception? EndException { get; private set; }

        public EChainTracingType BeginType { get; private set; }

        public IResultEnvelope? MergedResponse { get; private set; }

        public List<string> CallOrder { get; } = [];

        public string BeginTrace(
            string operation,
            string? handler,
            object? extraInfo = null,
            EChainTracingType type = EChainTracingType.Unknown)
        {
            BeginCount++;
            BeginType = type;
            _containsTrace = true;
            CallOrder.Add("begin");
            return TRACE_ID;
        }

        public void EndTrace(
            string traceId,
            string? result = null,
            bool success = true,
            Exception? exception = null,
            object? extraInfo = null)
        {
            traceId.Should().Be(TRACE_ID);
            EndCount++;
            EndSuccess = success;
            EndResult = result;
            EndException = exception;
            _containsTrace = false;
            CallOrder.Add("end");
        }

        public bool ContainsTrace(string traceId)
        {
            return _containsTrace && traceId == TRACE_ID;
        }

        public ChainTraceContext? GetCurrentChain()
        {
            return null;
        }

        public ChainTraceNode? GetCurrentNode()
        {
            return null;
        }

        public void MergeRemoteChain(string traceId, IResultEnvelope remoteRes)
        {
            traceId.Should().Be(TRACE_ID);
            MergedResponse = remoteRes;
            CallOrder.Add("merge");
        }

        public void Init()
        {
        }
    }

    private sealed class LocalComponent
    {
        public void Execute()
        {
        }
    }

    private sealed class RemoteComponent : IRpcApi
    {
        public void Execute()
        {
        }
    }
}
