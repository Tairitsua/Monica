using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Utils;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Validates JSON configuration values against Monica schema nodes.
/// </summary>
internal sealed class ConfigurationValueValidationEngine
{
    private static readonly string[] TIME_SPAN_FORMATS =
    [
        "c",
        "g",
        "G",
        @"d\.hh\:mm\:ss",
        @"hh\:mm\:ss",
        @"hh\:mm"
    ];

    /// <summary>
    /// Validates a value against a schema node and returns every discovered issue.
    /// </summary>
    /// <param name="schema">The schema node.</param>
    /// <param name="path">The logical path represented by <paramref name="schema"/>.</param>
    /// <param name="value">The JSON value to validate.</param>
    /// <param name="options">Validation options.</param>
    /// <returns>Validation issues.</returns>
    public IReadOnlyList<ConfigurationValueValidationIssue> Validate(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        ConfigurationValueValidationOptions options)
    {
        var issues = new List<ConfigurationValueValidationIssue>();
        ValidateNodeValue(schema, path, value, options, issues);
        return issues;
    }

    /// <summary>
    /// Validates removing a value from a schema node.
    /// </summary>
    /// <param name="schema">The schema node being removed.</param>
    /// <param name="path">The logical path being removed.</param>
    /// <param name="options">Validation options.</param>
    /// <returns>Validation issues.</returns>
    public IReadOnlyList<ConfigurationValueValidationIssue> ValidateRemoval(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        ConfigurationValueValidationOptions options)
    {
        var issues = new List<ConfigurationValueValidationIssue>();
        ValidateMissing(schema, path, options, issues);
        return issues;
    }

    private static void ValidateNodeValue(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            ValidateNull(schema, path, options, issues);
            return;
        }

