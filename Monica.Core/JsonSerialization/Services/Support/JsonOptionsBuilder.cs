using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Monica.Core.JsonSerialization.Converters;
using Monica.Modules;

namespace Monica.Core.JsonSerialization.Services.Support;

internal static class JsonOptionsBuilder
{
    public static void ApplyJsonSerializationDefaults(this JsonSerializerOptions options, ModuleJsonSerializationOption extraOption)
    {
        options.Converters.Add(new NullableDateTimeJsonConverter(extraOption.DateTimeFormat));
        options.Converters.Add(new DateTimeJsonConverter(extraOption.DateTimeFormat));
        options.Converters.Add(new NullableGuidJsonConverter());
        options.Converters.Add(new LongToStringJsonConverter());

        // Add our EnumFormatValue converter if enabled
        if (extraOption.EnableEnumFormatValue)
        {
            options.Converters.Add(new EnumFormatValueJsonConverterFactory());
        }

        if (extraOption.EnableGlobalEnumToString)
        {
            options.Converters.Add(new ExcludeTypesJsonConverterFactory(new JsonStringEnumConverter(),
                [.. extraOption.EnumTypeToIgnore ?? []])); // Enables global enum string/int conversion while allowing type exclusions.
            //options.Converters.Add(new JsonStringEnumConverter()); // Enables global enum string/int conversion without exclusions.
        }

        options.DefaultIgnoreCondition = extraOption.DefaultIgnoreCondition;

        // Required members are wire-mandatory: null-omitting ignore conditions must never drop
        // them, or a strict reader would reject the envelope as a malformed contract.
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(static typeInfo =>
            {
                if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
                foreach (var property in typeInfo.Properties)
                    if (property.IsRequired)
                        property.ShouldSerialize = static (_, _) => true;
            });

        if (extraOption.ReferenceHandlerPreserve)
        {
            options.ReferenceHandler = ReferenceHandler.Preserve;
        }

        options.PropertyNameCaseInsensitive = true;

        // Allow numeric values to be read from string tokens.
        //options.NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;

        //options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        //options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = new JsonCamelCaseNamingPolicy();
        options.PropertyNamingPolicy = new JsonCamelCaseNamingPolicy();

        // Allow JSON comments when needed.
        //options.ReadCommentHandling = JsonCommentHandling.Skip;
    }
}
