using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Tool.Extensions;

namespace Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;

/// <summary>Captures command execution and reader lifetime beneath the original business node.</summary>
internal sealed class ChainTracingDbCommandInterceptor(
    DatabaseTraceRecorder recorder,
    ILogger<ChainTracingDbCommandInterceptor> logger) : DbCommandInterceptor
{
    private readonly ConditionalWeakTable<DbCommand, CommandTrace> _commands = new();
    // EF retains the command through reader disposal, even if another interceptor replaces the reader.
    private readonly ConditionalWeakTable<DbCommand, DatabaseTraceOperation> _readers = new();

    private void StartCommandTrace(DbCommand command, CommandEventData data)
    {
        try
        {
            var contextType = data.Context?.GetType().FullName;
            var shape = command.CommandType + ":" + command.CommandText;
            var operation = recorder.Start(DatabaseTracePhase.CommandExecute,
                command.CommandText.LimitMaxLength(1000, "..."), shape,
                contextType, data.ConnectionId, data.CommandId,
                new { command.CommandTimeout, ParameterCount = command.Parameters.Count });
            if (operation is not null)
                _commands.Add(command, new CommandTrace(operation, shape, contextType, data.ConnectionId, data.CommandId));
        }
        catch (Exception error) { logger.LogWarning(error, "Failed to start database command tracing."); }
    }

    private void Complete(DbCommand command, CommandEndEventData data, object? result = null, bool isCanceled = false)
    {
        if (!_commands.TryGetValue(command, out var state) || !_commands.Remove(command)) return;
        try
        {
            var error = (data as CommandErrorEventData)?.Exception;
            state.Operation.Complete(isCanceled || error is OperationCanceledException ? "Canceled"
                : error is null ? "Success" : "Failed", error, data.Duration);
            if (error is null && !isCanceled && result is DbDataReader)
            {
                var consumption = recorder.Start(DatabaseTracePhase.ReaderConsume, "ConsumeResult", state.Shape,
                    state.ContextType, state.ConnectionId, state.CommandId, origin: state.Operation);
                if (consumption is not null) _readers.Add(command, consumption);
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Failed to complete database command tracing."); }
    }

    private void CompleteReader(DbCommand command)
    {
        if (!_readers.TryGetValue(command, out var operation) || !_readers.Remove(command)) return;
        try { operation.Complete("Closed"); }
        catch (Exception error) { logger.LogWarning(error, "Failed to complete database reader tracing."); }
    }

    public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        if (!result.IsSuppressed) CompleteReader(command);
        return result;
    }

    public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
        DataReaderClosingEventData eventData, InterceptionResult result)
        => ValueTask.FromResult(DataReaderClosing(command, eventData, result));

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
        InterceptionResult result)
    {
        if (!result.IsSuppressed) CompleteReader(command);
        return result;
    }

    private sealed record CommandTrace(DatabaseTraceOperation Operation, string Shape, string? ContextType,
        Guid ConnectionId, Guid CommandId);

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        StartCommandTrace(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Complete(command, eventData, result);

        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, result);

        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        StartCommandTrace(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Complete(command, eventData, result);

        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, result);

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        StartCommandTrace(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Complete(command, eventData, result);

        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        StartCommandTrace(command, eventData);
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
