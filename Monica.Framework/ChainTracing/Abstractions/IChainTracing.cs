using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Models;

namespace Monica.Framework.ChainTracing.Abstractions;

/// <summary>
/// Defines call-chain tracing operations for application flows.
/// </summary>
public interface IChainTracing
{
    /// <summary>
    /// Starts a new trace node.
    /// </summary>
    /// <param name="operation">The operation name, such as a method or action description.</param>
    /// <param name="handler">The handler name, such as a service or class name.</param>
    /// <param name="extraInfo">Optional extra metadata captured at the start of the trace.</param>
    /// <param name="type">The traced operation type.</param>
    /// <returns>The trace identifier used to complete the node later.</returns>
    string BeginTrace(string operation, string? handler, object? extraInfo = null,
        EChainTracingType type = EChainTracingType.Unknown);

    /// <summary>
    /// Completes a trace node.
    /// </summary>
    /// <param name="traceId">The trace identifier.</param>
    /// <param name="result">A description of the result.</param>
    /// <param name="success">Whether the operation succeeded.</param>
    /// <param name="exception">The captured exception, if any.</param>
    /// <param name="extraInfo">Optional extra metadata captured at completion time.</param>
    void EndTrace(string traceId, string? result = null, bool success = true, Exception? exception = null,
        object? extraInfo = null);

    /// <summary>
    /// Checks whether the current chain contains the specified trace node.
    /// </summary>
    /// <param name="traceId">The trace identifier.</param>
    /// <returns><see langword="true" /> when the node is tracked; otherwise, <see langword="false" />.</returns>
    bool ContainsTrace(string traceId);

    /// <summary>
    /// Gets the current call-chain context.
    /// </summary>
    /// <returns>The current chain, or <see langword="null" /> when no chain exists.</returns>
    ChainTraceContext? GetCurrentChain();

    /// <summary>
    /// Gets the ambient current node of this flow. Database leaves never occupy it, so callers that
    /// aggregate repeated commands can key their aggregation by the enclosing scope.
    /// </summary>
    /// <returns>The current node, or <see langword="null" /> when no scope is active.</returns>
    ChainTraceNode? GetCurrentNode();

    /// <summary>
    /// Links a remote response to a local trace node using its origin trace identifier and an independent
    /// structured diagnostic response. Remote exception identifiers retain their original document scope.
    /// </summary>
    /// <param name="traceId">The local trace node that should receive the remote correlation details.</param>
    /// <param name="remoteRes">The remote response carrying correlation and optional diagnostic metadata.</param>
    void MergeRemoteChain(string traceId, IResultEnvelope remoteRes);

    /// <summary>
    /// Initializes the current <see cref="AsyncLocal{T}" /> context.
    /// Prefer starting a chain explicitly at the outermost scope when possible.
    /// </summary>
    void Init();
} 
