using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Services.Support;

namespace Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;

/// <summary>Measures logical connection opening without interpreting it as physical connection or pool wait.</summary>
internal sealed class ChainTracingDbConnectionInterceptor(DatabaseTraceRecorder recorder,
    ILogger<ChainTracingDbConnectionInterceptor> logger) : DbConnectionInterceptor
{
    private readonly ConditionalWeakTable<DbConnection, DatabaseTraceOperation> _pending = new();

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result)
    {
        if (result.IsSuppressed) return result;
        try
        {
            var operation = recorder.Start(DatabaseTracePhase.ConnectionOpen, "OpenConnection", "OpenConnection",
                eventData.Context?.GetType().FullName, eventData.ConnectionId, null);
            if (operation is not null) _pending.Add(connection, operation);
        }
        catch (Exception error) { logger.LogWarning(error, "Failed to start connection tracing."); }
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ConnectionOpening(connection, eventData, result));

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Complete(connection, eventData.Duration);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Complete(connection, eventData.Duration);
        return Task.CompletedTask;
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
        => Complete(connection, eventData.Duration, eventData.Exception);

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Complete(connection, eventData.Duration, eventData.Exception);
        return Task.CompletedTask;
    }

    private void Complete(DbConnection connection, TimeSpan duration, Exception? exception = null)
    {
        if (!_pending.TryGetValue(connection, out var operation) || !_pending.Remove(connection)) return;
        try
        {
            operation.Complete(exception is OperationCanceledException ? "Canceled"
                : exception is null ? "Success" : "Failed", exception, duration);
        }
        catch (Exception error) { logger.LogWarning(error, "Failed to complete connection tracing."); }
    }
}
