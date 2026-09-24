using System.Dynamic;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Models;
using Monica.Modules;

namespace Monica.Framework.ChainTracing.Services.Support;

/// <summary>
/// Attaches completed chain-trace metadata to a result envelope at the owning HTTP boundary. Shared by
/// the MVC result filter and the exception-response diagnostics hook so normal and exception paths expose
/// the same correlation members.
/// </summary>
public sealed class ChainResultMetadataAttacher(
    IOptions<ModuleResultEnvelopeOption> envelopeOptions,
    IJsonSerializerOptionsProvider? serializer = null,
    IEnumerable<IRemoteExceptionDiagnosticsExtractor>? extractors = null)
{
    private readonly JsonSerializerOptions _json = serializer?.SerializerOptions ?? new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Marks the chain complete and attaches correlation metadata. The trace identifier is always public;
    /// the chain itself (SQL text, parameter values, exception diagnostics) is reserved for hosts
    /// that expose diagnostic details.
    /// </summary>
    /// <param name="chain">The active chain, if any.</param>
    /// <param name="httpContext">The owning request context, when available.</param>
    /// <param name="response">The response envelope to enrich.</param>
    public void Attach(ChainTraceContext? chain, HttpContext? httpContext, IResultEnvelope response)
    {
        if (chain is null) return;

        chain.MarkComplete();
        var traceId = ResultTraceId.Capture(httpContext);
        if (!envelopeOptions.Value.ExposeDiagnosticDetails || chain.Root is null)
        {
            response.SetMetadata(ResultMetadataKeys.TraceId, traceId);
            return;
        }

        response.Metadata ??= new ExpandoObject();
        var metadata = (IDictionary<string, object?>)response.Metadata;
        var nodes = EnumerateNodes(chain).ToArray();
        PreserveForwardedDiagnostics(response, metadata, chain.Root, nodes);

        var document = ExceptionDiagnosticProjection.GetOrCreate(response);
        foreach (var node in nodes)
        {
            node.ExceptionId = node.Exception is { } exception
                ? ExceptionDiagnosticProjection.Capture(document, exception, extractors)
                : null;
        }

        // Each host owns one top-level chain and one exception table. Forwarded tables live with their
        // remote call, preventing duplicate chains and ambiguous identifiers across service boundaries.
        response.SetMetadata(ResultMetadataKeys.TraceId, traceId);
        metadata[ChainTraceContext.CHAIN_KEY] = chain.Root;
        if (chain.IsolatedNodes is not null)
        {
            metadata[$"{ChainTraceContext.CHAIN_KEY}_error"] = chain.IsolatedNodes.Select(p => new
            {
                p.Operation,
                p.Handler,
                p.Duration,
                p.Type,
                p.ExceptionId,
                p.StartTime,
                p.EndTime,
                p.TraceId,
            }).ToArray();
        }
    }

    private void PreserveForwardedDiagnostics(IResultEnvelope response, IDictionary<string, object?> metadata,
        ChainTraceNode root, IReadOnlyList<ChainTraceNode> nodes)
    {
        metadata.TryGetValue(ChainTraceContext.CHAIN_KEY, out var existingChain);
        if (ReferenceEquals(existingChain, root)) return;

        var invocation = nodes.LastOrDefault(node => ReferenceEquals(node.RemoteEnvelope, response));
        metadata.TryGetValue(ResultMetadataKeys.Diagnostics, out var existingDiagnostics);
        var ownsLocalException = existingDiagnostics is ExceptionDiagnosticDocument localDocument
            && nodes.Any(node => node.Exception is { } exception && ContainsCapturedException(localDocument, exception));
        var forwarded = invocation is not null || existingChain is not null
            || existingDiagnostics is JsonElement
            || (existingDiagnostics is ExceptionDiagnosticDocument || metadata.ContainsKey("exception"))
                && metadata.ContainsKey(ResultMetadataKeys.RemoteService) && !ownsLocalException;
        if (!forwarded) return;

        var remote = invocation?.Remote ?? ExceptionDiagnosticProjection.CaptureRemote(response, _json);
        if (remote is not null)
        {
            (invocation ?? root).Remote ??= remote;
        }

        // The original remote response is already represented by its invocation. Removing its top-level
        // table ensures even in-process typed forwarding creates a fresh local identifier scope.
        foreach (var key in metadata.Keys.Where(IsForwardedDiagnosticKey).ToArray())
        {
            metadata.Remove(key);
        }
    }

    private static bool ContainsCapturedException(ExceptionDiagnosticDocument document, Exception exception)
    {
        // Exception handling unwraps contextual exceptions before capture; the chain can still own their
        // original wrappers. A captured cause therefore also proves that this is the local document.
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var candidate))
        {
            if (!visited.Add(candidate)) continue;
            if (document.Contains(candidate)) return true;
            if (candidate is AggregateException aggregate)
            {
                foreach (var cause in aggregate.InnerExceptions) pending.Push(cause);
            }
            else if (candidate.InnerException is { } cause)
            {
                pending.Push(cause);
            }
        }

        return false;
    }

    private static bool IsForwardedDiagnosticKey(string key)
    {
        foreach (var prefix in new[] { "chain", "chain_error", "exception", ResultMetadataKeys.Diagnostics })
        {
            if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = key.AsSpan(prefix.Length).TrimStart('_');
            if (!suffix.IsEmpty && !suffix.ContainsAnyExceptInRange('0', '9')) return true;
        }

        return false;
    }

    private static IEnumerable<ChainTraceNode> EnumerateNodes(ChainTraceContext chain)
    {
        var visited = new HashSet<ChainTraceNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ChainTraceNode>(chain.NodeMap.Values);
        if (chain.Root is { } root) pending.Push(root);
        if (chain.IsolatedNodes is { } isolated)
        {
            foreach (var node in isolated) pending.Push(node);
        }

        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            yield return node;
            if (node.Children is not { } children) continue;
            foreach (var child in children) pending.Push(child);
        }
    }
}
