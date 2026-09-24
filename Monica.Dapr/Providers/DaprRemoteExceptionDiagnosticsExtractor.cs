using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Models;

namespace Monica.Dapr.Providers;

/// <summary>
/// Recovers the actor host's envelope from Dapr's textual error transport. The SDK exposes no response
/// body on <see cref="DaprApiException" />, so this adapter recognizes only the actor-service boundary.
/// </summary>
internal sealed class DaprRemoteExceptionDiagnosticsExtractor : IRemoteExceptionDiagnosticsExtractor
{
    private const string ACTOR_RESPONSE_MARKER = "error from actor service: (";
    private const int MAX_RESPONSE_BYTES = 1024 * 1024;

    public bool TryExtract(Exception exception, [NotNullWhen(true)] out RemoteExceptionPayload? payload)
    {
        payload = null;
        if (exception is not DaprApiException || exception.Message.Length > MAX_RESPONSE_BYTES) return false;

        var message = exception.Message;
        var marker = message.IndexOf(ACTOR_RESPONSE_MARKER, StringComparison.Ordinal);
        if (marker < 0) return false;

        var statusStart = marker + ACTOR_RESPONSE_MARKER.Length;
        if (message.Length <= statusStart + 3 || message[statusStart + 3] != ')' ||
            !int.TryParse(message.AsSpan(statusStart, 3), NumberStyles.None, CultureInfo.InvariantCulture,
                out var status) || status is < 400 or > 599)
            return false;

        var bodyStart = statusStart + 4;
        while (bodyStart < message.Length && char.IsWhiteSpace(message[bodyStart])) bodyStart++;
        if (bodyStart == message.Length || message[bodyStart] != '{' ||
            Encoding.UTF8.GetByteCount(message.AsSpan(bodyStart)) > MAX_RESPONSE_BYTES)
            return false;

        try
        {
            // Parse the JSON boundary once. General Unicode or backslash replacement would corrupt
            // literal paths and user data, and must never substitute for actual JSON decoding.
            using var document = JsonDocument.Parse(message.AsMemory(bodyStart), new JsonDocumentOptions
            {
                MaxDepth = 64
            });
            if (!IsFailureEnvelope(document.RootElement, status)) return false;

            payload = new RemoteExceptionPayload(message[..bodyStart].TrimEnd(), document.RootElement.Clone(),
                "dapr-actor", status);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsFailureEnvelope(JsonElement root, int status)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasUniqueMembers(root) ||
            !TryGetUniqueMember(root, "message", out var message) ||
            message.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            !TryGetUniqueMember(root, "metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object || !HasUniqueMembers(metadata))
            return false;

        // Older Monica hosts used "code" for the envelope status. Do not treat arbitrary JSON in a
        // provider message as an exception envelope, or accept conflicting status aliases.
        var hasStatus = TryGetUniqueMember(root, "status", out var remoteStatus);
        var hasCode = TryGetUniqueMember(root, "code", out var legacyStatus);
        var value = hasStatus ? remoteStatus : legacyStatus;
        if (hasStatus == hasCode || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var numericStatus) || numericStatus != status)
            return false;

        return TryGetUniqueMember(metadata, "exception", out var exception) && exception.ValueKind == JsonValueKind.Object ||
               TryGetUniqueMember(metadata, "diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Object ||
               TryGetUniqueMember(metadata, "chain", out var chain) &&
               chain.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
               HasSafeError(metadata);
    }

    private static bool HasSafeError(JsonElement metadata)
    {
        // Production hosts intentionally omit technical diagnostics. Their standard safe error still
        // identifies a remote failure and must not be serialized back into the local exception message.
        return TryGetUniqueMember(metadata, "error", out var error) &&
               error.ValueKind == JsonValueKind.Object && HasUniqueMembers(error) &&
               TryGetUniqueMember(error, "code", out var code) && code.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(code.GetString()) &&
               TryGetUniqueMember(error, "traceId", out var traceId) && traceId.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(traceId.GetString());
    }

    private static bool HasUniqueMembers(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryGetUniqueMember(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }
}
