using System.Text.Json;
using System.Text.Json.Nodes;
using Monica.Configuration.Models;
using Monica.Configuration.UI.State;

namespace Monica.Configuration.UI.Support;

internal static class ConfigurationPendingValueDocumentBuilder
{
    public static JsonNode Build(
        ConfigurationNodeDefinition scopeNode,
        string definitionKey,
        string? effectiveJson,
        IReadOnlyList<PendingChange> pendingChanges)
    {
        var root = ParseJson(effectiveJson) ?? CreateDefaultJsonFor(scopeNode);
        return ApplyPendingChanges(scopeNode, definitionKey, scopeNode.RelativePath, root, pendingChanges);
    }

    public static JsonNode ApplyPendingChanges(
        ConfigurationNodeDefinition scopeNode,
        string definitionKey,
        LogicalPath scopePath,
        JsonNode root,
        IReadOnlyList<PendingChange> pendingChanges)
    {
        var scopedChanges = pendingChanges
            .Where(change => IsInScope(change, definitionKey, scopePath))
            .OrderBy(change => change.LogicalPath.ToCanonicalString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var change in scopedChanges)
        {
            root = ApplyPendingChange(scopeNode, scopePath, root, change);
        }

        return root;
    }

    /// <summary>Applies explicit staged values only when no unknown parent or fallback value must be invented.</summary>
    public static bool TryApplyKnownPendingChanges(
        ConfigurationNodeDefinition scopeNode,
        string definitionKey,
        JsonNode? baseline,
        IReadOnlyList<PendingChange> pendingChanges,
        out JsonNode? result)
    {
        result = baseline?.DeepClone();
        foreach (var change in pendingChanges.Where(change => IsInScope(change, definitionKey, scopeNode.RelativePath))
                     .OrderBy(change => change.LogicalPath.ToCanonicalString(), StringComparer.Ordinal))
        {
            if (change.LogicalPath.Equals(scopeNode.RelativePath))
            {
                if (change.MutationKind == ConfigurationMutationKind.Remove)
                    return false;
                result = JsonNode.Parse(change.NewValue.Json);
                continue;
            }

            if (result is null)
                return false;
            var segments = change.LogicalPath.Segments.Skip(scopeNode.RelativePath.Depth).ToArray();
            var parent = result;
            var parentSchema = scopeNode;
            foreach (var segment in segments.SkipLast(1))
            {
                var child = GetExistingChild(parent, parentSchema, segment);
                if (child is null)
                    return false;
                parentSchema = ResolveChildSchema(parentSchema, segment);
                parent = child;
            }

            var leaf = segments[^1];
            if (parent is JsonObject && (leaf is PropertySegment or DictionaryKeySegment))
            {
                if (ReferenceEquals(ResolveChildSchema(parentSchema, leaf), parentSchema))
                    return false;
            }
            else if (parent is JsonArray array && leaf is ListIndexSegment index)
            {
                if (index.Index < 0 || index.Index > array.Count)
                    return false;
            }
            else if (parent is not JsonArray || leaf is not ListItemKeySegment
                     || parentSchema.ListTemplate?.SupportsPerItemMutation is not true)
                return false;

            if (change.MutationKind == ConfigurationMutationKind.Remove)
                RemoveChild(parent, parentSchema, leaf);
            else
                SetChild(parent, parentSchema, leaf, JsonNode.Parse(change.NewValue.Json));
        }
        return true;
    }

    public static bool IsInScope(PendingChange change, string definitionKey, LogicalPath scopePath)
    {
        return string.Equals(change.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase)
               && IsPrefix(scopePath, change.LogicalPath);
    }

    public static JsonNode CreateDefaultJsonFor(ConfigurationNodeDefinition schema)
    {
        return schema.NodeKind switch
        {
            ConfigurationNodeKind.Object => CreateDefaultObjectFor(schema),
            ConfigurationNodeKind.Dictionary => new JsonObject(),
            ConfigurationNodeKind.List => new JsonArray(),
            ConfigurationNodeKind.Scalar => CreateDefaultScalarFor(schema),
            _ => JsonValue.Create(string.Empty)
        };
    }

    private static JsonNode ApplyPendingChange(
        ConfigurationNodeDefinition scopeNode,
        LogicalPath scopePath,
        JsonNode root,
        PendingChange change)
    {
        if (change.LogicalPath.Equals(scopePath))
        {
            return change.MutationKind == ConfigurationMutationKind.Remove
                ? CreateDefaultJsonFor(scopeNode)
                : ParseStoredValue(change.NewValue) ?? CreateDefaultJsonFor(scopeNode);
        }

        var segments = change.LogicalPath.Segments.Skip(scopePath.Depth).ToArray();
        if (segments.Length == 0)
        {
            return root;
        }

        if (change.MutationKind == ConfigurationMutationKind.Remove)
        {
            RemoveRelative(root, scopeNode, segments);
            return root;
        }

        SetRelative(root, scopeNode, segments, ParseStoredValue(change.NewValue));
        return root;
    }

