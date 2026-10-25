using System.Text.Encodings.Web;
using System.Text.Json;

namespace Monica.UI.Shared.Components.Json;

/// <summary>Retains source and computes JSON projections without coercing numbers or dropping duplicate properties.</summary>
internal sealed class JsonTextPresentation
{
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private JsonTextPresentation(string source, MoJsonValidation validation, string? formatted = null, string? compact = null)
    {
        Source = source;
        Validation = validation;
        Formatted = formatted ?? source;
        Compact = compact ?? source;
    }

    internal string Source { get; }
    internal string Formatted { get; }
    internal string Compact { get; }
    internal MoJsonValidation Validation { get; }

    internal static JsonTextPresentation Create(string? source)
    {
        source ??= string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return new(source, new(false, true));
        try
        {
            using var document = JsonDocument.Parse(source);
            return new(source, new(true, false),
                JsonSerializer.Serialize(document.RootElement, IndentedOptions),
                JsonSerializer.Serialize(document.RootElement, CompactOptions));
        }
        catch (JsonException exception)
        {
            return new(source, new(false, false, exception.Message,
                exception.LineNumber + 1, exception.BytePositionInLine + 1));
        }
    }

    /// <summary>Lexical spans also highlight an incomplete edit. All gaps remain source-faithful plain text.</summary>
    internal static IEnumerable<JsonTextToken> Tokenize(string text)
    {
        var plainStart = 0;
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            string? role = null;
            if (text[position] == '"')
            {
                position++;
                while (position < text.Length && text[position] is not '\r' and not '\n')
                {
                    var character = text[position++];
                    if (character == '\\' && position < text.Length) position++;
                    else if (character == '"') break;
                }
                var next = position;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                role = next < text.Length && text[next] == ':' ? "key" : "string";
            }
            else if (char.IsAsciiDigit(text[position]) || text[position] == '-' && position + 1 < text.Length && char.IsAsciiDigit(text[position + 1]))
            {
                position++;
                while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] is '.' or 'e' or 'E' or '+' or '-')) position++;
                role = "number";
            }
            else if (text.AsSpan(position).StartsWith("true", StringComparison.Ordinal)
                || text.AsSpan(position).StartsWith("null", StringComparison.Ordinal))
            {
                position += 4;
                role = "literal";
            }
            else if (text.AsSpan(position).StartsWith("false", StringComparison.Ordinal))
            {
                position += 5;
                role = "literal";
            }
            else position++;

            if (role is null) continue;
            if (start > plainStart) yield return new(text[plainStart..start], null);
            yield return new(text[start..position], role);
            plainStart = position;
        }
        if (plainStart < text.Length) yield return new(text[plainStart..], null);
    }
}

internal readonly record struct JsonTextToken(string Text, string? Role);
