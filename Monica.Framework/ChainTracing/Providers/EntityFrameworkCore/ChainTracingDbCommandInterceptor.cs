using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Models;
using Monica.Tool.Extensions;

namespace Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;

/// <summary>
/// Records EF Core commands beneath an active chain-tracing scope; commands without a parent are skipped.
/// Completion belongs to the node captured at command start, independently of the callback's async context.
/// All command kinds (reader, non-query, scalar) and terminal paths (executed, failed, canceled) are paired
/// to release command tracking on completion. Repeated commands with the same shape inside one ambient
/// scope aggregate onto a single node (with a repeat count), so batch writers inserting tens of
/// thousands of rows cannot balloon the chain or the diagnostic response.
/// </summary>
/// <param name="chainTracing">The chain tracing service.</param>
/// <param name="logger">The logger.</param>
public class ChainTracingDbCommandInterceptor(
    IChainTracing chainTracing,
    ILogger<ChainTracingDbCommandInterceptor> logger) : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<DbCommand, ChainTraceNode> _commandTraces = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<ChainTraceNode, Dictionary<(CommandType Type, string Text), ChainTraceNode>> _scopeTraces = new();

    private void StartCommandTrace(DbCommand command)
    {
        try
        {
            // EF may invoke start and completion in separate async contexts. An interceptor cannot
            // create an ambient root for its caller; the controller or business execution owns it.
            if (chainTracing.GetCurrentChain() is not { EndTime: null } context
                || chainTracing.GetCurrentNode() is not { EndTime: null } parent)
            {
                return;
            }

            // Weak ownership prevents a reused interceptor from retaining completed chains. Use the
            // entire command text so unrelated commands with a common prefix never share a node.
            var shapes = _scopeTraces.GetValue(parent, static _ => new());
            lock (shapes)
            {
                var shape = (command.CommandType, command.CommandText);
                if (!shapes.TryGetValue(shape, out var node))
                {
                    var extraInfo = new
                    {
                        command.CommandTimeout,
                        ParameterCount = command.Parameters.Count,
                        Parameters = command.Parameters.Cast<DbParameter>()
                            .Take(10)
                            .Select(p => new
                            {
                                Name = p.ParameterName,
                                DbType = p.DbType.ToString(),
                                Value = p.Value?.ToString()?.LimitMaxLength(100, "...")
                            })
                            .ToArray()
                    };

                    var traceId = chainTracing.BeginTrace(
                        command.CommandText.LimitMaxLength(1000, "..."),
                        null,
                        extraInfo,
                        EChainTracingType.Database);

                    // Capture limits return synthetic identifiers without allocating a node.
                    if (!context.NodeMap.TryGetValue(traceId, out node)) return;
                    shapes.Add(shape, node);
                }

                _commandTraces[command] = node;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record the start of a database command trace.");
        }
    }

    private void Complete(DbCommand command, CommandEventData eventData, object? result = null, bool isCanceled = false)
    {
        if (!_commandTraces.TryRemove(command, out var node))
        {
            return;
        }

        try
        {
            string resultDescription;
            var success = true;
            Exception? exception = null;

            if (eventData is CommandEndEventData endEvent)
            {
                resultDescription = "Success";

                if (eventData is CommandErrorEventData errorEvent)
                {
                    success = false;
                    exception = errorEvent.Exception;
                    resultDescription = "Error";
                }
                else if (isCanceled)
                {
                    success = false;
                    resultDescription = "Canceled";
                }
                else if (result is int affectedRows)
                {
                    resultDescription += $"[AffectedRows:{affectedRows}]";
                }

                resultDescription += $"[{endEvent.Duration.TotalMilliseconds:0.##}ms]";
            }
            else
            {
                resultDescription = "Unknown";
            }

            // Database nodes never occupy the ambient scope. Complete the captured leaf directly,
            // including repeats that finish out of order, without moving any caller's current node.
            lock (node)
            {
                var endTime = DateTime.UtcNow;
                if (node.EndTime.HasValue) node.AddRepeat(!success, endTime);
                else node.EndTime = endTime;

                if (!success) node.IsFailed = true;
                node.Exception ??= exception;
                node.Result = node.RepeatCount > 0
                    ? $"{resultDescription} ×{node.RepeatCount + 1}"
                    : resultDescription;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record the end of a database command trace.");
        }
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        StartCommandTrace(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Complete(command, eventData, result);

        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, result);

        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        StartCommandTrace(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Complete(command, eventData, result);

        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, result);

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        StartCommandTrace(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Complete(command, eventData, result);

        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, result);

        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        Complete(command, eventData);

        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData);

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        Complete(command, eventData, isCanceled: true);

        base.CommandCanceled(command, eventData);
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, isCanceled: true);

        return base.CommandCanceledAsync(command, eventData, cancellationToken);
    }
}
