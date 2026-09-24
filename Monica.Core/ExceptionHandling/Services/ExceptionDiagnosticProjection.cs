using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Text;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.ExceptionHandling.Services;

/// <summary>
/// Builds bounded response diagnostics without serializing Exception.ToString(). Runtime exceptions
/// are deduplicated by identity; legacy remote exception strings are normalized at the receive boundary.
/// </summary>
public static class ExceptionDiagnosticProjection
{
    private const int MAX_EXCEPTIONS = 64;
    private const int MAX_STACK_FRAMES = 128;
    private const int MAX_TEXT_LENGTH = 4096;
    private const int MAX_REMOTE_LENGTH = 1024 * 1024;
    private const int MAX_REMOTE_DEPTH = 6;
    private const int MAX_CHAIN_NODES = 512;
    private const int MAX_CHAIN_DEPTH = 24;
    private const string TRUNCATED_ID = "truncated";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the local response catalog, creating one when no typed local catalog exists.</summary>
    public static ExceptionDiagnosticDocument GetOrCreate(IResultEnvelope response)
    {
        if (response.Metadata is IDictionary<string, object?> metadata
            && metadata.TryGetValue(ResultMetadataKeys.Diagnostics, out var existing)
            && existing is ExceptionDiagnosticDocument document) return document;
        var created = new ExceptionDiagnosticDocument();
        response.SetMetadata(ResultMetadataKeys.Diagnostics, created);
        return created;
    }

    /// <summary>
    /// Captures an exception and its causes once in the given response. Call at the final response
    /// boundary so propagated exceptions retain their complete local stack.
    /// </summary>
    public static string Capture(ExceptionDiagnosticDocument document, Exception exception,
        IEnumerable<IRemoteExceptionDiagnosticsExtractor>? extractors = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(exception);
        return CaptureCore(document, exception, extractors?.ToArray() ?? [], 0);
    }

