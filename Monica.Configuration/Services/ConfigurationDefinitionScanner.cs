using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Annotations;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>
/// Reflection scanner that converts CLR options types into schema definitions.
/// </summary>
internal sealed class ConfigurationDefinitionScanner(
    ConfigurationSchemaHasher hasher,
    ConfigurationSectionPathConvention sectionPathConvention = ConfigurationSectionPathConvention.ShortTypeName)
{
    /// <summary>
    /// Creates the persisted schema definition for a registered configuration options type.
    /// </summary>
    /// <param name="optionsType">The marked options type to inspect.</param>
    /// <returns>The discovered configuration definition.</returns>
    public ConfigurationDefinition Scan(Type optionsType)
    {
        var attribute = optionsType.GetCustomAttribute<ConfigurationAttribute>()
            ?? throw new InvalidOperationException($"Type '{optionsType.FullName}' is not marked with {nameof(ConfigurationAttribute)}.");

        var definitionKey = attribute.DefinitionKey ?? optionsType.FullName ?? optionsType.Name;
        ConfigurationLocalDefinitionDescriptor.EnsureRootConstructible(optionsType, definitionKey);
        var sectionPath = ConfigurationSectionPathResolver.Resolve(optionsType, attribute, sectionPathConvention);
        var root = ScanNode(
            optionsType,
            optionsType.Name,
            LogicalPath.Root,
            sectionPath,
            attribute.ReloadBehavior,
            new SchemaTraversalContext()) with { IsNullable = false };
        var reloadBehavior = attribute.ReloadBehavior == ConfigurationReloadBehavior.Inherit
            ? ConfigurationReloadBehavior.Unknown
            : attribute.ReloadBehavior;

        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = sectionPath,
            DisplayName = attribute.DisplayName ?? optionsType.Name,
            Description = attribute.Description,
            ClrTypeName = optionsType.AssemblyQualifiedName ?? optionsType.FullName ?? optionsType.Name,
            FromProject = ResolveFromProject(optionsType),
            Category = attribute.Category,
            ReloadBehavior = reloadBehavior,
            ReloadBehaviorObservationKind = reloadBehavior is ConfigurationReloadBehavior.OnlineReloadable
                or ConfigurationReloadBehavior.RequiresRestart
                or ConfigurationReloadBehavior.StaticAfterStartup
                    ? ConfigurationReloadBehaviorObservationKind.Declared
                    : ConfigurationReloadBehaviorObservationKind.Unresolved,
            Root = root,
            ValidationContract = ConfigurationLocalDefinitionDescriptor.CreateContract(optionsType, root),
            SchemaHash = hasher.ComputeHash(definitionKey, sectionPath, root)
        };
    }

    private static string ResolveFromProject(Type optionsType)
    {
        return optionsType.Assembly.GetName().Name
               ?? optionsType.Namespace
               ?? optionsType.Name;
    }

    private static ConfigurationNodeDefinition ScanNode(
        Type type,
        string name,
        LogicalPath path,
        string configurationPath,
        ConfigurationReloadBehavior inheritedReloadBehavior,
        SchemaTraversalContext traversal)
    {
        var nodeKind = GetNodeKind(type);
        var entered = traversal.Enter(type, nodeKind, path);
        try
        {
            var option = type.GetCustomAttribute<OptionSettingAttribute>();
            var valueKind = nodeKind == ConfigurationNodeKind.Scalar ? GetValueKind(type) : (ConfigurationValueKind?)null;
            var children = nodeKind == ConfigurationNodeKind.Object
                ? ScanObjectChildren(Nullable.GetUnderlyingType(type) ?? type, path, configurationPath, inheritedReloadBehavior, traversal)
                : [];
            var textSemantic = ResolveTextSemantic(option, nodeKind, valueKind, type.FullName ?? type.Name);
            var editorHint = ResolveEditorHint(option);

            return new ConfigurationNodeDefinition
            {
                NodeKey = option?.NodeKey ?? path.ToCanonicalString(),
                Name = name,
                DisplayName = option?.DisplayName,
                Description = option?.Description,
                RelativePath = path,
                ConfigurationPath = configurationPath,
                ClrTypeName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
                NodeKind = nodeKind,
                ValueKind = valueKind,
                IsNullable = IsNullable(type),
                IsSensitive = option?.IsSensitive is true,
                TextSemantic = textSemantic,
                EditorHint = editorHint,
                ReloadBehavior = ResolveReloadBehavior(option),
                DictionaryTemplate = nodeKind == ConfigurationNodeKind.Dictionary
                    ? BuildDictionaryTemplate(type, path, configurationPath, inheritedReloadBehavior, traversal)
                    : null,
                ListTemplate = nodeKind == ConfigurationNodeKind.List
                    ? BuildListTemplate(type, path, configurationPath, inheritedReloadBehavior, option, traversal)
                    : null,
                Children = children,
                ValidationRules = GetValidationRules(type, type.GetCustomAttributes<ValidationAttribute>()),
                EnumValues = GetEnumValues(type)
            };
        }
        finally
        {
            if (entered)
            {
                traversal.Exit(type);
            }
        }
    }

    private static IReadOnlyList<ConfigurationNodeDefinition> ScanObjectChildren(
        Type type,
        LogicalPath parentPath,
        string parentConfigurationPath,
        ConfigurationReloadBehavior inheritedReloadBehavior,
        SchemaTraversalContext traversal)
    {
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .ToArray();
        if (properties.GroupBy(GetConfigurationPropertyName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ConfigurationValidationExecutionException(type.FullName ?? type.Name, parentPath,
                "duplicate-configuration-alias", ConfigurationValidationIssueKind.Contract);
        if (properties.Any(property => string.IsNullOrWhiteSpace(GetConfigurationPropertyName(property))
            || GetConfigurationPropertyName(property).Contains(':')))
            throw new ConfigurationValidationExecutionException(type.FullName ?? type.Name, parentPath,
                "invalid-configuration-alias", ConfigurationValidationIssueKind.Contract);
        return properties
            .Select(property =>
            {
                var propertyName = GetConfigurationPropertyName(property);
                var childPath = parentPath.Append(new PropertySegment(propertyName));
                var childConfigurationPath = string.IsNullOrWhiteSpace(parentConfigurationPath)
                    ? propertyName
                    : $"{parentConfigurationPath}:{propertyName}";
                var option = property.GetCustomAttribute<OptionSettingAttribute>();
                var propertyType = property.PropertyType;
                return ScanPropertyNode(
                    property,
                    option,
                    propertyType,
                    propertyName,
                    childPath,
                    childConfigurationPath,
                    inheritedReloadBehavior,
                    traversal);
            })
            .ToArray();
    }

    private static ConfigurationNodeDefinition ScanPropertyNode(
        PropertyInfo property,
        OptionSettingAttribute? option,
        Type propertyType,
        string nodeName,
        LogicalPath path,
        string configurationPath,
        ConfigurationReloadBehavior inheritedReloadBehavior,
        SchemaTraversalContext traversal)
    {
        var nodeKind = GetNodeKind(propertyType);
        var entered = traversal.Enter(propertyType, nodeKind, path);
        try
        {
            var valueKind = nodeKind == ConfigurationNodeKind.Scalar ? GetValueKind(propertyType) : (ConfigurationValueKind?)null;
            var children = nodeKind == ConfigurationNodeKind.Object
                ? ScanObjectChildren(Nullable.GetUnderlyingType(propertyType) ?? propertyType, path, configurationPath, inheritedReloadBehavior, traversal)
                : [];
            var textSemantic = ResolveTextSemantic(
                option,
                nodeKind,
                valueKind,
                $"{property.DeclaringType?.FullName ?? property.DeclaringType?.Name}.{property.Name}");
            var editorHint = ResolveEditorHint(option);

            return new ConfigurationNodeDefinition
            {
                NodeKey = option?.NodeKey ?? path.ToCanonicalString(),
                Name = nodeName,
                DisplayName = option?.DisplayName,
                Description = option?.Description,
                RelativePath = path,
                ConfigurationPath = configurationPath,
                ClrTypeName = propertyType.AssemblyQualifiedName ?? propertyType.FullName ?? propertyType.Name,
                NodeKind = nodeKind,
                ValueKind = valueKind,
                IsNullable = IsNullable(property),
                IsSensitive = option?.IsSensitive is true,
                TextSemantic = textSemantic,
                EditorHint = editorHint,
                ReloadBehavior = ResolveReloadBehavior(option),
                DictionaryTemplate = nodeKind == ConfigurationNodeKind.Dictionary
                    ? BuildDictionaryTemplate(propertyType, path, configurationPath, inheritedReloadBehavior, traversal)
                    : null,
                ListTemplate = nodeKind == ConfigurationNodeKind.List
                    ? BuildListTemplate(propertyType, path, configurationPath, inheritedReloadBehavior, option, traversal)
                    : null,
                Children = children,
                ValidationRules = GetValidationRules(propertyType, property.GetCustomAttributes<ValidationAttribute>()),
                EnumValues = GetEnumValues(propertyType)
            };
        }
        finally
        {
            if (entered)
            {
                traversal.Exit(propertyType);
            }
        }
    }

    private static ConfigurationDictionaryTemplate BuildDictionaryTemplate(
        Type dictionaryType,
        LogicalPath dictionaryPath,
        string configurationPath,
        ConfigurationReloadBehavior inheritedReloadBehavior,
        SchemaTraversalContext traversal)
    {
        var keyType = typeof(string);
        var valueType = typeof(object);
        var dictionaryInterface = GetClosedGenericInterface(dictionaryType, typeof(IDictionary<,>));
        if (dictionaryInterface is not null)
        {
            var arguments = dictionaryInterface.GetGenericArguments();
            keyType = arguments[0];
            valueType = arguments[1];
        }

        return new ConfigurationDictionaryTemplate
        {
            KeyClrTypeName = keyType.AssemblyQualifiedName ?? keyType.FullName ?? keyType.Name,
            KeyKind = GetValueKind(keyType),
            KeyEnumValues = GetEnumValues(keyType),
            KeyEnumUnderlyingClrTypeName = keyType.IsEnum ? Enum.GetUnderlyingType(keyType).FullName : null,
            ValueTemplate = ScanNode(
                valueType,
                "Value",
                dictionaryPath.Append(new DictionaryKeySegment("*")),
                string.IsNullOrWhiteSpace(configurationPath) ? "*" : $"{configurationPath}:*",
                inheritedReloadBehavior,
                traversal)
        };
    }

    private static ConfigurationListTemplate BuildListTemplate(
        Type listType,
        LogicalPath listPath,
        string configurationPath,
        ConfigurationReloadBehavior inheritedReloadBehavior,
        OptionSettingAttribute? option,
        SchemaTraversalContext traversal)
    {
        var itemType = GetEnumerableItemType(listType) ?? typeof(object);
        return new ConfigurationListTemplate
        {
            AllowDuplicateItems = option?.AllowDuplicateListItems is true,
            ItemKeyPropertyName = ResolveListItemKeyPropertyName(itemType),
            ItemTemplate = ScanNode(
                itemType,
                "Item",
                listPath.Append(new ListItemKeySegment("*")),
                string.IsNullOrWhiteSpace(configurationPath) ? "*" : $"{configurationPath}:*",
                inheritedReloadBehavior,
                traversal)
        };
    }

    private static string? ResolveListItemKeyPropertyName(Type itemType)
    {
        var keyProperties = itemType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<OptionSettingAttribute>()?.IsListItemKey is true)
            .ToArray();

        if (keyProperties.Length == 0)
        {
            return null;
        }

        if (keyProperties.Length > 1)
        {
            var propertyNames = string.Join(", ", keyProperties.Select(property => property.Name));
            throw new InvalidOperationException(
                $"List item type '{itemType.FullName}' declares multiple list item key properties: {propertyNames}.");
        }

        var keyProperty = keyProperties[0];
        if (keyProperty.GetMethod is null
            || !keyProperty.GetMethod.IsPublic
            || keyProperty.GetIndexParameters().Length > 0
            || GetNodeKind(keyProperty.PropertyType) != ConfigurationNodeKind.Scalar)
        {
            throw new InvalidOperationException(
                $"List item key property '{itemType.FullName}.{keyProperty.Name}' must be a public scalar property.");
        }

        return GetConfigurationPropertyName(keyProperty);
    }

    private static ConfigurationNodeKind GetNodeKind(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(actual))
        {
            return ConfigurationNodeKind.Scalar;
        }

        if (actual != typeof(string) && (typeof(IDictionary).IsAssignableFrom(actual)
            || GetClosedGenericInterface(actual, typeof(IDictionary<,>)) is not null))
        {
            return ConfigurationNodeKind.Dictionary;
        }

        if (actual != typeof(string) && typeof(IEnumerable).IsAssignableFrom(actual))
        {
            return ConfigurationNodeKind.List;
        }

        return ConfigurationNodeKind.Object;
    }

    private static Type? GetClosedGenericInterface(Type type, Type genericInterfaceDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericInterfaceDefinition)
        {
            return type;
        }

        return type.GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType
                                         && candidate.GetGenericTypeDefinition() == genericInterfaceDefinition);
    }

    private static Type? GetEnumerableItemType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerableInterface = GetClosedGenericInterface(type, typeof(IEnumerable<>));
        return enumerableInterface?.GetGenericArguments()[0];
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive
               || type.IsEnum
               || type == typeof(string)
               || type == typeof(decimal)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(TimeSpan)
               || type == typeof(Uri)
               || type == typeof(Guid);
    }

    private static ConfigurationValueKind GetValueKind(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual == typeof(bool))
        {
            return ConfigurationValueKind.Boolean;
        }

        if (actual.IsEnum)
        {
            return ConfigurationValueKind.Enum;
        }

        if (actual == typeof(decimal))
        {
            return ConfigurationValueKind.Decimal;
        }

        if (actual == typeof(float) || actual == typeof(double))
        {
            return ConfigurationValueKind.Floating;
        }

        if (actual == typeof(DateTime) || actual == typeof(DateTimeOffset))
        {
            return ConfigurationValueKind.DateTime;
        }

        if (actual == typeof(TimeSpan))
        {
            return ConfigurationValueKind.TimeSpan;
        }

        if (actual == typeof(Uri))
        {
            return ConfigurationValueKind.Uri;
        }

        if (!IsScalar(actual))
        {
            return ConfigurationValueKind.Json;
        }

        if (actual == typeof(byte)
            || actual == typeof(short)
            || actual == typeof(int)
            || actual == typeof(long)
            || actual == typeof(sbyte)
            || actual == typeof(ushort)
            || actual == typeof(uint)
            || actual == typeof(ulong))
        {
            return ConfigurationValueKind.Integer;
        }

        return ConfigurationValueKind.String;
    }

    private static string GetConfigurationPropertyName(PropertyInfo property)
    {
        return property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name;
    }

    private static bool IsNullable(Type type)
    {
        return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }

    private static bool IsNullable(PropertyInfo property)
    {
        return IsNullable(property.PropertyType);
    }

    private static IReadOnlyList<ConfigurationValidationRule> GetValidationRules(
        Type nodeType,
        IEnumerable<ValidationAttribute> attributes)
    {
        var rules = new List<ConfigurationValidationRule>();
        foreach (var attribute in attributes)
        {
            var supported = attribute.GetType() == typeof(RequiredAttribute)
                || attribute.GetType() == typeof(RangeAttribute)
                || attribute.GetType() == typeof(RegularExpressionAttribute)
                || attribute.GetType() == typeof(MaxLengthAttribute)
                || attribute.GetType() == typeof(MinLengthAttribute)
                || attribute.GetType() == typeof(StringLengthAttribute);
            if (!supported)
            {
                throw new ConfigurationValidationExecutionException(
                    nodeType.FullName ?? nodeType.Name, LogicalPath.Root, "unsupported-validation-attribute",
                    ConfigurationValidationIssueKind.Contract);
            }

            var rule = attribute switch
            {
                RequiredAttribute required => new RequiredRule { ErrorMessage = required.ErrorMessage },
                RangeAttribute range => new RangeRule(ToDecimal(range.Minimum), ToDecimal(range.Maximum)) { ErrorMessage = range.ErrorMessage },
                RegularExpressionAttribute regex => new RegexRule(ConfigurationRegexTextCodec.NormalizePattern(regex.Pattern)) { ErrorMessage = regex.ErrorMessage },
                MaxLengthAttribute max => new MaxLengthRule(max.Length) { ErrorMessage = max.ErrorMessage },
                MinLengthAttribute min => new MinLengthRule(min.Length) { ErrorMessage = min.ErrorMessage },
                StringLengthAttribute length => new MaxLengthRule(length.MaximumLength) { ErrorMessage = length.ErrorMessage },
                _ => (ConfigurationValidationRule?)null
            };
            if (rule is not null) rules.Add(rule);
            if (attribute is StringLengthAttribute { MinimumLength: > 0 } minimum)
            {
                rules.Add(new MinLengthRule(minimum.MinimumLength) { ErrorMessage = minimum.ErrorMessage });
            }
        }

        var actual = Nullable.GetUnderlyingType(nodeType) ?? nodeType;
        if (actual.IsEnum && !rules.OfType<AllowedValuesRule>().Any())
        {
            rules.Add(new AllowedValuesRule(Enum.GetNames(actual)));
        }

        return rules;
    }

    private static IReadOnlyList<ConfigurationEnumValue> GetEnumValues(Type nodeType)
    {
        var actual = Nullable.GetUnderlyingType(nodeType) ?? nodeType;
        if (!actual.IsEnum)
        {
            return [];
        }

        return Enum.GetNames(actual)
            .Select(name => new ConfigurationEnumValue
            {
                Name = name,
                Value = ToInvariantEnumNumber(Enum.Parse(actual, name))
            })
            .ToArray();
    }

    private static string ToInvariantEnumNumber(object enumValue)
    {
        var underlyingType = Enum.GetUnderlyingType(enumValue.GetType());
        var numericValue = Convert.ChangeType(enumValue, underlyingType, CultureInfo.InvariantCulture);
        return Convert.ToString(numericValue, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static decimal? ToDecimal(object? value)
    {
        if (value is null)
        {
            return null;
        }

        // RangeAttribute accepts floating-point bounds, while configuration validation stores
        // numeric limits as decimals. Treat limits beyond decimal's representable range as
        // unbounded instead of clamping to decimal boundary constants: boundary values are not
        // JSON round-trip safe (JavaScript number re-serialization overflows decimal) and
        // misrepresent developer intent as a concrete finite bound.
        if (value is double doubleValue)
        {
            if (doubleValue >= (double)decimal.MaxValue || doubleValue <= (double)decimal.MinValue)
            {
                return null;
            }
        }
        else if (value is float floatValue)
        {
            if (floatValue >= (float)decimal.MaxValue || floatValue <= (float)decimal.MinValue)
            {
                return null;
            }
        }

        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static ConfigurationReloadBehavior? ResolveReloadBehavior(OptionSettingAttribute? option)
    {
        return option is null || option.ReloadBehavior == ConfigurationReloadBehavior.Inherit
            ? null
            : option.ReloadBehavior;
    }

    private static ConfigurationTextSemantic ResolveTextSemantic(
        OptionSettingAttribute? option,
        ConfigurationNodeKind nodeKind,
        ConfigurationValueKind? valueKind,
        string nodeLabel)
    {
        var textSemantic = option?.TextSemantic ?? ConfigurationTextSemantic.PlainText;
        if (textSemantic == ConfigurationTextSemantic.PlainText)
        {
            return ConfigurationTextSemantic.PlainText;
        }

        if (textSemantic == ConfigurationTextSemantic.RegexPattern
            && nodeKind == ConfigurationNodeKind.Scalar
            && valueKind == ConfigurationValueKind.String)
        {
            return textSemantic;
        }

        throw new InvalidOperationException(
            $"{nameof(OptionSettingAttribute.TextSemantic)}.{textSemantic} can only be used on scalar string configuration nodes. Node '{nodeLabel}' is {nodeKind}/{valueKind?.ToString() ?? "None"}.");
    }

    private static string? ResolveEditorHint(OptionSettingAttribute? option)
    {
        // Any node kind may carry a hint; the framework never interprets it and business UIs
        // decide how (or whether) to render a dedicated editor for each combination.
        var editorHint = option?.EditorHint;
        return string.IsNullOrWhiteSpace(editorHint) ? null : editorHint;
    }

    private sealed class SchemaTraversalContext
    {
        private readonly HashSet<Type> _activeTypes = [];
        private readonly List<SchemaTraversalFrame> _ancestors = [];

        public bool Enter(Type type, ConfigurationNodeKind nodeKind, LogicalPath path)
        {
            var actualType = Nullable.GetUnderlyingType(type) ?? type;
            if (path.Depth > ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH)
            {
                throw new InvalidOperationException(
                    $"Configuration schema exceeds the maximum logical depth of "
                    + $"{ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH} at path '{FormatPath(path)}'. "
                    + $"CLR type chain: {FormatTypeChain(_ancestors.Append(new SchemaTraversalFrame(actualType, path)))}.");
            }

            if (nodeKind == ConfigurationNodeKind.Scalar)
            {
                return false;
            }

            if (!_activeTypes.Add(actualType))
            {
                var cycleStart = _ancestors.FindIndex(frame => frame.Type == actualType);
                var cycle = _ancestors
                    .Skip(cycleStart)
                    .Append(new SchemaTraversalFrame(actualType, path));
                throw new InvalidOperationException(
                    $"Recursive configuration schema detected at logical path '{FormatPath(path)}'. "
                    + $"CLR type chain: {FormatTypeChain(cycle)}.");
            }

            _ancestors.Add(new SchemaTraversalFrame(actualType, path));
            return true;
        }

        public void Exit(Type type)
        {
            var actualType = Nullable.GetUnderlyingType(type) ?? type;
            _ancestors.RemoveAt(_ancestors.Count - 1);
            _activeTypes.Remove(actualType);
        }

        private static string FormatTypeChain(IEnumerable<SchemaTraversalFrame> frames)
        {
            return string.Join(
                " -> ",
                frames.Select(frame => $"'{frame.Type.FullName ?? frame.Type.Name}' at '{FormatPath(frame.Path)}'"));
        }

        private static string FormatPath(LogicalPath path)
        {
            return path.Depth == 0 ? "<root>" : path.ToCanonicalString();
        }
    }

    private readonly record struct SchemaTraversalFrame(Type Type, LogicalPath Path);
}