        switch (schema.NodeKind)
        {
            case ConfigurationNodeKind.Scalar:
                ValidateScalar(schema, path, value, issues);
                break;
            case ConfigurationNodeKind.Object:
                ValidateObject(schema, path, value, options, issues);
                break;
            case ConfigurationNodeKind.Dictionary:
                ValidateDictionary(schema, path, value, options, issues);
                break;
            case ConfigurationNodeKind.List:
                ValidateList(schema, path, value, options, issues);
                break;
        }
    }

    private static void ValidateObject(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            AddIssue(schema, path, "Expected a JSON object.", value, issues, blocksMaterialization: true, blocksProjection: true);
            return;
        }

        var properties = value.EnumerateObject().ToArray();
        ValidateDuplicateObjectProperties(schema, path, properties, isDictionary: false, issues);

        if (options.RejectUnknownObjectProperties)
        {
            ValidateUnknownObjectProperties(schema, path, properties, issues);
        }

        foreach (var child in schema.Children)
        {
            var childPath = path.Append(new PropertySegment(child.Name));
            if (!TryGetProperty(value, child.Name, out var childValue))
            {
                ValidateMissing(child, childPath, options, issues);
                continue;
            }

            ValidateNodeValue(child, childPath, childValue, options, issues);
        }
    }

    private static void ValidateUnknownObjectProperties(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        IReadOnlyList<JsonProperty> properties,
        List<ConfigurationValueValidationIssue> issues)
    {
        var knownProperties = schema.Children
            .Select(static child => child.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties)
        {
            if (knownProperties.Contains(property.Name))
            {
                continue;
            }

            AddIssue(
                schema,
                path.Append(new PropertySegment(property.Name)),
                $"Property '{property.Name}' is not defined by the current configuration schema.",
                property.Value,
                issues, blocksProjection: true);
        }
    }

    private static void ValidateDictionary(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            AddIssue(schema, path, "Expected a JSON object for dictionary configuration.", value, issues, blocksMaterialization: true, blocksProjection: true);
            return;
        }

        var properties = value.EnumerateObject().ToArray();
        ValidateDuplicateObjectProperties(schema, path, properties, isDictionary: true, issues);
        ValidateContainerRules(schema, path, properties.Length, value, issues);
        if (schema.DictionaryTemplate is not { } template)
        {
            return;
        }

        foreach (var property in properties)
        {
            var propertyPath = path.Append(new DictionaryKeySegment(property.Name));
            if (!ValidateDictionaryKey(template, schema, propertyPath, property, issues))
            {
                continue;
            }

            ValidateNodeValue(
                template.ValueTemplate,
                propertyPath,
                property.Value,
                options,
                issues);
        }
    }

    private static bool ValidateDictionaryKey(
        ConfigurationDictionaryTemplate template,
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonProperty property,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (!ConfigurationDictionaryKeyEscaper.TryValidateForProjection(property.Name, out var projectionProblem))
        {
            AddIssue(schema, path, projectionProblem!, property.Value, issues, blocksMaterialization: true, blocksProjection: true);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(template.KeyRegexPattern)
            && !Regex.IsMatch(property.Name, template.KeyRegexPattern))
        {
            AddIssue(schema, path, "Dictionary key does not match the required pattern.", property.Value, issues);
            return false;
        }

        var keyType = ResolveClrType(template.KeyClrTypeName);
        if (template.KeyKind == ConfigurationValueKind.Enum)
        {
            var numericType = ResolveClrType(template.KeyEnumUnderlyingClrTypeName ?? string.Empty);
            var named = property.Name.Split(',').All(name => template.KeyEnumValues.Any(value =>
                string.Equals(value.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (template.KeyEnumValues.Count > 0 && numericType is not null && numericType != typeof(string)
                && IsConfigurationBinderDictionaryKeyType(numericType)
                && (named || TryConvertInvariantString(numericType, property.Name, allowNull: false))) return true;
            AddIssue(schema, path, "Dictionary enum key cannot be converted using the published portable contract.",
                property.Value, issues, blocksMaterialization: true, blocksProjection: true);
            return false;
        }
        if (keyType is null)
        {
            AddIssue(
                schema,
                path,
                $"Dictionary key CLR type '{template.KeyClrTypeName}' could not be resolved for validation.",
                property.Value,
                issues, blocksMaterialization: true, blocksProjection: true);
            return false;
        }

        if (!IsConfigurationBinderDictionaryKeyType(keyType))
        {
            AddIssue(
                schema,
                path,
                $"Dictionary key type '{keyType.FullName ?? keyType.Name}' is not supported by Microsoft "
                + "ConfigurationBinder. Supported key types are string, enum, and the built-in signed or unsigned "
                + "integer types.",
                property.Value,
                issues, blocksMaterialization: true, blocksProjection: true);
            return false;
        }

        if (!TryConvertInvariantString(keyType, property.Name, allowNull: false))
        {
            AddIssue(
                schema,
                path,
                $"Dictionary key cannot be converted to '{template.KeyClrTypeName}'.",
                property.Value,
                issues, blocksMaterialization: true, blocksProjection: true);
            return false;
        }

        return true;
    }

    private static bool IsConfigurationBinderDictionaryKeyType(Type keyType)
    {
        return keyType == typeof(string)
               || keyType.IsEnum
               || keyType == typeof(sbyte)
               || keyType == typeof(byte)
               || keyType == typeof(short)
               || keyType == typeof(ushort)
               || keyType == typeof(int)
               || keyType == typeof(uint)
               || keyType == typeof(long)
               || keyType == typeof(ulong);
    }

    private static void ValidateList(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            AddIssue(schema, path, "Expected a JSON array for list configuration.", value, issues, blocksMaterialization: true, blocksProjection: true);
            return;
        }

        var items = value.EnumerateArray().ToArray();
        ValidateContainerRules(schema, path, items.Length, value, issues);
        if (schema.ListTemplate is not { } template)
        {
            return;
        }

        ValidateDuplicateListItems(path, template, items, issues);
        var itemPaths = ResolveListItemPaths(path, template, items, issues);
        for (var index = 0; index < items.Length; index++)
        {
            ValidateNodeValue(template.ItemTemplate, itemPaths[index], items[index], options, issues);
        }
    }

    private static void ValidateDuplicateObjectProperties(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        IReadOnlyList<JsonProperty> properties,
        bool isDictionary,
        List<ConfigurationValueValidationIssue> issues)
    {
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (propertyNames.Add(property.Name))
            {
                continue;
            }

            var propertyPath = isDictionary
                ? path.Append(new DictionaryKeySegment(property.Name))
                : path.Append(new PropertySegment(property.Name));
            var propertySchema = isDictionary
                ? schema.DictionaryTemplate?.ValueTemplate
                : schema.Children.FirstOrDefault(child =>
                    string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            AddIssue(
                propertySchema ?? schema,
                propertyPath,
                $"Property '{property.Name}' is duplicated in the same JSON object. Property names are compared case-insensitively.",
                property.Value,
                issues, blocksMaterialization: true, blocksProjection: true);
        }
    }

    private static void ValidateDuplicateListItems(
        LogicalPath path,
        ConfigurationListTemplate template,
        IReadOnlyList<JsonElement> items,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (template.AllowDuplicateItems || template.ItemTemplate.NodeKind != ConfigurationNodeKind.Scalar)
        {
            return;
        }

        var firstIndexByValue = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            if (!TryBuildScalarListItemIdentity(template.ItemTemplate, items[index], out var identity))
            {
                continue;
            }

            if (firstIndexByValue.TryAdd(identity, index))
            {
                continue;
            }

            AddIssue(
                template.ItemTemplate,
                path.Append(new ListIndexSegment(index)),
                $"List item duplicates the value at index {firstIndexByValue[identity]}. This list does not allow duplicate item values.",
                items[index],
                issues);
        }
    }

    private static bool TryBuildScalarListItemIdentity(
        ConfigurationNodeDefinition itemSchema,
        JsonElement item,
        out string identity)
    {
        if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            identity = "null";
            return true;
        }

        try
        {
            identity = $"value:{ConvertScalar(itemSchema, item)}";
            return true;
        }
        catch (ConfigurationValueConversionException)
        {
            identity = string.Empty;
            return false;
        }
    }

    private static IReadOnlyList<LogicalPath> ResolveListItemPaths(
        LogicalPath listPath,
        ConfigurationListTemplate template,
        IReadOnlyList<JsonElement> items,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (!template.SupportsPerItemMutation)
        {
            return Enumerable.Range(0, items.Count)
                .Select(index => listPath.Append(new ListIndexSegment(index)))
                .ToArray();
        }

        var keyPropertyName = template.ItemKeyPropertyName!;
        var keySchema = template.ItemTemplate.Children.FirstOrDefault(child =>
                            string.Equals(child.Name, keyPropertyName, StringComparison.OrdinalIgnoreCase))
                        ?? template.ItemTemplate;
        var firstIndexByItemKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var paths = new LogicalPath[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var indexPath = listPath.Append(new ListIndexSegment(index));
            paths[index] = indexPath;
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var keyProperties = item.EnumerateObject()
                .Where(property => string.Equals(property.Name, keyPropertyName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (keyProperties.Length == 0)
            {
                AddIssue(
                    keySchema,
                    indexPath.Append(new PropertySegment(keyPropertyName)),
                    $"List item key property '{keyPropertyName}' is required because this list supports per-item mutation.",
                    item,
                    issues);
                continue;
            }

            if (keyProperties.Length > 1)
            {
                // Duplicate-property validation reports the ambiguous key. Keep an index path so the item remains identifiable.
                continue;
            }

            var keyProperty = keyProperties[0];
            var itemKey = ReadStringLike(keyProperty.Value);
            if (string.IsNullOrWhiteSpace(itemKey))
            {
                AddIssue(
                    keySchema,
                    indexPath.Append(new PropertySegment(keyPropertyName)),
                    $"List item key property '{keyPropertyName}' must contain a non-empty scalar value because this list supports per-item mutation.",
                    keyProperty.Value,
                    issues);
                continue;
            }

            if (!ConfigurationDictionaryKeyEscaper.TryValidateForProjection(itemKey, out var projectionProblem))
            {
                AddIssue(
                    keySchema,
                    indexPath.Append(new PropertySegment(keyPropertyName)),
                    projectionProblem!,
                    keyProperty.Value,
                    issues);
                continue;
            }

            if (!firstIndexByItemKey.TryAdd(itemKey, index))
            {
                AddIssue(
                    keySchema,
                    indexPath.Append(new PropertySegment(keyPropertyName)),
                    $"List item key duplicates the item at index {firstIndexByItemKey[itemKey]}. Stable item keys must be unique when compared case-insensitively.",
                    keyProperty.Value,
                    issues);
                continue;
            }

            paths[index] = listPath.Append(new ListItemKeySegment(itemKey));
        }

        return paths;
    }

    private static void ValidateContainerRules(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        int itemCount,
        JsonElement value,
        List<ConfigurationValueValidationIssue> issues)
    {
        foreach (var rule in schema.ValidationRules)
        {
            switch (rule)
            {
                case RequiredRule when itemCount == 0:
                    AddIssue(schema, path, "value is required and must contain at least 1 item.", value, issues);
                    return;
                case MinLengthRule minLengthRule when itemCount < minLengthRule.Min:
                    AddIssue(schema, path, MinimumItemCountMessage(minLengthRule.Min), value, issues);
                    return;
                case MaxLengthRule maxLengthRule when itemCount > maxLengthRule.Max:
                    AddIssue(schema, path, MaximumItemCountMessage(maxLengthRule.Max), value, issues);
                    return;
            }
        }
    }

    private static void ValidateScalar(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        JsonElement value,
        List<ConfigurationValueValidationIssue> issues)
    {
        string scalar;
        try
        {
            scalar = ConvertScalar(schema, value);
        }
        catch (ConfigurationValueConversionException ex)
        {
            AddIssue(schema, path, ex.Message, value, issues, blocksMaterialization: true,
                blocksProjection: value.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
            return;
        }

        ValidateScalarRules(schema, path, scalar, value, issues);
    }

    private static string ConvertScalar(
        ConfigurationNodeDefinition schema,
        JsonElement value)
    {
        var scalar = schema.ValueKind switch
        {
            ConfigurationValueKind.Boolean => ConvertBoolean(value),
            ConfigurationValueKind.Integer => ConvertInteger(value),
            ConfigurationValueKind.Decimal => ConvertDecimal(value),
            ConfigurationValueKind.Floating => ConvertFloating(value),
            ConfigurationValueKind.DateTime => ConvertDateTime(value),
            ConfigurationValueKind.TimeSpan => ConvertTimeSpan(value),
            ConfigurationValueKind.Enum => ConvertEnum(schema, value),
            ConfigurationValueKind.Uri => ConvertUri(value),
            ConfigurationValueKind.Json => value.GetRawText(),
            _ => ConvertStringLike(value)
        };

        ValidateExactClrType(schema, value);
        return scalar;
    }

    private static void ValidateExactClrType(
        ConfigurationNodeDefinition schema,
        JsonElement value)
    {
        // JSON nodes are intentionally opaque and have no scalar TypeConverter contract.
        if (schema.ValueKind is ConfigurationValueKind.Json or ConfigurationValueKind.Enum)
        {
            return;
        }

        var declaredType = ResolveClrType(schema.ClrTypeName);
        if (declaredType is null)
        {
            throw ConversionFailed(
                $"Configured CLR type '{schema.ClrTypeName}' could not be resolved for validation.");
        }

        var scalarType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var sourceText = ReadStringLike(value)
                         ?? throw ConversionFailed("Expected a scalar value.");
        if (!TryConvertInvariantString(scalarType, sourceText, schema.IsNullable))
        {
            throw ConversionFailed(
                $"Value cannot be converted to the configured CLR type '{scalarType.FullName ?? scalarType.Name}'.");
        }
    }

    private static Type? ResolveClrType(string clrTypeName)
    {
        // Published identities never authorize assembly loading or custom TypeConverter execution.
        var name = clrTypeName;
        var nullable = name.StartsWith("System.Nullable`1[[", StringComparison.Ordinal);
        if (nullable) name = name["System.Nullable`1[[".Length..];
        var separator = name.IndexOf(',');
        if (separator >= 0) name = name[..separator];
        Type? type = name.Trim() switch
        {
            "System.String" => typeof(string), "System.Boolean" => typeof(bool), "System.Char" => typeof(char),
            "System.SByte" => typeof(sbyte), "System.Byte" => typeof(byte), "System.Int16" => typeof(short),
            "System.UInt16" => typeof(ushort), "System.Int32" => typeof(int), "System.UInt32" => typeof(uint),
            "System.Int64" => typeof(long), "System.UInt64" => typeof(ulong), "System.Single" => typeof(float),
            "System.Double" => typeof(double), "System.Decimal" => typeof(decimal), "System.DateTime" => typeof(DateTime),
            "System.DateTimeOffset" => typeof(DateTimeOffset), "System.TimeSpan" => typeof(TimeSpan),
            "System.Uri" => typeof(Uri), "System.Guid" => typeof(Guid), _ => null
        };
        return nullable && type is { IsValueType: true } ? typeof(Nullable<>).MakeGenericType(type) : type;
    }

    private static bool TryConvertInvariantString(Type targetType, string value, bool allowNull)
    {
        try
        {
            if (!IsFloatingPointValueInRange(targetType, value))
            {
                return false;
            }

            var converter = TypeDescriptor.GetConverter(targetType);
            if (!converter.CanConvertFrom(typeof(string)))
            {
                return false;
            }

            var converted = converter.ConvertFromInvariantString(value);
            return converted is null
                ? allowNull
                : targetType.IsInstanceOfType(converted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsFloatingPointValueInRange(Type targetType, string value)
    {
        // Modern parsers saturate finite overflow to infinity, so distinguish overflow from an explicit infinity value.
        if (targetType == typeof(float))
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                   && (!float.IsInfinity(parsed) || IsExplicitInfinity(value));
        }

        if (targetType == typeof(double))
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                   && (!double.IsInfinity(parsed) || IsExplicitInfinity(value));
        }

        return true;
    }

    private static bool IsExplicitInfinity(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(
                   trimmed,
                   NumberFormatInfo.InvariantInfo.PositiveInfinitySymbol,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   trimmed,
                   $"+{NumberFormatInfo.InvariantInfo.PositiveInfinitySymbol}",
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   trimmed,
                   NumberFormatInfo.InvariantInfo.NegativeInfinitySymbol,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ConvertBoolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? "true" : "false";
        }

        var text = ReadStringLike(value);
        if (bool.TryParse(text, out var parsed))
        {
            return parsed ? "true" : "false";
        }

        throw ConversionFailed("Expected a boolean value.");
    }

    private static string ConvertInteger(JsonElement value)
    {
        var text = ReadStringLike(value);
        if (BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        throw ConversionFailed("Expected an integer value.");
    }

    private static string ConvertDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var text = ReadStringLike(value);
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        throw ConversionFailed("Expected a numeric value.");
    }

    private static string ConvertFloating(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var text = ReadStringLike(value);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        throw ConversionFailed("Expected a floating-point value.");
    }

    private static string ConvertDateTime(JsonElement value)
    {
        var text = ReadStringLike(value);
        if (!string.IsNullOrWhiteSpace(text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.ToString("O", CultureInfo.InvariantCulture);
        }

        throw ConversionFailed("Expected a date/time value.");
    }

    private static string ConvertTimeSpan(JsonElement value)
    {
        var text = ReadStringLike(value);
        if (!string.IsNullOrWhiteSpace(text)
            && (TimeSpan.TryParseExact(text, TIME_SPAN_FORMATS, CultureInfo.InvariantCulture, out var parsed)
                || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out parsed)))
        {
            return parsed.ToString("c", CultureInfo.InvariantCulture);
        }

        throw ConversionFailed("Expected a valid time span value.");
    }

    private static string ConvertEnum(
        ConfigurationNodeDefinition schema,
        JsonElement value)
    {
        var text = ConvertStringLike(value);
        return schema.TryNormalizeEnumDisplayValue(text, out var normalized)
            ? normalized
            : text;
    }

    private static string ConvertUri(JsonElement value)
    {
        var text = ConvertStringLike(value);
        if (string.IsNullOrWhiteSpace(text) || Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out _))
        {
            return text;
        }

        throw ConversionFailed("Expected a URI value.");
    }

    private static string ConvertStringLike(JsonElement value)
    {
        return ReadStringLike(value)
               ?? throw ConversionFailed("Expected a scalar value.");
    }

    private static string? ReadStringLike(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static void ValidateScalarRules(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        string value,
        JsonElement sourceValue,
        List<ConfigurationValueValidationIssue> issues)
    {
        var requiredRule = schema.ValidationRules.OfType<RequiredRule>().FirstOrDefault();
        if (requiredRule is not null && string.IsNullOrWhiteSpace(value))
        {
            AddIssue(schema, path, requiredRule.ErrorMessage ?? "A value is required.", sourceValue, issues);
            return;
        }

        foreach (var rule in schema.ValidationRules)
        {
            switch (rule)
            {
                case AllowedValuesRule allowedValuesRule when !string.IsNullOrWhiteSpace(value)
                                                              && !allowedValuesRule.Values.Contains(value, StringComparer.OrdinalIgnoreCase):
                    AddIssue(schema, path, rule.ErrorMessage ?? $"Value must be one of: {string.Join(", ", allowedValuesRule.Values)}.", sourceValue, issues);
                    return;
                case RegexRule regexRule when !string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value, regexRule.Pattern):
                    AddIssue(schema, path, rule.ErrorMessage ?? "Value does not match the required pattern.", sourceValue, issues);
                    return;
                case RangeRule rangeRule when decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue):
                    if (rangeRule.Min is not null && decimalValue < rangeRule.Min || rangeRule.Max is not null && decimalValue > rangeRule.Max)
                    {
                        AddIssue(schema, path, rule.ErrorMessage ?? "Value is outside the allowed range.", sourceValue, issues);
                        return;
                    }

                    break;
                case MaxLengthRule maxLengthRule when value.Length > maxLengthRule.Max:
                    AddIssue(schema, path, rule.ErrorMessage ?? $"Value must be at most {maxLengthRule.Max} characters.", sourceValue, issues);
                    return;
                case MinLengthRule minLengthRule when value.Length < minLengthRule.Min:
                    AddIssue(schema, path, rule.ErrorMessage ?? $"Value must be at least {minLengthRule.Min} characters.", sourceValue, issues);
                    return;
            }
        }
    }

    private static void ValidateNull(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (!schema.IsNullable)
        {
            issues.Add(new ConfigurationValueValidationIssue
            {
                LogicalPath = path,
                LogicalPaths = [path],
                Node = schema,
                Message = "Explicit null cannot represent a non-nullable configuration value.",
                BlocksMaterialization = true,
                ValidationRules = schema.ValidationRules
            });
        }
        if (BuildMissingProblem(schema, options) is { } problem)
        {
            AddMissingIssue(schema, path, problem, issues);
        }
    }

    private static void ValidateMissing(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        ConfigurationValueValidationOptions options,
        List<ConfigurationValueValidationIssue> issues)
    {
        if (BuildMissingProblem(schema, options) is { } problem)
        {
            AddMissingIssue(schema, path, problem, issues);
        }
    }

    private static string? BuildMissingProblem(
        ConfigurationNodeDefinition schema,
        ConfigurationValueValidationOptions options)
    {
        var minLengthRule = schema.ValidationRules.OfType<MinLengthRule>().FirstOrDefault();
        if (schema.NodeKind is ConfigurationNodeKind.Dictionary or ConfigurationNodeKind.List
            && minLengthRule is not null
            && minLengthRule.Min > 0)
        {
            return MinimumItemCountMessage(minLengthRule.Min);
        }

        if (schema.ValidationRules.OfType<RequiredRule>().Any())
        {
            return "A value is required.";
        }

        if (schema.NodeKind == ConfigurationNodeKind.Scalar
            && options.TreatNonNullableScalarsAsRequired
            && !schema.IsNullable)
        {
            return "A value is required.";
        }

        return null;
    }

    private static string MinimumItemCountMessage(int minimum)
    {
        return minimum == 1
            ? "value is required and must contain at least 1 item."
            : $"value is required and must contain at least {minimum} items.";
    }

    private static string MaximumItemCountMessage(int maximum)
    {
        return maximum == 1
            ? "value must contain at most 1 item."
            : $"value must contain at most {maximum} items.";
    }

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement propertyValue)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                propertyValue = property.Value;
                return true;
            }
        }

        propertyValue = default;
        return false;
    }

    private static void AddMissingIssue(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        string message,
        List<ConfigurationValueValidationIssue> issues)
    {
        issues.Add(new ConfigurationValueValidationIssue
        {
            LogicalPath = path,
            Node = schema,
            Message = message,
            IsMissing = true,
            ValidationRules = schema.ValidationRules
        });
    }

    private static void AddIssue(
        ConfigurationNodeDefinition schema,
        LogicalPath path,
        string message,
        JsonElement value,
        List<ConfigurationValueValidationIssue> issues,
        bool blocksMaterialization = false,
        bool blocksProjection = false)
    {
        issues.Add(new ConfigurationValueValidationIssue
        {
            LogicalPath = path,
            LogicalPaths = [path],
            BlocksMaterialization = blocksMaterialization,
            BlocksProjection = blocksProjection,
            Node = schema,
            Message = message,
            DisplayValue = schema.IsSensitive || schema.NodeKind != ConfigurationNodeKind.Scalar
                || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? null : DisplayValue(schema, value),
            ValidationRules = schema.ValidationRules
        });
    }

    private static string DisplayValue(ConfigurationNodeDefinition schema, JsonElement value)
    {
        var displayValue = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        return ConfigurationRegexTextCodec.NormalizeDisplayValue(schema, displayValue);
    }

    private static ConfigurationValueConversionException ConversionFailed(string message)
    {
        return new ConfigurationValueConversionException(message);
    }

    private sealed class ConfigurationValueConversionException(string message) : Exception(message);
}

