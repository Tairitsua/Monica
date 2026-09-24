using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Monica.Core.ExceptionHandling.Models;

/// <summary>
/// A response-owned exception catalog. Exception identifiers are local to this document;
/// a remote response owns its own catalog and identifier scope.
/// </summary>
public sealed class ExceptionDiagnosticDocument
{
    /// <summary>The structured diagnostics wire version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The exception that reached this response boundary, when one was thrown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionId { get; set; }

    /// <summary>Each exception appears once; chains and causes refer to its identifier.</summary>
    public List<ExceptionDiagnosticEntry> Exceptions { get; init; } = [];

    /// <summary>The local request boundary that captured the exception.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticRequest? Request { get; set; }

    /// <summary>Whether a diagnostic size or nesting limit omitted detail.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    internal Dictionary<Exception, string> ExceptionIds { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<string, string> ImportedSignatures { get; } = new(StringComparer.Ordinal);

    /// <summary>Returns whether this document captured the same local exception instance.</summary>
    public bool Contains(Exception exception) => ExceptionIds.ContainsKey(exception);
}

/// <summary>One exception's own message and stack, with explicit references to its causes.</summary>
public sealed class ExceptionDiagnosticEntry
{
    /// <summary>Identifier referenced by this response's chain and other exception entries.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>CLR exception type, or the remote host's reported exception type.</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>This exception's message, excluding serialized remote responses and inner messages.</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>This exception's stack frames, excluding inner exceptions' stacks.</summary>
    public string[] StackTrace { get; set; } = [];
    /// <summary>All direct causes, including every branch of an aggregate exception.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? InnerExceptionIds { get; set; }
    /// <summary>The structured response from the service whose invocation failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteDiagnosticResponse? Remote { get; set; }
    /// <summary>Whether limits omitted part of this exception's diagnostic detail.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }
}

/// <summary>Request context belongs to the response boundary, rather than every exception.</summary>
public sealed record DiagnosticRequest(string? Method, string? Path, string? Endpoint, DateTime UtcTime);

/// <summary>
/// Diagnostic evidence from one remote response. Its chain's exception identifiers refer only to
/// <see cref="Diagnostics"/>; they never refer to the caller's exception catalog.
/// </summary>
public sealed class RemoteDiagnosticResponse
{
    /// <summary>Transport that supplied the response, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transport { get; init; }
    /// <summary>Reported upstream HTTP or envelope status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Status { get; init; }
    /// <summary>Upstream response summary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
    /// <summary>Logical service that produced the remote response.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Service { get; init; }
    /// <summary>Trace identifier captured at the remote boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
    /// <summary>HTTP method reported by the remote boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }
    /// <summary>Request path reported by the remote boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
    /// <summary>The remote service's independent exception catalog.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionDiagnosticDocument? Diagnostics { get; init; }
    /// <summary>The remote call chain, with exception details replaced by catalog references.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Chain { get; init; }
    /// <summary>Additional chains reported by legacy forwarding hosts or detached scopes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonArray? AdditionalChains { get; init; }
    /// <summary>Whether limits omitted remote diagnostic detail.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }
}
