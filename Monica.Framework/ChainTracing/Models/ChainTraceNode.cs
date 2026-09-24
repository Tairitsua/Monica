using System.Text.Json.Serialization;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.Results.Abstractions;

namespace Monica.Framework.ChainTracing.Models;

/// <summary>
/// Represents a single node in a call chain.
/// </summary>
public class ChainTraceNode
{
    /// <summary>
    /// Depth of the node within the chain.
    /// </summary>
    public int Depth { get; set; }

    private string? _duration;
    private EChainTracingType _type;
    private int _repeatCount;

    /// <summary>
    /// Number of identical database commands aggregated onto this node beyond the first execution.
    /// Batch writers and repeated lookups collapse into one node so a tens-of-thousands-row insert
    /// cannot balloon the chain; the node's time window spans every aggregated execution.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RepeatCount => _repeatCount;

    /// <summary>
    /// Sets the parent node and updates the depth.
    /// </summary>
    /// <param name="parent">The parent node.</param>
    public void SetParent(ChainTraceNode parent)
    {
        Depth = parent.Depth + 1;
        Parent = parent;
    }

    /// <summary>
    /// Records one more identical database command aggregated onto this node, extending its window to
    /// the aggregated execution's completion so <see cref="Duration" /> reflects the whole batch.
    /// </summary>
    /// <param name="failed">Whether the aggregated execution failed.</param>
    /// <param name="endTime">Completion time of the aggregated execution.</param>
    public void AddRepeat(bool failed, DateTime endTime)
    {
        lock (this)
        {
            _repeatCount++;
            EndTime = endTime;
            if (failed) IsFailed = true;
        }
    }

    /// <summary>
    /// Recalculates descendant depths and prunes children beyond the depth limit.
    /// </summary>
    public void RecalculateDepthAndTrim(int currentDepth, int maxChainDepth)
    {
        Depth = currentDepth;

        if (Children is null or { Count: 0 })
        {
            return;
        }

        if (currentDepth > maxChainDepth)
        {
            Children.Clear();
            return;
        }

        foreach (var child in Children)
        {
            child.RecalculateDepthAndTrim(currentDepth + 1, maxChainDepth);
        }
    }

    /// <summary>
    /// Trace node category.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EChainTracingType Type
    {
        get => _type;
        set
        {
            _type = value;
            IsRemoteCall = value == EChainTracingType.RemoteService;
        }
    }

    /// <summary>
    /// Unique identifier of the trace node.
    /// </summary>
    [JsonIgnore]
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Handler name, such as a service or class name.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Handler { get; set; }

    /// <summary>
    /// Operation name, such as a method or action description.
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Time when the node started.
    /// </summary>
    [JsonIgnore]
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Time when the node completed.
    /// </summary>
    [JsonIgnore]
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Duration of the node formatted in milliseconds.
    /// </summary>
    public string? Duration
    {
        get => _duration ?? (EndTime?.Subtract(StartTime).TotalMilliseconds is { } milliseconds ? $"{milliseconds}ms" : null);
        set => _duration = value;
    }

    /// <summary>
    /// Description of the result.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; set; }

    /// <summary>
    /// Indicates whether the call failed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFailed { get; set; }

    /// <summary>
    /// Indicates whether this node represents a remote call.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRemoteCall { get; set; }

    /// <summary>
    /// Service identity (for example the Dapr app id) of the host that recorded this chain.
    /// Labeled on the chain root when the host configures <c>ModuleChainTracingOption.ServiceName</c>,
    /// so multi-hop debug output (a forwarded response carrying several chains) stays self-describing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Service { get; set; }

    /// <summary>
    /// Service identity of the remote target this node invoked, regardless of outcome. Chain tracing is
    /// the debugging channel when no distributed-tracing infrastructure exists, so both successful and
    /// failed remote calls carry the target identity.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteService { get; set; }

    /// <summary>
    /// Correlation identifier returned by the remote service for the call this node represents.
    /// Use it to look up the remote host's own chain or logs; captured for successful and failed calls alike.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteTraceId { get; set; }

    /// <summary>
    /// Structured diagnostics returned by this call's remote service. Exception identifiers inside this
    /// response belong to its own diagnostics document, independently of the enclosing host's document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteDiagnosticResponse? Remote { get; set; }

    // Keeps direct envelope forwarding associated with its originating invocation until response projection.
    [JsonIgnore]
    internal IResultEnvelope? RemoteEnvelope { get; set; }

    /// <summary>
    /// Captured exception.
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Identifier of this node's exception in the owning response's diagnostics document. Propagating the
    /// same exception through multiple scopes reuses this identifier instead of repeating its stack.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionId { get; set; }

    /// <summary>
    /// Extra metadata captured when the node starts.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StartExtraInfo { get; set; }

    /// <summary>
    /// Extra metadata captured when the node ends.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? EndExtraInfo { get; set; }

    /// <summary>
    /// Child trace nodes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChainTraceNode>? Children { get; set; }

    /// <summary>
    /// Parent trace node.
    /// </summary>
    [JsonIgnore]
    public ChainTraceNode? Parent { get; private set; }

    /// <summary>
    /// Optional notes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remarks { get; set; }

    public override string ToString()
    {
        var target = RemoteService is null ? null : $"->{RemoteService}";
        return $"[{Type}{target}]{Handler}-{Operation}";
    }
}