    /// <summary>
    /// Captures only a remote envelope's diagnostic metadata and summary, never its business data.
    /// Call before replacing forwarded diagnostic metadata with the local response's catalog.
    /// </summary>
    public static RemoteDiagnosticResponse? CaptureRemote(IResultEnvelope response, JsonSerializerOptions json,
        string? transport = null, int? statusCode = null)
    {
        if (response.Metadata is not IDictionary<string, object?> metadata) return null;
        var diagnosticMetadata = metadata.Where(pair => IsRemoteMetadata(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (diagnosticMetadata.Count == 0) return null;
        try
        {
            var element = JsonSerializer.SerializeToElement(new
            {
                message = response.Message,
                status = (int)response.Status,
                metadata = diagnosticMetadata
            }, json);
            return CaptureRemote(element, transport, statusCode);
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>Normalizes a current or legacy remote diagnostic envelope with bounded nesting.</summary>
    public static RemoteDiagnosticResponse? CaptureRemote(JsonElement response,
        string? transport = null, int? statusCode = null)
    {
        try { return CaptureRemoteCore(response, transport, statusCode, 0); }
        catch (JsonException)
        {
            // A malformed or excessively deep remote graph must not mask the invocation outcome.
            return new RemoteDiagnosticResponse { Transport = transport, Status = statusCode, Truncated = true };
        }
    }

    private static string CaptureCore(ExceptionDiagnosticDocument document, Exception exception,
        IRemoteExceptionDiagnosticsExtractor[] extractors, int depth)
    {
        if (document.ExceptionIds.TryGetValue(exception, out var existing)) return existing;
        if (document.Exceptions.Count >= MAX_EXCEPTIONS || depth >= MAX_EXCEPTIONS) return Truncate(document);

        var stack = exception.StackTrace?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        var message = exception.Message;
        var entry = new ExceptionDiagnosticEntry
        {
            Id = NextId(document),
            Type = Limit(exception.GetType().FullName ?? exception.GetType().Name),
            Message = Limit(message),
            StackTrace = Frames(stack)
        };
        document.ExceptionIds.Add(exception, entry.Id);
        document.Exceptions.Add(entry);

        foreach (var extractor in extractors)
        {
            try
            {
                if (!extractor.TryExtract(exception, out var payload)) continue;
                var remote = CaptureRemoteCore(payload.Response, payload.Transport, payload.StatusCode, 0);
                if (remote is null) continue;
                message = payload.Message;
                entry.Message = Limit(message);
                entry.Remote = remote;
                break;
            }
            catch (Exception)
            {
                // Diagnostic enrichment must never replace the original application failure.
            }
        }

        entry.Truncated = message.Length > MAX_TEXT_LENGTH || StackTruncated(stack);
        var causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions.ToArray()
            : exception.InnerException is { } inner ? [inner] : Array.Empty<Exception>();
        if (causes.Length > 0)
            entry.InnerExceptionIds = causes.Take(MAX_EXCEPTIONS)
                .Select(cause => CaptureCore(document, cause, extractors, depth + 1)).Distinct().ToArray();
        if (causes.Length > MAX_EXCEPTIONS) entry.Truncated = true;
        document.Truncated |= entry.Truncated;
        return entry.Id;
    }

    private static RemoteDiagnosticResponse? CaptureRemoteCore(JsonElement response, string? transport,
        int? statusCode, int depth, string? method = null, string? path = null)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;
        statusCode ??= Integer(Property(response, "status")) ?? Integer(Property(response, "code"));
        if (depth >= MAX_REMOTE_DEPTH || Encoding.UTF8.GetByteCount(response.GetRawText()) > MAX_REMOTE_LENGTH)
            return new RemoteDiagnosticResponse { Transport = transport, Status = statusCode, Truncated = true };
        var metadata = Property(response, "metadata");
        if (metadata.ValueKind != JsonValueKind.Object) return null;

        var document = new ExceptionDiagnosticDocument();
        var current = Property(metadata, ResultMetadataKeys.Diagnostics);
        var legacy = Property(metadata, "exception");
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (current.ValueKind == JsonValueKind.Object) ImportDocument(document, current, idMap, depth);
        else if (legacy.ValueKind == JsonValueKind.Object)
            document.ExceptionId = ImportLegacyException(document, legacy, depth);

        var nodes = 0;
        var chain = NormalizeChain(Property(metadata, "chain"), document, idMap, depth, 0, ref nodes);
        JsonArray? additional = null;
        foreach (var member in metadata.EnumerateObject())
        {
            if (member.Name.Equals("chain", StringComparison.OrdinalIgnoreCase) || !IsChainKey(member.Name)) continue;
            var normalized = NormalizeChain(member.Value, document, idMap, depth, 0, ref nodes);
            if (normalized is null) continue;
            (additional ??= []).Add(normalized);
        }
        var error = Property(metadata, "error");
        var request = Property(current, "request");
        return new RemoteDiagnosticResponse
        {
            Transport = transport,
            Status = statusCode,
            Message = Text(Property(response, "message")),
            Service = Text(Property(error, "service")) ?? Text(Property(Property(metadata, "chain"), "service"))
                ?? Text(Property(metadata, ResultMetadataKeys.RemoteService)),
            TraceId = Text(Property(metadata, "traceId")) ?? Text(Property(error, "traceId")),
            Method = Text(Property(request, "method")) ?? Text(Property(legacy, "method")) ?? method,
            Path = Text(Property(request, "path")) ?? Text(Property(legacy, "path")) ?? path,
            Diagnostics = document.Exceptions.Count > 0 || document.Truncated ? document : null,
            Chain = chain,
            AdditionalChains = additional,
            Truncated = document.Truncated || TextTruncated(response, "message")
                || TextTruncated(error, "service", "traceId") || TextTruncated(metadata, "traceId", ResultMetadataKeys.RemoteService)
                || TextTruncated(Property(metadata, "chain"), "service")
                || TextTruncated(request, "method", "path") || TextTruncated(legacy, "method", "path")
        };
    }

    private static void ImportDocument(ExceptionDiagnosticDocument document, JsonElement source,
        Dictionary<string, string> idMap, int depth)
    {
        var entries = Property(source, "exceptions");
        if (entries.ValueKind != JsonValueKind.Array) return;
        var imported = new List<JsonElement>();
        foreach (var item in entries.EnumerateArray().Take(MAX_EXCEPTIONS))
        {
            var oldId = Text(Property(item, "id"));
            if (oldId is null || item.ValueKind != JsonValueKind.Object || idMap.ContainsKey(oldId))
            {
                document.Truncated = true;
                continue;
            }
            var entry = AddImported(document, Text(Property(item, "type")) ?? "Exception",
                RawText(Property(item, "message")) ?? string.Empty, StringArray(Property(item, "stackTrace")), deduplicate: false);
            idMap[oldId] = entry.Id;
            imported.Add(item);
            entry.Truncated |= Property(item, "truncated").ValueKind == JsonValueKind.True;
            document.Truncated |= entry.Truncated;
            var remote = Property(item, "remote");
            if (remote.ValueKind == JsonValueKind.Object)
                entry.Remote = ImportRemote(remote, depth + 1);
        }
        foreach (var item in imported)
        {
            var oldId = Text(Property(item, "id"));
            if (oldId is null || !idMap.TryGetValue(oldId, out var id)) continue;
            var sourceCauses = StringArray(Property(item, "innerExceptionIds"));
            var causes = sourceCauses.Where(idMap.ContainsKey).Select(cause => idMap[cause]).Distinct().ToArray();
            document.Truncated |= sourceCauses.Any(cause => !idMap.ContainsKey(cause));
            if (causes.Length > 0) document.Exceptions.First(entry => entry.Id == id).InnerExceptionIds = causes;
        }
        var root = Text(Property(source, "exceptionId"));
        document.ExceptionId = root is not null && idMap.TryGetValue(root, out var mapped) ? mapped : null;
        document.Truncated |= entries.GetArrayLength() > MAX_EXCEPTIONS
            || Property(source, "truncated").ValueKind == JsonValueKind.True;
        var request = Property(source, "request");
        if (request.ValueKind == JsonValueKind.Object)
        {
            DateTime.TryParse(Text(Property(request, "utcTime")), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utcTime);
            document.Request = new DiagnosticRequest(Text(Property(request, "method")), Text(Property(request, "path")),
                Text(Property(request, "endpoint")), utcTime);
        }
    }

    private static RemoteDiagnosticResponse? ImportRemote(JsonElement source, int depth)
    {
        if (depth >= MAX_REMOTE_DEPTH) return new RemoteDiagnosticResponse { Truncated = true };
        // The structured remote model uses the same diagnostic members as an envelope's metadata.
        var metadata = new JsonObject();
        foreach (var key in new[] { "diagnostics", "chain", "traceId" })
            if (Property(source, key) is { ValueKind: not JsonValueKind.Undefined } value)
                metadata[key] = JsonNode.Parse(value.GetRawText());
        metadata["error"] = new JsonObject { ["service"] = Text(Property(source, "service")) };
        if (Property(source, "additionalChains") is { ValueKind: JsonValueKind.Array } additional)
        {
            var index = 0;
            foreach (var chain in additional.EnumerateArray().Take(MAX_CHAIN_NODES))
                metadata[$"chain_{++index}"] = JsonNode.Parse(chain.GetRawText());
        }
        var envelope = new JsonObject
        {
            ["message"] = Text(Property(source, "message")),
            ["status"] = Integer(Property(source, "status")),
            ["metadata"] = metadata
        };
        var captured = CaptureRemoteCore(JsonSerializer.SerializeToElement(envelope), Text(Property(source, "transport")),
            Integer(Property(source, "status")), depth, Text(Property(source, "method")), Text(Property(source, "path")));
        if (captured is not null) captured.Truncated |= Property(source, "truncated").ValueKind == JsonValueKind.True
            || TextTruncated(source, "transport", "message", "service", "traceId", "method", "path");
        return captured;
    }

    private static string ImportLegacyException(ExceptionDiagnosticDocument document, JsonElement source, int depth)
    {
        var lines = StringArray(Property(source, "stackTrace"));
        return ImportLegacyStack(document, lines, Text(Property(source, "type")),
            Property(source, "message") is { ValueKind: JsonValueKind.String } message ? message.GetString() : null, depth);
    }

    private static string ImportLegacyStack(ExceptionDiagnosticDocument document, string[] lines,
        string? declaredType, string? declaredMessage, int depth)
    {
        // Legacy Exception.ToString() lists inner stacks before outer stacks. Parse the headers and
        // unwind markers instead of copying every inner frame into each propagated exception.
        var pending = new Stack<LegacyException>();
        var root = new LegacyException(declaredType ?? "Exception", declaredMessage ?? "Exception");
        pending.Push(root);
        var sawHeader = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var closesBranch = trimmed.EndsWith("<---", StringComparison.Ordinal);
            if (closesBranch) trimmed = trimmed[..^4].TrimEnd();
            if (trimmed.StartsWith("--- End of inner exception stack trace", StringComparison.Ordinal))
            {
                if (pending.Count > 1) pending.Pop();
                continue;
            }
            var header = trimmed.StartsWith("---> ", StringComparison.Ordinal) ? trimmed[5..] : trimmed;
            if (header.StartsWith("(Inner Exception #", StringComparison.Ordinal) && header.IndexOf(')') is var end && end > 0)
                header = header[(end + 1)..].TrimStart();
            var colon = header.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && !header.StartsWith("at ", StringComparison.Ordinal)
                && !header.AsSpan(0, colon).Contains(' ') && header[..colon].Contains("Exception", StringComparison.Ordinal))
            {
                if (!sawHeader)
                {
                    root.Type = header[..colon];
                    root.Message = header[(colon + 2)..];
                    sawHeader = true;
                }
                else if (trimmed.StartsWith("---> ", StringComparison.Ordinal) && pending.Count < MAX_EXCEPTIONS)
                {
                    var inner = new LegacyException(header[..colon], header[(colon + 2)..]);
                    pending.Peek().Causes.Add(inner);
                    pending.Push(inner);
                }
                if (closesBranch && pending.Count > 1) pending.Pop();
                continue;
            }
            if (trimmed.StartsWith("at ", StringComparison.Ordinal)
                || trimmed.StartsWith("--- End of stack trace from previous location", StringComparison.Ordinal))
                pending.Peek().Frames.Add(line.EndsWith("<---", StringComparison.Ordinal) ? line[..^4].TrimEnd() : line);
            if (closesBranch && pending.Count > 1) pending.Pop();
        }
        return Register(root);

        string Register(LegacyException failure)
        {
            var isRemote = TryLegacyRemote(failure.Type, failure.Message, out var summary, out var remote);
            var entry = AddImported(document, failure.Type, isRemote ? summary : failure.Message, [.. failure.Frames],
                identityMessage: failure.Message);
            if (entry.Id == TRUNCATED_ID) return entry.Id;
            if (failure.Causes.Count > 0) entry.InnerExceptionIds = failure.Causes.Select(Register).Distinct().ToArray();
            // Some legacy outer exceptions contain another complete remote envelope in their message.
            if (isRemote)
            {
                entry.Message = Limit(summary);
                entry.Remote = CaptureRemoteCore(remote, "dapr-actor", null, depth + 1);
            }
            return entry.Id;
        }
    }

    private static JsonNode? NormalizeChain(JsonElement source, ExceptionDiagnosticDocument document,
        Dictionary<string, string> idMap, int remoteDepth, int chainDepth, ref int nodes)
    {
        if (source.ValueKind == JsonValueKind.Undefined || source.ValueKind == JsonValueKind.Null) return null;
        if (chainDepth >= MAX_CHAIN_DEPTH || nodes >= MAX_CHAIN_NODES)
        {
            document.Truncated = true;
            return new JsonObject { ["truncated"] = true };
        }
        if (source.ValueKind == JsonValueKind.Array)
        {
            var items = new JsonArray();
            foreach (var item in source.EnumerateArray().Take(MAX_CHAIN_NODES))
            {
                items.Add(NormalizeChain(item, document, idMap, remoteDepth, chainDepth, ref nodes));
                if (nodes < MAX_CHAIN_NODES) continue;
                document.Truncated = true;
                break;
            }
            document.Truncated |= source.GetArrayLength() > MAX_CHAIN_NODES;
            return items;
        }
        if (source.ValueKind != JsonValueKind.Object) return null;
        nodes++;
        var node = new JsonObject();
        var legacyLines = StringArray(Property(source, "exceptionMessage"));
        string? exceptionId = null;
        if (legacyLines.Length > 0) exceptionId = ImportLegacyStack(document, legacyLines, null, null, remoteDepth);
        else if (Text(Property(source, "exceptionId")) is { } id && idMap.TryGetValue(id, out var mapped)) exceptionId = mapped;
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name.Equals("exceptionMessage", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("exceptionId", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Name.Equals("children", StringComparison.OrdinalIgnoreCase))
                node[property.Name] = NormalizeChain(property.Value, document, idMap, remoteDepth, chainDepth + 1, ref nodes);
            else if (property.Name.Equals("result", StringComparison.OrdinalIgnoreCase) && exceptionId is not null)
                node[property.Name] = "Exception";
            else if (property.Name.Equals("remote", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                node[property.Name] = JsonSerializer.SerializeToNode(ImportRemote(property.Value, remoteDepth + 1), _json);
            else
                node[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        if (exceptionId is not null) node["exceptionId"] = exceptionId;
        return node;
    }

    private static ExceptionDiagnosticEntry AddImported(ExceptionDiagnosticDocument document,
        string type, string message, string[] frames, bool deduplicate = true, string? identityMessage = null)
    {
        var normalized = Frames(frames);
        var signature = type + "\n" + (identityMessage ?? message) + "\n" + string.Join('\n', normalized);
        if (deduplicate && document.ImportedSignatures.TryGetValue(signature, out var id))
            return document.Exceptions.First(entry => entry.Id == id);
        if (document.Exceptions.Count >= MAX_EXCEPTIONS)
        {
            var truncatedId = Truncate(document);
            return document.Exceptions.First(entry => entry.Id == truncatedId);
        }
        var created = new ExceptionDiagnosticEntry
        {
            Id = NextId(document), Type = Limit(type), Message = Limit(message), StackTrace = normalized,
            Truncated = message.Length > MAX_TEXT_LENGTH || type.Length > MAX_TEXT_LENGTH || StackTruncated(frames)
        };
        document.Exceptions.Add(created);
        document.ImportedSignatures.TryAdd(signature, created.Id);
        document.Truncated |= created.Truncated;
        return created;
    }

    private static bool TryLegacyRemote(string type, string message, out string summary, out JsonElement response)
    {
        summary = message;
        response = default;
        if (!type.Equals("Dapr.DaprApiException", StringComparison.Ordinal)) return false;
        const string marker = "error from actor service: (";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || message.Length > MAX_REMOTE_LENGTH) return false;
        var close = message.IndexOf(')', start + marker.Length);
        if (close < 0) return false;
        var body = message[(close + 1)..].Trim();
        try
        {
            using var json = JsonDocument.Parse(body);
            if (Property(json.RootElement, "metadata").ValueKind != JsonValueKind.Object) return false;
            response = json.RootElement.Clone();
            summary = message[..(close + 1)];
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string[] Frames(IEnumerable<string> lines)
    {
        var frames = new List<string>();
        foreach (var line in lines)
        {
            var frame = Limit(line.Trim());
            if (frame.Length == 0) continue;
            if (frames.Count == MAX_STACK_FRAMES) break;
            frames.Add(frame);
        }
        return [.. frames];
    }

    private static string Truncate(ExceptionDiagnosticDocument document)
    {
        document.Truncated = true;
        if (document.Exceptions.All(entry => entry.Id != TRUNCATED_ID))
            document.Exceptions.Add(new ExceptionDiagnosticEntry
            {
                Id = TRUNCATED_ID, Type = "DiagnosticLimit", Message = "Further exception details were omitted.", Truncated = true
            });
        return TRUNCATED_ID;
    }

    private static string NextId(ExceptionDiagnosticDocument document) => $"e{document.Exceptions.Count + 1}";
    private static bool StackTruncated(string[] frames) => frames.Length > MAX_STACK_FRAMES
        || frames.Any(frame => frame.Trim().Length > MAX_TEXT_LENGTH);
    private static string Limit(string value) => value.Length <= MAX_TEXT_LENGTH ? value : value[..MAX_TEXT_LENGTH] + "…";
    private static string? RawText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Text(JsonElement value) => RawText(value) is { } text ? Limit(text) : null;
    private static bool TextTruncated(JsonElement source, params string[] names) => names.Any(name =>
        RawText(Property(source, name)) is { Length: > MAX_TEXT_LENGTH });
    private static int? Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static string[] StringArray(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Take(MAX_STACK_FRAMES * 2).Select(item => item.GetString()!).ToArray() : [];

    private static JsonElement Property(JsonElement source, string name)
    {
        if (source.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in source.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static bool IsRemoteMetadata(string key) => key.ToLowerInvariant() is "exception" or "diagnostics" or "error" or "traceid" or "remoteservice" || IsChainKey(key);
    private static bool IsChainKey(string key)
    {
        if (key.Equals("chain_error", StringComparison.OrdinalIgnoreCase)) return true;
        if (!key.StartsWith("chain", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = key.AsSpan(5);
        if (suffix.IsEmpty) return true;
        if (suffix.StartsWith("_")) suffix = suffix[1..];
        return !suffix.IsEmpty && !suffix.ContainsAnyExceptInRange('0', '9');
    }

    private sealed class LegacyException(string type, string message)
    {
        public string Type { get; set; } = type;
        public string Message { get; set; } = message;
        public List<string> Frames { get; } = [];
        public List<LegacyException> Causes { get; } = [];
    }
}
