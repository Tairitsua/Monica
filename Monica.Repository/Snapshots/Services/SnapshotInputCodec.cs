using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Monica.Repository.Snapshots.Services;

internal static class SnapshotInputCodec
{
    public static void Validate(Type type) => Validate(type, []);

    private static void Validate(Type type, HashSet<Type> path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(ValueTuple) || type.IsEnum || type == typeof(string) || type == typeof(bool)
            || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(decimal) || type == typeof(double) || type == typeof(float)
            || type == typeof(Guid) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return;
        if (!path.Add(type) || type == typeof(object) || type.IsInterface || type.IsAbstract
            || typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
            || type.GetFields(BindingFlags.Instance | BindingFlags.Public).Length != 0)
            throw new NotSupportedException($"Snapshot input type '{type}' is not an immutable scalar or value record.");
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        if (properties.Length == 0) throw new NotSupportedException($"Snapshot input '{type}' has no serializable value properties.");
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null
                || property.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))
                || property.IsDefined(typeof(System.Text.Json.Serialization.JsonConverterAttribute))
                || property.SetMethod is { } setter && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                throw new NotSupportedException($"Snapshot input property '{property}' must be a readable immutable value.");
            Validate(property.PropertyType, path);
        }
        if (type.IsDefined(typeof(System.Text.Json.Serialization.JsonConverterAttribute)))
            throw new NotSupportedException("Custom JSON converters cannot define snapshot input identity.");
        path.Remove(type);
    }

    public static string Encode<T>(T input) where T : notnull
    {
        if (typeof(T) == typeof(ValueTuple)) return "{}";
        // Fixed serializer options and ordinal property order avoid culture and delimiter collisions.
        var element = JsonSerializer.SerializeToElement(input);
        var builder = new StringBuilder();
        Write(element, builder);
        if (builder.Length > 8192) throw new ArgumentOutOfRangeException(nameof(input), "Canonical snapshot input must not exceed 8192 characters.");
        return builder.ToString();
    }

    public static T Capture<T>(string canonical) where T : notnull
    {
        var captured = JsonSerializer.Deserialize<T>(canonical)
            ?? throw new NotSupportedException("Snapshot inputs must round-trip as immutable values.");
        if (Encode(captured) != canonical)
            throw new NotSupportedException("Snapshot inputs must round-trip without losing result-affecting properties.");
        return captured;
    }

    private static void Write(JsonElement value, StringBuilder builder)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            builder.Append('{');
            var first = true;
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                Write(property.Value, builder);
            }
            builder.Append('}');
        }
        // Decimal normalization must not round tiny floating-point inputs to zero or erase negative zero.
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            && value.GetDouble() == (double)number
            && !(number == 0 && value.GetRawText().StartsWith('-')))
            builder.Append(number.ToString("G29", CultureInfo.InvariantCulture));
        else builder.Append(value.GetRawText());
    }
}
