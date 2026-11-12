using System.Diagnostics;
using System.Runtime.CompilerServices;
using Monica.Framework.ChainTracing.Models;

namespace Monica.Framework.ChainTracing.Services.Support;

/// <summary>Creates captured database leaves without changing the ambient business node.</summary>
internal sealed class DatabaseTraceRecorder(AsyncLocalChainTracingService tracing)
{
    private readonly ConditionalWeakTable<ChainTraceNode, Dictionary<Shape, ChainTraceNode>> _groups = new();

    public DatabaseTraceOperation? Start(DatabaseTracePhase phase, string operation, string shape,
        string? contextType, Guid connectionId, Guid? commandId, object? extra = null, DatabaseTraceOperation? origin = null)
    {
        if ((origin?.Chain ?? tracing.GetCurrentChain()) is not { EndTime: null } chain
            || (origin?.Parent ?? tracing.GetCurrentNode()) is not { EndTime: null } parent) return null;
        var groups = _groups.GetValue(parent, static _ => new());
        lock (groups)
        {
            var key = new Shape(phase, shape, contextType, connectionId);
            if (!groups.TryGetValue(key, out var node))
            {
                node = tracing.BeginDatabaseTrace(chain, parent, operation, extra);
                if (node is null) return null;
                node.Database = new DatabaseTraceDetails
                {
                    Phase = phase, ContextType = contextType, ConnectionId = connectionId, CommandId = commandId
                };
                groups.Add(key, node);
            }
            lock (node)
            {
                var details = node.Database!;
                if (details.ExecutionCount + details.PendingCount > 0) details.CommandId = null;
                details.PendingCount++;
                node.EndTime = null;
            }
            return new DatabaseTraceOperation(this, node, chain, parent);
        }
    }

    public void Complete(ChainTraceNode node, double durationMs, string result, Exception? exception)
    {
        lock (node)
        {
            var details = node.Database!;
            var failed = result is "Failed" or "Canceled";
            details.PendingCount--;
            if (details.ExecutionCount++ == 0) node.EndTime = DateTime.UtcNow;
            else node.AddRepeat(failed, DateTime.UtcNow);
            details.TotalDurationMs += durationMs;
            details.MaxDurationMs = Math.Max(details.MaxDurationMs, durationMs);
            details.WindowDurationMs = node.DurationMs;
            if (details.PendingCount != 0) node.EndTime = null;
            if (failed) node.IsFailed = true;
            node.Exception ??= exception;
            // A later successful repeat must not hide a failed or cancelled earlier attempt.
            var outcome = node.IsFailed == true && !failed ? "Failed" : result;
            node.Result = details.ExecutionCount == 1 ? outcome : $"{outcome} ×{details.ExecutionCount}";
        }
    }

    private sealed record Shape(DatabaseTracePhase Phase, string Text, string? ContextType, Guid ConnectionId);
}

/// <summary>A completion handle bound to the original node, independent of later async-local state.</summary>
internal sealed class DatabaseTraceOperation(DatabaseTraceRecorder owner, ChainTraceNode node,
    ChainTraceContext chain, ChainTraceNode parent)
{
    internal ChainTraceContext Chain { get; } = chain;
    internal ChainTraceNode Parent { get; } = parent;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _completed;

    public void Complete(string result = "Success", Exception? exception = null, TimeSpan? duration = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            owner.Complete(node, (duration ?? Stopwatch.GetElapsedTime(_started)).TotalMilliseconds, result, exception);
    }
}
