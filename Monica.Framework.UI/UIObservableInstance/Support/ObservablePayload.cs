using System.Text.Json;
using Monica.Core.ObservableInstance.Models;

namespace Monica.Framework.UI.UIObservableInstance.Support;

internal static class ObservablePayload
{
    internal static bool IsComplex(ObservableValueSnapshot snapshot)
    {
        if (snapshot.CaptureError is not null) return true;
        if (snapshot.Text is not { } text) return false;
        return text.Length > 64 || text.Contains('\n') || text.Contains('\r') || IsJsonObject(text);
    }

    internal static IEnumerable<PayloadFieldChange> Compare(ObservableValueSnapshot before, ObservableValueSnapshot after)
    {
        if (!TryReadObject(before.Text, out var previous) || !TryReadObject(after.Text, out var current)) yield break;

        foreach (var key in previous.Keys.Union(current.Keys, StringComparer.Ordinal))
        {
            var hadPrevious = previous.TryGetValue(key, out var a);
            var hasCurrent = current.TryGetValue(key, out var b);
            if (hadPrevious && hasCurrent && JsonElement.DeepEquals(a, b)) continue;
            yield return new PayloadFieldChange(key, hadPrevious ? ShortValue(a) : "∅", hasCurrent ? ShortValue(b) : "∅");
        }
    }

    private static string ShortValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static bool IsJsonObject(string text)
    {
        if (!text.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal)) return false;
        return TryReadObject(text, out _);
    }

    private static bool TryReadObject(string? text, out Dictionary<string, JsonElement> fields)
    {
        fields = new(StringComparer.Ordinal);
        if (text is null || !text.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal)) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var item in document.RootElement.EnumerateObject()) fields[item.Name] = item.Value.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}

internal sealed record PayloadFieldChange(string Key, string Before, string After);
