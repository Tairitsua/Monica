using System.Text.Json;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Converts CLR values and stored payloads.
/// </summary>
public sealed class ConfigurationStoredValueCodec
{
    /// <summary>
    /// Encodes a CLR value as a JSON stored value.
    /// </summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The stored value.</returns>
    public ConfigurationStoredValue Encode<TValue>(TValue value)
    {
        return ConfigurationStoredValue.FromJson(JsonSerializer.Serialize(value));
    }

    /// <summary>
    /// Converts a stored value into a Microsoft configuration scalar string.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The scalar value.</returns>
    public string? ToConfigurationString(ConfigurationStoredValue value)
    {
        using var document = JsonDocument.Parse(value.Json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString(),
            JsonValueKind.Number => document.RootElement.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => document.RootElement.GetRawText()
        };
    }

    /// <summary>
    /// Converts a stored value into flat Microsoft configuration key/value pairs.
    /// </summary>
    /// <param name="configurationPath">The root configuration path.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>The projected key/value pairs.</returns>
    public IReadOnlyDictionary<string, string?> ToConfigurationValues(string configurationPath, ConfigurationStoredValue value)
    {
        using var document = JsonDocument.Parse(value.Json);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(configurationPath, document.RootElement, values);
        return values;
    }

    private static void Flatten(string path, JsonElement element, IDictionary<string, string?> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!element.EnumerateObject().Any()) values[path] = null;
                foreach (var property in element.EnumerateObject())
                {
                    Flatten($"{path}:{property.Name}", property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten($"{path}:{index}", item, values);
                    index++;
                }
                if (index == 0) values[path] = string.Empty;
                break;
            case JsonValueKind.String:
                values[path] = element.GetString();
                break;
            case JsonValueKind.Number:
                values[path] = element.GetRawText();
                break;
            case JsonValueKind.True:
                values[path] = "true";
                break;
            case JsonValueKind.False:
                values[path] = "false";
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                values[path] = null;
                break;
            default:
                values[path] = element.GetRawText();
                break;
        }
    }
}
