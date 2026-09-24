using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Models;

namespace Monica.Framework.ChainTracing.Abstractions;

/// <summary>
/// Disposable scope wrapper for chain tracing.
/// </summary>
public class ChainTracingScope : IDisposable
{
    private readonly IChainTracing _chainTracing;
    private bool _disposed;

    /// <summary>
    /// Creates a new tracing scope and starts the trace immediately.
    /// </summary>
    /// <param name="chainTracing">The chain tracing service.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="handler">The handler name.</param>
    /// <param name="extraInfo">Optional extra metadata.</param>
    /// <param name="type">The traced operation type.</param>
    public ChainTracingScope(IChainTracing chainTracing, string operation, string? handler, object? extraInfo = null,
        EChainTracingType type = EChainTracingType.Unknown)
    {
        _chainTracing = chainTracing;
        TraceId = _chainTracing.BeginTrace(operation, handler, extraInfo, type);
    }

    /// <summary>
    /// Identifier of the trace node created for this scope.
    /// </summary>
    public string TraceId { get; }

    /// <summary>
    /// Completes the scope as a success.
    /// </summary>
    /// <param name="result">Optional result description.</param>
    /// <param name="extraInfo">Optional completion metadata.</param>
    public void EndWithSuccess(string? result = null, object? extraInfo = null)
    {
        if (!_disposed)
        {
            _chainTracing.EndTrace(TraceId, result ?? "Success", true, null, extraInfo);
            _disposed = true;
        }
    }

    /// <summary>
    /// Completes the scope as a failure.
    /// </summary>
    /// <param name="result">Optional result description.</param>
    /// <param name="extraInfo">Optional completion metadata.</param>
    public void EndWithFailure(string? result = null, object? extraInfo = null)
    {
        if (!_disposed)
        {
            _chainTracing.EndTrace(TraceId, result ?? "Failed", false, null, extraInfo);
            _disposed = true;
        }
    }

    /// <summary>
    /// Completes the scope with an exception.
    /// </summary>
    /// <param name="exception">The exception to record.</param>
    /// <param name="result">Optional result description.</param>
    /// <param name="extraInfo">Optional completion metadata.</param>
    public void EndWithException(Exception exception, string? result = null, object? extraInfo = null)
    {
        if (!_disposed)
        {
            _chainTracing.EndTrace(TraceId, result ?? $"Exception: {exception.GetType().Name}", false, exception, extraInfo);
            _disposed = true;
        }
    }

    /// <summary>
    /// Merges remote chain data into this scope.
    /// </summary>
    /// <param name="remoteChainInfo">The remote response carrying chain metadata.</param>
    public void MergeRemoteChain(IResultEnvelope remoteChainInfo)
    {
        if (!_disposed)
        { 
            _chainTracing.MergeRemoteChain(TraceId, remoteChainInfo);
        }
    }

    /// <summary>
    /// Releases the scope.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_chainTracing.ContainsTrace(TraceId))
            {
                _chainTracing.EndTrace(TraceId, "Auto-Completed", true, null);
            }
            _disposed = true;
        }
    }
}
