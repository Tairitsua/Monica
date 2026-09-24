using Monica.Core.Results;
using Monica.Core.ExceptionHandling.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Models;
using Monica.Modules;
using Monica.Tool.Diagnostics;
using Monica.Tool.Extensions;

namespace Monica.Framework.ChainTracing.Services;

/// <summary>
/// Chain tracing implementation backed by <see cref="AsyncLocal{T}" />. The chain context is shared by
/// reference within a request, while the ambient current node forks with each async flow, so parallel
/// scopes that begin under the same parent attach as siblings without racing each other.
/// </summary>
/// <remarks>
/// Creates a new <see cref="AsyncLocalChainTracingService" /> instance.
/// </remarks>
/// <param name="options">Chain tracing configuration options.</param>
/// <param name="logger">The logger.</param>
/// <param name="jsonSerializerOptionsProvider">Global JSON serialization options.</param>
public class AsyncLocalChainTracingService(IOptions<ModuleChainTracingOption> options, ILogger<AsyncLocalChainTracingService> logger, IJsonSerializerOptionsProvider jsonSerializerOptionsProvider) : IChainTracing
{
    private readonly AsyncLocal<ChainTraceContext?> _chainContext = new();
    private readonly AsyncLocal<ChainTraceNode?> _currentNode = new();
    private readonly ModuleChainTracingOption _options = options.Value;

    /// <summary>
    /// Starts a new trace node beneath the ambient current node.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="handler">The handler name.</param>
    /// <param name="extraInfo">Optional extra metadata.</param>
    /// <param name="type">The traced operation type.</param>
    /// <returns>The trace identifier.</returns>
    public string BeginTrace(string operation, string? handler, object? extraInfo = null,
        EChainTracingType type = EChainTracingType.Unknown)
    {
        try
        {
            var context = _chainContext.Value ??= new ChainTraceContext();

            if (IsMaxDepthReached())
            {
                WarnOnce(context, "Chain depth reached the {Limit} limit; skipping further nodes below {Handler}.{Operation}.",
                    _options.MaxChainDepth, handler, operation);
                return Guid.NewGuid().ToString("N"); // Return a synthetic TraceId so follow-up calls stay safe.
            }

            if (IsMaxNodeCountReached())
            {
                WarnOnce(context, "Chain node count reached the {Limit} limit; skipping {Handler}.{Operation}.",
                    _options.MaxNodeCount, handler, operation);
                return Guid.NewGuid().ToString("N"); // Return a synthetic TraceId so follow-up calls stay safe.
            }

            var node = new ChainTraceNode
            {
                Handler = handler,
                Operation = operation,
                Type = type,
                StartExtraInfo = extraInfo,
                StartTime = DateTime.UtcNow
            };

            context.AddNode(node, _currentNode.Value);

            // Label the chain root with this host's identity so multi-hop debug output is self-describing.
            if (ReferenceEquals(context.Root, node) && !string.IsNullOrEmpty(_options.ServiceName))
            {
                node.Service = _options.ServiceName;
            }

            if (ChainTraceContext.CanHaveChildOperations(type))
            {
                _currentNode.Value = node;
            }

            logger.LogDebug("Started chain node {Handler}.{Operation} ({TraceId}); depth {Depth}, nodes {NodeCount}.",
                handler, operation, node.TraceId, node.Depth, context.NodeMap.Count);

            return node.TraceId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start chain node {Handler}.{Operation}.", handler, operation);
            return Guid.NewGuid().ToString("N"); // Return a synthetic TraceId so follow-up calls stay safe.
        }
    }

    /// <summary>
    /// Completes a trace node and restores the ambient current node. When an ancestor completes while
    /// descendant scopes are still open in the same flow, those leaked scopes are recorded as isolated.
    /// </summary>
    /// <param name="traceId">The trace identifier.</param>
    /// <param name="result">A description of the result.</param>
    /// <param name="success">Whether the operation succeeded.</param>
    /// <param name="exception">The captured exception, if any.</param>
    /// <param name="extraInfo">Optional completion metadata.</param>
    public void EndTrace(string traceId, string? result = null, bool success = true, Exception? exception = null,
        object? extraInfo = null)
    {
        try
        {
            var context = _chainContext.Value;
            if (context == null)
            {
                logger.LogWarning("Cannot complete chain node {TraceId} because no chain context is active.", traceId);
                return;
            }

            context.CompleteNode(traceId, result, success, exception, extraInfo);
            RestoreCurrentNode(context, traceId);

            logger.LogDebug("Completed chain node {TraceId}; success {Success}, result {Result}.",
                traceId, success, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete chain node {TraceId}.", traceId);
        }
    }

    /// <summary>
    /// Gets the current call-chain context.
    /// </summary>
    /// <returns>The current chain context.</returns>
    public ChainTraceContext? GetCurrentChain()
    {
        return _chainContext.Value;
    }

    /// <summary>
    /// Gets the ambient current node of this flow. Database leaves never occupy it.
    /// </summary>
    /// <returns>The current node, or <see langword="null" /> when no scope is active.</returns>
    public ChainTraceNode? GetCurrentNode()
    {
        return _currentNode.Value;
    }

    /// <summary>
    /// Checks whether a chain is currently active.
    /// </summary>
    /// <returns><see langword="true" /> when a chain exists; otherwise, <see langword="false" />.</returns>
    public bool HasActiveChain()
    {
        return _chainContext.Value != null;
    }

    /// <summary>
    /// Gets the current chain depth measured as the tree depth of the ambient current node.
    /// </summary>
    /// <returns>The depth of the current node, or zero when no scope is active.</returns>
    public int GetChainDepth()
    {
        return _currentNode.Value?.Depth ?? 0;
    }

