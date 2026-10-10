using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Materializes complete local objects through the same shape-aware binder used by production options.</summary>
internal sealed class ConfigurationObjectMaterializer(ConfigurationLocalDefinitionRegistry localRegistry)
{
    internal ConfigurationValueProjection Project(ConfigurationDefinition definition, string json)
    {
        using var document = JsonDocument.Parse(json);
        var projectionIssues = new ConfigurationValueValidationEngine().Validate(definition.Root, LogicalPath.Root,
            document.RootElement, new ConfigurationValueValidationOptions()).Where(issue => issue.BlocksProjection).ToArray();
        if (projectionIssues.FirstOrDefault() is { } invalid)
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, invalid.LogicalPath,
                "ambiguous-value-projection", ConfigurationValidationIssueKind.Contract);
        var node = Prune(definition.Root, document.RootElement);
        using var normalized = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return ConfigurationValueProjectionFactory.Create(definition.SectionPath, normalized.RootElement);
    }

    internal object Materialize(ConfigurationDefinition definition, IConfiguration fullRoot)
    {
        var descriptor = localRegistry.GetRequired(definition);
        try
        {
            var instance = Activator.CreateInstance(descriptor.OptionsType)!;
            MonicaConfigurationBinder.Bind(fullRoot, definition.SectionPath, instance);
            return instance;
        }
        catch (ConfigurationValidationExecutionException fault)
        {
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, fault.LogicalPath, fault.Stage, fault.Kind);
        }
        catch (Exception exception)
        {
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, LogicalPath.Root,
                "object-materialization", innerException: exception);
        }
    }

    internal string Snapshot(ConfigurationDefinition definition, object instance)
    {
        var descriptor = localRegistry.GetRequired(definition);
        try
        {
            return SnapshotNode(definition, descriptor.Root, instance, LogicalPath.Root,
                new HashSet<object>(ReferenceEqualityComparer.Instance))?.ToJsonString() ?? "null";
        }
        catch (ConfigurationValidationExecutionException) { throw; }
        catch (Exception exception)
        {
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, LogicalPath.Root,
                "object-snapshot", innerException: exception);
        }
    }

    internal string ReadConfigurationJson(ConfigurationDefinition definition, IConfiguration configuration)
    {
        var view = new ConfigurationShapeView(configuration);
        var node = ReadNode(definition.Root, definition.SectionPath, view, out var present);
        return node?.ToJsonString() ?? (present ? "null" : "{}");
    }

    private static JsonNode? ReadNode(ConfigurationNodeDefinition schema, string path, ConfigurationShapeView view, out bool present)
    {
        present = view.TryGetShape(path, out var shape);
        var section = view.GetSection(path);
        var children = section.GetChildren().ToArray();
        present |= children.Length > 0;
        if (!present) return null;
        if (shape == ConfigurationValueShape.AmbiguousNull && schema.NodeKind != ConfigurationNodeKind.Scalar)
            throw new ConfigurationValidationExecutionException(string.Empty, schema.RelativePath, "ambiguous-container-shape",
                ConfigurationValidationIssueKind.Contract);
        if (shape == ConfigurationValueShape.Null) return null;
        if (schema.NodeKind == ConfigurationNodeKind.Scalar)
        {
            if (children.Length > 0) return new JsonObject();
            var value = section.Value;
            return schema.ValueKind switch
            {
                ConfigurationValueKind.Boolean when bool.TryParse(value, out var parsed) => JsonValue.Create(parsed),
                ConfigurationValueKind.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                ConfigurationValueKind.Decimal when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                ConfigurationValueKind.Floating when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                _ => JsonValue.Create(value)
            };
        }
        if (shape == ConfigurationValueShape.Scalar && section.Value is { Length: > 0 } invalidScalar) return JsonValue.Create(invalidScalar);
        if (schema.ListTemplate is { } list)
        {
            if (shape == ConfigurationValueShape.Object) return new JsonObject();
            var array = new JsonArray();
            foreach (var child in children) array.Add(ReadNode(list.ItemTemplate, child.Path, view, out _));
            return array;
        }
        var result = new JsonObject();
        if (shape == ConfigurationValueShape.List) return new JsonArray();
        if (schema.DictionaryTemplate is { } dictionary)
        {
            foreach (var child in children) result[child.Key] = ReadNode(dictionary.ValueTemplate, child.Path, view, out _);
        }
        else
        {
            foreach (var child in schema.Children)
            {
                var value = ReadNode(child, $"{path}:{child.Name}", view, out var childPresent);
                if (childPresent) result[child.Name] = value;
            }
        }
        return result;
    }

    private static JsonNode? SnapshotNode(ConfigurationDefinition definition, ConfigurationObjectNodeDescriptor node,
        object? value, LogicalPath path, ISet<object> ancestors)
    {
        if (value is null) return null;
        var actualType = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
        if (node.Schema.NodeKind == ConfigurationNodeKind.Scalar)
        {
            try
            {
                if (actualType.IsEnum) return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
                return JsonSerializer.SerializeToNode(value, actualType);
            }
            catch (Exception exception)
            {
                throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path,
                    "object-snapshot", innerException: exception);
            }
        }
        if (node.Schema.NodeKind == ConfigurationNodeKind.Object && value.GetType() != actualType)
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path, "undeclared-object-shape",
                ConfigurationValidationIssueKind.Contract);
        if (!ancestors.Add(value))
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path, "recursive-object-value",
                ConfigurationValidationIssueKind.Contract);
        try
        {
            if (node.Schema.NodeKind == ConfigurationNodeKind.Object)
            {
                var result = new JsonObject();
                foreach (var property in node.Properties)
                {
                    var childPath = path.Append(new PropertySegment(property.Node.Schema.Name));
                    object? child;
                    try { child = property.Property.GetValue(value); }
                    catch (Exception exception)
                    {
                        throw new ConfigurationValidationExecutionException(definition.DefinitionKey, childPath,
                            "property-read", innerException: exception);
                    }
                    result[property.Node.Schema.Name] = SnapshotNode(definition, property.Node, child, childPath, ancestors);
                }
                return result;
            }
            if (node.DictionaryValue is { } dictionary)
            {
                var result = new JsonObject();
                foreach (var (key, item) in ReadDictionaryEntries(definition, value, path, "object-snapshot"))
                    result[key] = SnapshotNode(definition, dictionary, item, path.Append(new DictionaryKeySegment(key)), ancestors);
                return result;
            }
            if (node.ListItem is { } list && value is IEnumerable enumerable)
            {
                var result = new JsonArray();
                var index = 0;
                foreach (var item in ReadListItems(definition, enumerable, path, "object-snapshot"))
                    result.Add(SnapshotNode(definition, list, item, path.Append(new ListIndexSegment(index++)), ancestors));
                return result;
            }
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path, "unsupported-object-shape",
                ConfigurationValidationIssueKind.Contract);
        }
        finally { ancestors.Remove(value); }
    }

    internal static (string Key, object? Value)[] ReadDictionaryEntries(ConfigurationDefinition definition,
        object value, LogicalPath path, string stage)
    {
        try { return EnumerateDictionary(value).ToArray(); }
        catch (Exception exception)
        {
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path, stage, innerException: exception);
        }
    }

    internal static object?[] ReadListItems(ConfigurationDefinition definition, IEnumerable value, LogicalPath path, string stage)
    {
        try { return value.Cast<object?>().ToArray(); }
        catch (Exception exception)
        {
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, path, stage, innerException: exception);
        }
    }

    private static IEnumerable<(string Key, object? Value)> EnumerateDictionary(object value)
    {
        foreach (var entry in (IEnumerable)value)
        {
            if (entry is DictionaryEntry dictionary)
                yield return (Convert.ToString(dictionary.Key, CultureInfo.InvariantCulture) ?? string.Empty, dictionary.Value);
            else if (entry is not null)
                yield return (Convert.ToString(entry.GetType().GetProperty("Key")!.GetValue(entry), CultureInfo.InvariantCulture) ?? string.Empty,
                    entry.GetType().GetProperty("Value")!.GetValue(entry));
        }
    }

    private static JsonNode? Prune(ConfigurationNodeDefinition schema, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (schema.NodeKind == ConfigurationNodeKind.Object && value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                var child = schema.Children.FirstOrDefault(child => string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (child is not null) result[child.Name] = Prune(child, property.Value);
            }
            return result;
        }
        if (schema.DictionaryTemplate is { } dictionary && value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in value.EnumerateObject()) result[property.Name] = Prune(dictionary.ValueTemplate, property.Value);
            return result;
        }
        if (schema.ListTemplate is { } list && value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in value.EnumerateArray()) result.Add(Prune(list.ItemTemplate, item));
            return result;
        }
        return JsonNode.Parse(value.GetRawText());
    }
}