/// <summary>
/// Controls schema validation behavior for a specific caller.
/// </summary>
internal sealed record ConfigurationValueValidationOptions
{
    /// <summary>
    /// Gets whether non-nullable scalar nodes should be treated as required when no explicit rule exists.
    /// </summary>
    public bool TreatNonNullableScalarsAsRequired { get; init; }

    /// <summary>
    /// Gets whether object values containing properties absent from the schema should be rejected.
    /// Dictionary keys remain dynamic and are validated against their value template.
    /// </summary>
    public bool RejectUnknownObjectProperties { get; init; }
}

/// <summary>
/// Describes one raw schema validation issue before runtime source metadata is attached.
/// </summary>
internal sealed record ConfigurationValueValidationIssue
{
    internal ConfigurationValidationIssueKind Kind { get; init; } = ConfigurationValidationIssueKind.Schema;
    internal IReadOnlyList<LogicalPath> LogicalPaths { get; init; } = [];
    internal bool IsSensitive { get; init; }
    internal bool BlocksMaterialization { get; init; }
    internal bool BlocksProjection { get; init; }
    /// <summary>
    /// Gets the target logical path.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>
    /// Gets the schema node that failed validation.
    /// </summary>
    public required ConfigurationNodeDefinition Node { get; init; }

    /// <summary>
    /// Gets the validation message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the display-safe invalid value when one exists.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets whether the value is missing.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Gets the rules that constrained the value.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationRule> ValidationRules { get; init; } = [];
}