    /// <summary>
    /// Gets the total number of tracked nodes in the current chain.
    /// </summary>
    /// <returns>The number of tracked nodes.</returns>
    public int GetNodeCount()
    {
        var context = _chainContext.Value;
        return context?.NodeMap.Count ?? 0;
    }

    /// <summary>
    /// Checks whether the configured depth limit has been reached.
    /// </summary>
    /// <returns><see langword="true" /> when the depth limit is reached.</returns>
    public bool IsMaxDepthReached()
    {
        return GetChainDepth() >= _options.MaxChainDepth;
    }

    /// <summary>
    /// Checks whether the configured node-count limit has been reached.
    /// </summary>
    /// <returns><see langword="true" /> when the node-count limit is reached.</returns>
    public bool IsMaxNodeCountReached()
    {
        return GetNodeCount() >= _options.MaxNodeCount;
    }

    /// <summary>
    /// Links remote response correlation to a local trace node for every outcome. Chain tracing is the
    /// debugging channel when no distributed-tracing infrastructure exists, so successful calls record the
    /// target identity and the remote correlation identifier just like failed ones; failures additionally
    /// keep the typed error origin. Structured remote diagnostics remain scoped to that remote response
    /// so the local exception table never reinterprets a downstream exception identifier.
    /// </summary>
    /// <param name="traceId">The local trace node that should receive the remote correlation.</param>
    /// <param name="remoteRes">The response returned by the remote-call boundary.</param>
    public void MergeRemoteChain(string traceId, IResultEnvelope remoteRes)
    {
        if (_chainContext.Value is not { } context || !context.NodeMap.TryGetValue(traceId, out var node)) return;

        node.RemoteTraceId = ResolveRemoteTraceId(remoteRes);
        node.RemoteService ??= AsString(remoteRes.Metadata?.GetOrDefault(ResultMetadataKeys.RemoteService));
        node.Remote = ExceptionDiagnosticProjection.CaptureRemote(remoteRes, jsonSerializerOptionsProvider.SerializerOptions);
        node.RemoteEnvelope = remoteRes;

        if (remoteRes.TryGetError(jsonSerializerOptionsProvider.SerializerOptions, out var error))
            node.EndExtraInfo = new { error.Code, error.Service, error.Operation };
    }

    /// <summary>
    /// Resolves the remote host's correlation identifier: the trace-id metadata its result filter attached,
    /// or the typed error's trace identifier when the failure envelope carries no metadata.
    /// </summary>
    /// <param name="remoteRes">The remote response.</param>
    /// <returns>The remote trace identifier, or <see langword="null" /> when the remote host publishes none.</returns>
    private string? ResolveRemoteTraceId(IResultEnvelope remoteRes)
    {
        if (AsString(remoteRes.Metadata?.GetOrDefault(ResultMetadataKeys.TraceId)) is { } metadataTraceId)
            return metadataTraceId;

        return remoteRes.TryGetError(jsonSerializerOptionsProvider.SerializerOptions, out var error)
            ? error.TraceId
            : null;
    }

    /// <summary>
    /// Converts a metadata value that may be an in-memory string or a JSON-round-tripped element into text.
    /// </summary>
    /// <param name="value">The raw metadata value.</param>
    /// <returns>The textual value, or <see langword="null" /> when absent or not textual.</returns>
    private static string? AsString(object? value)
    {
        return value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    public void Init()
    {
        _chainContext.Value ??= new ChainTraceContext();
    }

    public bool ContainsTrace(string traceId)
    {
        var context = _chainContext.Value;
        return context?.NodeMap.ContainsKey(traceId) ?? false;
    }

    /// <summary>
    /// Emits a limit warning once per chain so volume beyond a limit cannot flood the log.
    /// </summary>
    /// <param name="context">The active chain context.</param>
    /// <param name="message">The warning message template.</param>
    /// <param name="args">The template arguments.</param>
    private void WarnOnce(ChainTraceContext context, string message, params object?[] args)
    {
        if (context.LimitWarningIssued)
        {
            return;
        }

        context.LimitWarningIssued = true;
        logger.LogWarning(message, args);
    }

    /// <summary>
    /// Restores the ambient current node after a completion. Only nodes that actually occupy this flow's
    /// ambient scope (or an ancestor of it) move the pointer; database leaves, foreign-flow nodes, and
    /// synthetic identifiers never disturb the enclosing scope.
    /// </summary>
    /// <param name="context">The active chain context.</param>
    /// <param name="traceId">The identifier of the completed node.</param>
    private void RestoreCurrentNode(ChainTraceContext context, string traceId)
    {
        var current = _currentNode.Value;
        if (current is null)
        {
            return;
        }

        if (current.TraceId == traceId)
        {
            _currentNode.Value = current.Parent;
            return;
        }

        if (!context.NodeMap.TryGetValue(traceId, out var node))
        {
            return;
        }

        // Out-of-order closure: an ancestor finished while descendant scopes in this flow are still open.
        // Record the leaked descendants, then continue from the completed ancestor's parent.
        var leaked = new List<ChainTraceNode>();
        var walk = current;
        while (walk is not null && walk != node)
        {
            leaked.Add(walk);
            walk = walk.Parent;
        }

        if (walk is null)
        {
            return; // The completed node is not on this flow's active path.
        }

        foreach (var descendant in leaked)
        {
            context.MarkIsolated(descendant);
        }

        _currentNode.Value = node.Parent;
    }
}