    private static void SetRelative(
        JsonNode root,
        ConfigurationNodeDefinition rootSchema,
        IReadOnlyList<ConfigurationPathSegment> segments,
        JsonNode? value)
    {
        var current = root;
        var currentSchema = rootSchema;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var childSchema = ResolveChildSchema(currentSchema, segments[index]);
            current = GetOrCreateChild(current, currentSchema, segments[index], childSchema);
            currentSchema = childSchema;
        }

        SetChild(current, currentSchema, segments[^1], value);
    }

    private static void RemoveRelative(
        JsonNode root,
        ConfigurationNodeDefinition rootSchema,
        IReadOnlyList<ConfigurationPathSegment> segments)
    {
        var current = root;
        var currentSchema = rootSchema;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var child = GetExistingChild(current, currentSchema, segments[index]);
            if (child is null)
            {
                return;
            }

            currentSchema = ResolveChildSchema(currentSchema, segments[index]);
            current = child;
        }

        RemoveChild(current, currentSchema, segments[^1]);
    }

    private static JsonNode GetOrCreateChild(
        JsonNode current,
        ConfigurationNodeDefinition currentSchema,
        ConfigurationPathSegment segment,
        ConfigurationNodeDefinition childSchema)
    {
        return current switch
        {
            JsonObject jsonObject when segment is PropertySegment or DictionaryKeySegment =>
                jsonObject[segment.Value] ?? AttachObjectChild(jsonObject, segment.Value, childSchema),
            JsonArray jsonArray => GetOrCreateArrayChild(jsonArray, currentSchema, segment, childSchema),
            _ => CreateDefaultJsonFor(childSchema)
        };
    }

    private static JsonNode AttachObjectChild(JsonObject jsonObject, string key, ConfigurationNodeDefinition childSchema)
    {
        var child = CreateDefaultJsonFor(childSchema);
        jsonObject[key] = child;
        return child;
    }

    private static JsonNode GetOrCreateArrayChild(
        JsonArray jsonArray,
        ConfigurationNodeDefinition listSchema,
        ConfigurationPathSegment segment,
        ConfigurationNodeDefinition itemSchema)
    {
        switch (segment)
        {
            case ListIndexSegment listIndex:
                EnsureArraySize(jsonArray, listIndex.Index);
                return jsonArray[listIndex.Index] ?? AttachArrayChild(jsonArray, listIndex.Index, itemSchema);
            case ListItemKeySegment itemKey:
                return FindArrayItemByKey(jsonArray, listSchema, itemKey.ItemKey)
                       ?? AppendArrayItem(jsonArray, listSchema, itemSchema, itemKey.ItemKey);
            default:
                return CreateDefaultJsonFor(itemSchema);
        }
    }

    private static JsonNode AttachArrayChild(JsonArray jsonArray, int index, ConfigurationNodeDefinition itemSchema)
    {
        var child = CreateDefaultJsonFor(itemSchema);
        jsonArray[index] = child;
        return child;
    }

    private static JsonNode AppendArrayItem(
        JsonArray jsonArray,
        ConfigurationNodeDefinition listSchema,
        ConfigurationNodeDefinition itemSchema,
        string itemKey)
    {
        var child = CreateDefaultJsonFor(itemSchema);
        SetListItemKeyValue(child, listSchema, itemKey);
        jsonArray.Add(child);
        return child;
    }

    private static JsonNode? GetExistingChild(
        JsonNode current,
        ConfigurationNodeDefinition currentSchema,
        ConfigurationPathSegment segment)
    {
        return current switch
        {
            JsonObject jsonObject when segment is PropertySegment or DictionaryKeySegment => jsonObject[segment.Value],
            JsonArray jsonArray when segment is ListIndexSegment listIndex && listIndex.Index >= 0 && listIndex.Index < jsonArray.Count => jsonArray[listIndex.Index],
            JsonArray jsonArray when segment is ListItemKeySegment itemKey => FindArrayItemByKey(jsonArray, currentSchema, itemKey.ItemKey),
            _ => null
        };
    }

    private static void SetChild(
        JsonNode current,
        ConfigurationNodeDefinition currentSchema,
        ConfigurationPathSegment segment,
        JsonNode? value)
    {
        switch (current)
        {
            case JsonObject jsonObject when segment is PropertySegment or DictionaryKeySegment:
                jsonObject[segment.Value] = value;
                break;
            case JsonArray jsonArray:
                SetArrayChild(jsonArray, currentSchema, segment, value);
                break;
        }
    }

    private static void SetArrayChild(
        JsonArray jsonArray,
        ConfigurationNodeDefinition listSchema,
        ConfigurationPathSegment segment,
        JsonNode? value)
    {
        switch (segment)
        {
            case ListIndexSegment listIndex:
                EnsureArraySize(jsonArray, listIndex.Index);
                jsonArray[listIndex.Index] = value;
                break;
            case ListItemKeySegment itemKey:
                SetListItemKeyValue(value, listSchema, itemKey.ItemKey);
                var index = FindArrayItemIndexByKey(jsonArray, listSchema, itemKey.ItemKey);
                if (index >= 0)
                {
                    jsonArray[index] = value;
                    return;
                }

                jsonArray.Add(value);
                break;
        }
    }

    private static void RemoveChild(
        JsonNode current,
        ConfigurationNodeDefinition currentSchema,
        ConfigurationPathSegment segment)
    {
        switch (current)
        {
            case JsonObject jsonObject when segment is PropertySegment or DictionaryKeySegment:
                jsonObject.Remove(segment.Value);
                break;
            case JsonArray jsonArray when segment is ListIndexSegment listIndex && listIndex.Index >= 0 && listIndex.Index < jsonArray.Count:
                jsonArray.RemoveAt(listIndex.Index);
                break;
            case JsonArray jsonArray when segment is ListItemKeySegment itemKey:
                var index = FindArrayItemIndexByKey(jsonArray, currentSchema, itemKey.ItemKey);
                if (index >= 0)
                {
                    jsonArray.RemoveAt(index);
                }

                break;
        }
    }

    private static ConfigurationNodeDefinition ResolveChildSchema(
        ConfigurationNodeDefinition current,
        ConfigurationPathSegment segment)
    {
        return segment switch
        {
            PropertySegment property => current.Children.FirstOrDefault(child =>
                string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase)) ?? current,
            DictionaryKeySegment => current.DictionaryTemplate?.ValueTemplate ?? current,
            ListIndexSegment or ListItemKeySegment => current.ListTemplate?.ItemTemplate ?? current,
            _ => current
        };
    }

    private static JsonNode? FindArrayItemByKey(JsonArray jsonArray, ConfigurationNodeDefinition listSchema, string itemKey)
    {
        var index = FindArrayItemIndexByKey(jsonArray, listSchema, itemKey);
        return index >= 0 ? jsonArray[index] : null;
    }

    private static int FindArrayItemIndexByKey(JsonArray jsonArray, ConfigurationNodeDefinition listSchema, string itemKey)
    {
        var keyPropertyName = listSchema.ListTemplate?.ItemKeyPropertyName;
        if (string.IsNullOrWhiteSpace(keyPropertyName))
        {
            return -1;
        }

        for (var index = 0; index < jsonArray.Count; index++)
        {
            if (string.Equals(ReadObjectPropertyText(jsonArray[index], keyPropertyName), itemKey, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SetListItemKeyValue(JsonNode? value, ConfigurationNodeDefinition listSchema, string itemKey)
    {
        var keyPropertyName = listSchema.ListTemplate?.ItemKeyPropertyName;
        if (value is JsonObject jsonObject && !string.IsNullOrWhiteSpace(keyPropertyName))
        {
            jsonObject[keyPropertyName] = itemKey;
        }
    }

    private static string? ReadObjectPropertyText(JsonNode? node, string propertyName)
    {
        return node is JsonObject jsonObject ? ReadScalarAsString(jsonObject[propertyName]) : null;
    }

    private static string? ReadScalarAsString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString(),
            JsonValueKind.Number => document.RootElement.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => document.RootElement.GetRawText()
        };
    }

    private static void EnsureArraySize(JsonArray jsonArray, int index)
    {
        while (jsonArray.Count <= index)
        {
            jsonArray.Add(null);
        }
    }

    private static JsonNode? ParseStoredValue(ConfigurationStoredValue value)
    {
        return ParseJson(value.Json);
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject CreateDefaultObjectFor(ConfigurationNodeDefinition schema)
    {
        var jsonObject = new JsonObject();
        foreach (var child in schema.Children)
        {
            jsonObject[child.Name] = CreateDefaultJsonFor(child);
        }

        return jsonObject;
    }

    private static JsonNode CreateDefaultScalarFor(ConfigurationNodeDefinition schema)
    {
        return schema.ValueKind switch
        {
            ConfigurationValueKind.Boolean => JsonValue.Create(false),
            ConfigurationValueKind.Integer => JsonValue.Create(0),
            ConfigurationValueKind.Decimal or ConfigurationValueKind.Floating => JsonValue.Create(0m),
            _ => JsonValue.Create(string.Empty)
        };
    }

    private static bool IsPrefix(LogicalPath ancestor, LogicalPath path)
    {
        if (ancestor.Depth > path.Depth)
        {
            return false;
        }

        for (var index = 0; index < ancestor.Depth; index++)
        {
            if (!ancestor.Segments[index].Equals(path.Segments[index]))
            {
                return false;
            }
        }

        return true;
    }
}
