using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Serialization;

/// <summary>
/// Serializes and restores the portable portion of a configuration definition schema.
/// </summary>
public static class ConfigurationDefinitionSchemaCodec
{
    /// <summary>
    /// Serializes the definition schema with exact scalar and dictionary-key CLR identities while omitting runtime-only paths.
    /// </summary>
    /// <param name="definition">The definition to serialize.</param>
    /// <returns>The compact schema JSON.</returns>
    public static string SerializeSchema(ConfigurationDefinition definition)
    {
        return JsonSerializer.Serialize(ToNodeDto(definition.Root, 0) with
        {
            ValidationContract = definition.ValidationContract
        }, ConfigurationPersistedJsonOptions.CompactSchema);
    }

    /// <summary>
    /// Serializes the stable hash input for a definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="sectionPath">The binding section path.</param>
    /// <param name="root">The root schema node.</param>
    /// <returns>Compact JSON suitable for deterministic hashing.</returns>
    public static string SerializeHashInput(string definitionKey, string sectionPath, ConfigurationNodeDefinition root)
    {
        var input = new SchemaHashInputDto
        {
            DefinitionKey = definitionKey.ToUpperInvariant(),
            SectionPath = sectionPath,
            Root = ToNodeDto(root, 0)
        };
        return JsonSerializer.Serialize(input, ConfigurationPersistedJsonOptions.CompactSchema);
    }

    /// <summary>
    /// Computes the canonical SHA-256 identity for a configuration definition schema.
    /// </summary>
    /// <param name="definitionKey">The stable definition key.</param>
    /// <param name="sectionPath">The Microsoft configuration section path.</param>
    /// <param name="root">The root schema node.</param>
    /// <returns>The canonical lowercase schema hash.</returns>
    public static string ComputeSchemaHash(
        string definitionKey,
        string sectionPath,
        ConfigurationNodeDefinition root)
    {
        var json = SerializeHashInput(definitionKey, sectionPath, root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// Rebuilds a runtime definition from compact persisted schema metadata.
    /// </summary>
    /// <param name="definitionKey">The stable published definition key.</param>
    /// <param name="sectionPath">The Microsoft configuration section path used by the owner.</param>
    /// <param name="displayName">The operator-facing definition display name.</param>
    /// <param name="description">The optional operator-facing definition description.</param>
    /// <param name="clrTypeName">The compact root type identity stored for display and search.</param>
    /// <param name="fromProject">The assembly name of the project that published the definition.</param>
    /// <param name="category">The optional operator-facing category.</param>
    /// <param name="schemaVersion">The published schema version.</param>
    /// <param name="schemaHash">The published schema hash used for drift detection.</param>
    /// <param name="reloadBehavior">The definition-level reload behavior.</param>
    /// <param name="schemaJson">The compact schema JSON.</param>
    /// <param name="origin">The origin marker to attach to the rebuilt definition.</param>
    /// <returns>
    /// A runtime definition with reconstructed logical and Microsoft configuration paths plus the exact persisted
    /// scalar and dictionary-key CLR identities required for binding-compatible validation.
    /// </returns>
    public static ConfigurationDefinition DeserializeDefinition(
        string definitionKey,
        string sectionPath,
        string displayName,
        string? description,
        string clrTypeName,
        string fromProject,
        string? category,
        int schemaVersion,
        string schemaHash,
        ConfigurationReloadBehavior reloadBehavior,
        string schemaJson,
        ConfigurationDefinitionOrigin origin)
    {
        var rootDto = JsonSerializer.Deserialize<NodeDto>(schemaJson, ConfigurationPersistedJsonOptions.CompactSchema)
            ?? throw new InvalidOperationException($"Configuration definition '{definitionKey}' has empty schema JSON.");

        ValidateNodeDto(rootDto, LogicalPath.Root, 0);

        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = sectionPath,
            DisplayName = displayName,
            Description = description,
            ClrTypeName = clrTypeName,
            FromProject = fromProject,
            Category = category,
            SchemaVersion = schemaVersion,
            SchemaHash = schemaHash,
            ValidationContract = rootDto.ValidationContract ?? new ConfigurationValidationContract(),
            ReloadBehavior = reloadBehavior,
            Root = FromNodeDto(rootDto, LogicalPath.Root, sectionPath, clrTypeName),
            Origin = origin
        };
    }

    /// <summary>
    /// Converts an assembly-qualified CLR type name into a compact display/search identity.
    /// </summary>
    /// <param name="clrTypeName">The CLR type name captured from scanner metadata.</param>
    /// <returns>A compact type name without assembly identity. Formatting never loads the named assembly or executes owner code.</returns>
    public static string ToCompactClrTypeName(string clrTypeName)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            return string.Empty;
        }

        return CompactTypeName(clrTypeName, 0);
    }

    private static string CompactTypeName(string identity, int depth)
    {
        var nesting = 0;
        var end = identity.Length;
        for (var i = 0; i < identity.Length; i++)
        {
            if (identity[i] == '[') nesting++;
            else if (identity[i] == ']') nesting--;
            else if (identity[i] == ',' && nesting == 0) { end = i; break; }
        }
        var typeName = identity[..end].Trim();
        var argumentStart = typeName.IndexOf('[');
        var generic = typeName.IndexOf('`');
        if (generic < 0 || argumentStart < generic || depth >= ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH)
            return typeName.Replace('+', '.');
        nesting = 0;
        var argumentEnd = -1;
        for (var i = argumentStart; i < typeName.Length; i++)
        {
            if (typeName[i] == '[') nesting++;
            else if (typeName[i] == ']' && --nesting == 0) { argumentEnd = i; break; }
        }
        if (argumentEnd < 0) return typeName.Replace('+', '.');
        var baseName = typeName[..argumentStart];
        var cleanBase = new StringBuilder();
        for (var i = 0; i < baseName.Length; i++)
        {
            if (baseName[i] == '`')
            {
                while (i + 1 < baseName.Length && char.IsDigit(baseName[i + 1])) i++;
            }
            else cleanBase.Append(baseName[i] == '+' ? '.' : baseName[i]);
        }
        var arguments = SplitTypeArguments(typeName[(argumentStart + 1)..argumentEnd])
            .Select(argument => CompactTypeName(argument, depth + 1));
        return $"{cleanBase}<{string.Join(',', arguments)}>{typeName[(argumentEnd + 1)..]}";
    }

    private static IEnumerable<string> SplitTypeArguments(string arguments)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= arguments.Length; i++)
        {
            if (i < arguments.Length)
            {
                if (arguments[i] == '[') depth++;
                else if (arguments[i] == ']') depth--;
                if (arguments[i] != ',' || depth != 0) continue;
            }
            var argument = arguments[start..i].Trim();
            if (argument.StartsWith('[') && argument.EndsWith(']')) argument = argument[1..^1];
            if (argument.Length > 0) yield return argument;
            start = i + 1;
        }
    }

    private static NodeDto ToNodeDto(ConfigurationNodeDefinition node, int depth)
    {
        EnsureSerializableDepth(node, depth);
        var canonicalPath = node.RelativePath.ToCanonicalString();
        return new NodeDto
        {
            NodeKey = string.Equals(node.NodeKey, canonicalPath, StringComparison.Ordinal) ? null : node.NodeKey,
            Name = node.Name,
            DisplayName = NullIfWhiteSpace(node.DisplayName),
            Description = NullIfWhiteSpace(node.Description),
            NodeKind = node.NodeKind,
            ValueKind = node.NodeKind == ConfigurationNodeKind.Scalar ? node.ValueKind : null,
            ClrTypeName = node.NodeKind == ConfigurationNodeKind.Scalar ? node.ClrTypeName : null,
            IsNullable = node.IsNullable,
            IsSensitive = node.IsSensitive,
            TextSemantic = node.IsRegexPatternText ? node.TextSemantic : null,
            EditorHint = NullIfWhiteSpace(node.EditorHint),
            ReloadBehavior = node.ReloadBehavior,
            DictionaryTemplate = node.DictionaryTemplate is null ? null : ToDictionaryDto(node.DictionaryTemplate, depth),
            ListTemplate = node.ListTemplate is null ? null : ToListDto(node.ListTemplate, depth),
            Children = node.Children.Count == 0
                ? null
                : node.Children.Select(child => ToNodeDto(child, depth + 1)).ToArray(),
            ValidationRules = node.ValidationRules.Count == 0 ? null : node.ValidationRules.Select(ToRuleDto).ToArray(),
            EnumValues = node.EnumValues.Count == 0 ? null : node.EnumValues.Select(ToEnumValueDto).ToArray()
        };
    }

    private static EnumValueDto ToEnumValueDto(ConfigurationEnumValue value)
    {
        return new EnumValueDto
        {
            Name = value.Name,
            Value = value.Value
        };
    }

    private static DictionaryTemplateDto ToDictionaryDto(ConfigurationDictionaryTemplate template, int depth)
    {
        return new DictionaryTemplateDto
        {
            KeyKind = template.KeyKind,
            KeyClrTypeName = template.KeyClrTypeName,
            KeyEnumValues = template.KeyEnumValues.Count == 0 ? null : template.KeyEnumValues.Select(ToEnumValueDto).ToArray(),
            KeyEnumUnderlyingClrTypeName = template.KeyEnumUnderlyingClrTypeName,
            KeyRegexPattern = NormalizeRegexPatternOrNull(template.KeyRegexPattern),
            DisallowColonInKey = template.DisallowColonInKey ? null : false,
            ValueTemplate = ToNodeDto(template.ValueTemplate, depth + 1)
        };
    }

    private static ListTemplateDto ToListDto(ConfigurationListTemplate template, int depth)
    {
        return new ListTemplateDto
        {
            AllowDuplicateItems = template.AllowDuplicateItems,
            ItemKeyPropertyName = NullIfWhiteSpace(template.ItemKeyPropertyName),
            ItemTemplate = ToNodeDto(template.ItemTemplate, depth + 1)
        };
    }

    private static void EnsureSerializableDepth(ConfigurationNodeDefinition node, int depth)
    {
        if (depth <= ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH)
        {
            return;
        }

        var schemaPath = node.RelativePath.ToCanonicalString();
        throw new InvalidOperationException(
            $"Configuration schema exceeds the maximum logical depth of "
            + $"{ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH} at schema path '{schemaPath}'.");
    }

    private static RuleDto ToRuleDto(ConfigurationValidationRule rule)
    {
        return rule switch
        {
            RequiredRule required => new RuleDto
            {
                Kind = RuleKind.Required,
                ErrorMessage = NullIfWhiteSpace(required.ErrorMessage)
            },
            RangeRule range => new RuleDto
            {
                Kind = RuleKind.Range,
                ErrorMessage = NullIfWhiteSpace(range.ErrorMessage),
                Min = range.Min,
                Max = range.Max
            },
            RegexRule regex => new RuleDto
            {
                Kind = RuleKind.Regex,
                ErrorMessage = NullIfWhiteSpace(regex.ErrorMessage),
                Pattern = ConfigurationRegexTextCodec.NormalizePattern(regex.Pattern)
            },
            AllowedValuesRule allowed => new RuleDto
            {
                Kind = RuleKind.AllowedValues,
                ErrorMessage = NullIfWhiteSpace(allowed.ErrorMessage),
                Values = allowed.Values.Count == 0 ? null : allowed.Values
            },
            MaxLengthRule maxLength => new RuleDto
            {
                Kind = RuleKind.MaxLength,
                ErrorMessage = NullIfWhiteSpace(maxLength.ErrorMessage),
                Length = maxLength.Max
            },
            MinLengthRule minLength => new RuleDto
            {
                Kind = RuleKind.MinLength,
                ErrorMessage = NullIfWhiteSpace(minLength.ErrorMessage),
                Length = minLength.Min
            },
            _ => throw new InvalidOperationException($"Unsupported configuration validation rule '{rule.GetType().Name}'.")
        };
    }

    private static ConfigurationNodeDefinition FromNodeDto(
        NodeDto dto,
        LogicalPath path,
        string sectionPath,
        string rootClrTypeName)
    {
        var nodeKind = dto.NodeKind;
        var configurationPath = ProjectConfigurationPath(sectionPath, path);
        var children = (dto.Children ?? [])
            .Select(child => FromNodeDto(
                child,
                path.Append(new PropertySegment(child.Name)),
                sectionPath,
                rootClrTypeName))
            .ToArray();

        return new ConfigurationNodeDefinition
        {
            NodeKey = string.IsNullOrWhiteSpace(dto.NodeKey) ? path.ToCanonicalString() : dto.NodeKey!,
            Name = dto.Name,
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            RelativePath = path,
            ConfigurationPath = configurationPath,
            ClrTypeName = ResolveRuntimeClrTypeName(dto, path, rootClrTypeName),
            NodeKind = nodeKind,
            ValueKind = nodeKind == ConfigurationNodeKind.Scalar ? dto.ValueKind ?? ConfigurationValueKind.String : null,
            IsNullable = dto.IsNullable,
            IsSensitive = dto.IsSensitive,
            TextSemantic = nodeKind == ConfigurationNodeKind.Scalar
                ? dto.TextSemantic ?? ConfigurationTextSemantic.PlainText
                : ConfigurationTextSemantic.PlainText,
            EditorHint = dto.EditorHint,
            ReloadBehavior = dto.ReloadBehavior,
            DictionaryTemplate = dto.DictionaryTemplate is null
                ? null
                : FromDictionaryDto(dto.DictionaryTemplate, path, sectionPath, rootClrTypeName),
            ListTemplate = dto.ListTemplate is null
                ? null
                : FromListDto(dto.ListTemplate, path, sectionPath, rootClrTypeName),
            Children = children,
            ValidationRules = (dto.ValidationRules ?? []).Select(FromRuleDto).ToArray(),
            EnumValues = (dto.EnumValues ?? []).Select(FromEnumValueDto).ToArray()
        };
    }

    private static ConfigurationEnumValue FromEnumValueDto(EnumValueDto dto)
    {
        return new ConfigurationEnumValue
        {
            Name = dto.Name,
            Value = dto.Value
        };
    }

    private static void ValidateNodeDto(NodeDto dto, LogicalPath path, int depth)
    {
        if (depth > ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH)
        {
            ThrowInvalidSchema(
                $"Persisted configuration schema exceeds the maximum logical depth of "
                + $"{ConfigurationSchemaLimits.MAX_LOGICAL_DEPTH}.",
                path);
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            ThrowInvalidSchema("Persisted configuration schema contains a node without a name.", path);
        }

        if (!Enum.IsDefined(dto.NodeKind))
        {
            ThrowInvalidSchema($"Persisted configuration schema uses unsupported node kind '{dto.NodeKind}'.", path);
        }

        if (dto.ValueKind is { } valueKind && !Enum.IsDefined(valueKind))
        {
            ThrowInvalidSchema($"Persisted configuration schema uses unsupported value kind '{valueKind}'.", path);
        }

        if (dto.TextSemantic is { } textSemantic && !Enum.IsDefined(textSemantic))
        {
            ThrowInvalidSchema($"Persisted configuration schema uses unsupported text semantic '{textSemantic}'.", path);
        }

        if (dto.ReloadBehavior is { } reloadBehavior && !Enum.IsDefined(reloadBehavior))
        {
            ThrowInvalidSchema($"Persisted configuration schema uses unsupported reload behavior '{reloadBehavior}'.", path);
        }

        var children = dto.Children ?? [];
        switch (dto.NodeKind)
        {
            case ConfigurationNodeKind.Scalar:
                if (dto.ValueKind is null)
                {
                    ThrowInvalidSchema("Persisted scalar schema node is missing its value kind.", path);
                }

                RequireClrTypeName(dto.ClrTypeName, path, "scalar node");
                RequireNoTemplatesOrChildren(dto, children, path);
                break;
            case ConfigurationNodeKind.Object:
                if (dto.ValueKind is not null || dto.DictionaryTemplate is not null || dto.ListTemplate is not null)
                {
                    ThrowInvalidSchema("Persisted object schema node contains scalar or collection metadata.", path);
                }

                break;
            case ConfigurationNodeKind.Dictionary:
                if (dto.ValueKind is not null || dto.ListTemplate is not null || children.Count > 0)
                {
                    ThrowInvalidSchema("Persisted dictionary schema node contains incompatible node metadata.", path);
                }

                ValidateDictionaryTemplate(dto.DictionaryTemplate, path, depth);
                break;
            case ConfigurationNodeKind.List:
                if (dto.ValueKind is not null || dto.DictionaryTemplate is not null || children.Count > 0)
                {
                    ThrowInvalidSchema("Persisted list schema node contains incompatible node metadata.", path);
                }

                ValidateListTemplate(dto.ListTemplate, path, depth);
                break;
        }

        if (dto.NodeKind != ConfigurationNodeKind.Scalar && dto.TextSemantic is not null)
        {
            ThrowInvalidSchema("Persisted non-scalar schema node contains text-semantic metadata.", path);
        }

        if (dto.EnumValues is { Count: > 0 } enumValues)
        {
            if (dto.NodeKind != ConfigurationNodeKind.Scalar || dto.ValueKind != ConfigurationValueKind.Enum)
            {
                ThrowInvalidSchema("Persisted enum values belong to a non-enum schema node.", path);
            }

            foreach (var enumValue in enumValues)
            {
                if (enumValue is null
                    || string.IsNullOrWhiteSpace(enumValue.Name)
                    || string.IsNullOrWhiteSpace(enumValue.Value))
                {
                    ThrowInvalidSchema("Persisted schema contains an invalid enum value entry.", path);
                }
            }
        }

        foreach (var rule in dto.ValidationRules ?? [])
        {
            if (rule is null || rule.Kind is null || !Enum.IsDefined(rule.Kind.Value))
            {
                ThrowInvalidSchema("Persisted schema contains an invalid validation rule entry.", path);
            }

            switch (rule.Kind.Value)
            {
                case RuleKind.Range when rule.Min is not null && rule.Max is not null && rule.Min > rule.Max:
                    ThrowInvalidSchema("Persisted range validation rule has a minimum greater than its maximum.", path);
                    break;
                case RuleKind.Regex when !ConfigurationRegexTextCodec.TryValidatePattern(rule.Pattern, out var regexProblem):
                    ThrowInvalidSchema($"Persisted regex validation rule is invalid: {regexProblem}", path);
                    break;
                case RuleKind.MaxLength or RuleKind.MinLength when rule.Length < 0:
                    ThrowInvalidSchema("Persisted length validation rule contains a negative length.", path);
                    break;
            }
        }

        var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            if (child is null || string.IsNullOrWhiteSpace(child.Name))
            {
                ThrowInvalidSchema("Persisted object schema contains an invalid child node.", path);
            }

            if (!childNames.Add(child.Name))
            {
                ThrowInvalidSchema($"Persisted object schema contains duplicate child name '{child.Name}'.", path);
            }

            ValidateNodeDto(child, path.Append(new PropertySegment(child.Name)), depth + 1);
        }
    }

    private static void RequireNoTemplatesOrChildren(
        NodeDto dto,
        IReadOnlyList<NodeDto> children,
        LogicalPath path)
    {
        if (dto.DictionaryTemplate is not null || dto.ListTemplate is not null || children.Count > 0)
        {
            ThrowInvalidSchema("Persisted scalar schema node contains object or collection metadata.", path);
        }
    }

    private static void ValidateDictionaryTemplate(
        DictionaryTemplateDto? template,
        LogicalPath path,
        int depth)
    {
        if (template is null)
        {
            ThrowInvalidSchema("Persisted dictionary schema node is missing its value template.", path);
        }

        if (!Enum.IsDefined(template.KeyKind))
        {
            ThrowInvalidSchema($"Persisted dictionary schema uses unsupported key kind '{template.KeyKind}'.", path);
        }

        RequireClrTypeName(template.KeyClrTypeName, path, "dictionary key");
        if (!ConfigurationRegexTextCodec.TryValidatePattern(template.KeyRegexPattern, out var regexProblem))
        {
            ThrowInvalidSchema($"Persisted dictionary key regex is invalid: {regexProblem}", path);
        }

        if (template.ValueTemplate is null)
        {
            ThrowInvalidSchema("Persisted dictionary schema node has a null value template.", path);
        }

        ValidateNodeDto(
            template.ValueTemplate,
            path.Append(new DictionaryKeySegment("*")),
            depth + 1);
    }

    private static void ValidateListTemplate(
        ListTemplateDto? template,
        LogicalPath path,
        int depth)
    {
        if (template is null)
        {
            ThrowInvalidSchema("Persisted list schema node is missing its item template.", path);
        }

        if (template.ItemTemplate is null)
        {
            ThrowInvalidSchema("Persisted list schema node has a null item template.", path);
        }

        ValidateNodeDto(
            template.ItemTemplate,
            path.Append(new ListItemKeySegment("*")),
            depth + 1);
    }

    [DoesNotReturn]
    private static void ThrowInvalidSchema(string message, LogicalPath path)
    {
        var schemaPath = path.ToCanonicalString();
        throw new ConfigurationPersistedSchemaException(
            ConfigurationDefinitionMetadataIssueKind.InvalidSchema,
            $"{message} Schema path: '{schemaPath}'.",
            schemaPath);
    }

    private static ConfigurationDictionaryTemplate FromDictionaryDto(
        DictionaryTemplateDto dto,
        LogicalPath dictionaryPath,
        string sectionPath,
        string rootClrTypeName)
    {
        return new ConfigurationDictionaryTemplate
        {
            KeyClrTypeName = RequireClrTypeName(
                dto.KeyClrTypeName,
                dictionaryPath,
                "dictionary key"),
            KeyKind = dto.KeyKind,
            KeyEnumValues = dto.KeyEnumValues?.Select(value => new ConfigurationEnumValue { Name = value.Name, Value = value.Value }).ToArray() ?? [],
            KeyEnumUnderlyingClrTypeName = dto.KeyEnumUnderlyingClrTypeName,
            KeyRegexPattern = NormalizeRegexPatternOrNull(dto.KeyRegexPattern),
            ValueTemplate = FromNodeDto(
                dto.ValueTemplate,
                dictionaryPath.Append(new DictionaryKeySegment("*")),
                sectionPath,
                rootClrTypeName),
            DisallowColonInKey = dto.DisallowColonInKey ?? true
        };
    }

    private static ConfigurationListTemplate FromListDto(
        ListTemplateDto dto,
        LogicalPath listPath,
        string sectionPath,
        string rootClrTypeName)
    {
        return new ConfigurationListTemplate
        {
            AllowDuplicateItems = dto.AllowDuplicateItems,
            ItemKeyPropertyName = dto.ItemKeyPropertyName,
            ItemTemplate = FromNodeDto(
                dto.ItemTemplate,
                listPath.Append(new ListItemKeySegment("*")),
                sectionPath,
                rootClrTypeName)
        };
    }

    private static ConfigurationValidationRule FromRuleDto(RuleDto dto)
    {
        return (dto.Kind ?? RuleKind.Required) switch
        {
            RuleKind.Required => new RequiredRule { ErrorMessage = dto.ErrorMessage },
            RuleKind.Range => new RangeRule(dto.Min, dto.Max) { ErrorMessage = dto.ErrorMessage },
            RuleKind.Regex => new RegexRule(ConfigurationRegexTextCodec.NormalizePattern(dto.Pattern)) { ErrorMessage = dto.ErrorMessage },
            RuleKind.AllowedValues => new AllowedValuesRule(dto.Values ?? []) { ErrorMessage = dto.ErrorMessage },
            RuleKind.MaxLength => new MaxLengthRule(dto.Length) { ErrorMessage = dto.ErrorMessage },
            RuleKind.MinLength => new MinLengthRule(dto.Length) { ErrorMessage = dto.ErrorMessage },
            _ => throw new InvalidOperationException($"Unsupported persisted validation rule kind '{dto.Kind}'.")
        };
    }

    private static string ResolveRuntimeClrTypeName(NodeDto dto, LogicalPath path, string rootClrTypeName)
    {
        if (dto.NodeKind == ConfigurationNodeKind.Scalar)
        {
            return RequireClrTypeName(dto.ClrTypeName, path, "scalar node");
        }

        if (path.Depth == 0 && !string.IsNullOrWhiteSpace(rootClrTypeName))
        {
            return rootClrTypeName;
        }

        return dto.NodeKind switch
        {
            ConfigurationNodeKind.Dictionary => typeof(Dictionary<string, object>).FullName!,
            ConfigurationNodeKind.List => typeof(List<object>).FullName!,
            _ => typeof(object).FullName!
        };
    }

    private static string RequireClrTypeName(
        string? clrTypeName,
        LogicalPath path,
        string metadataKind)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            var schemaPath = path.ToCanonicalString();
            throw new ConfigurationPersistedSchemaException(
                ConfigurationDefinitionMetadataIssueKind.OutdatedSchemaContract,
                $"Persisted configuration schema path '{schemaPath}' is missing the exact "
                + $"{metadataKind} CLR type. Republish the definition metadata before using it.",
                schemaPath);
        }

        return clrTypeName;
    }

    private static string ProjectConfigurationPath(string sectionPath, LogicalPath path)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sectionPath))
        {
            parts.Add(sectionPath);
        }

        parts.AddRange(path.Segments.Select(segment => segment.Value));
        return string.Join(':', parts);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? NormalizeRegexPatternOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ConfigurationRegexTextCodec.NormalizePattern(value);
    }

    private sealed record SchemaHashInputDto
    {
        public required string DefinitionKey { get; init; }

        public required string SectionPath { get; init; }

        public required NodeDto Root { get; init; }
    }

    private sealed record NodeDto
    {
        public ConfigurationValidationContract? ValidationContract { get; init; }

        public string? NodeKey { get; init; }

        public string Name { get; init; } = "";

        public string? DisplayName { get; init; }

        public string? Description { get; init; }

        public ConfigurationNodeKind NodeKind { get; init; }

        public ConfigurationValueKind? ValueKind { get; init; }

        public string? ClrTypeName { get; init; }

        public bool IsNullable { get; init; }

        public bool IsSensitive { get; init; }

        public ConfigurationTextSemantic? TextSemantic { get; init; }

        public string? EditorHint { get; init; }

        public ConfigurationReloadBehavior? ReloadBehavior { get; init; }

        public DictionaryTemplateDto? DictionaryTemplate { get; init; }

        public ListTemplateDto? ListTemplate { get; init; }

        public IReadOnlyList<NodeDto>? Children { get; init; }

        public IReadOnlyList<RuleDto>? ValidationRules { get; init; }

        public IReadOnlyList<EnumValueDto>? EnumValues { get; init; }
    }

    private sealed record EnumValueDto
    {
        public string Name { get; init; } = "";

        public string Value { get; init; } = "";
    }

    private sealed record DictionaryTemplateDto
    {
        public ConfigurationValueKind KeyKind { get; init; }

        public string? KeyClrTypeName { get; init; }

        public IReadOnlyList<EnumValueDto>? KeyEnumValues { get; init; }

        public string? KeyEnumUnderlyingClrTypeName { get; init; }

        public string? KeyRegexPattern { get; init; }

        public bool? DisallowColonInKey { get; init; }

        public required NodeDto ValueTemplate { get; init; }
    }

    private sealed record ListTemplateDto
    {
        public bool AllowDuplicateItems { get; init; }

        public required NodeDto ItemTemplate { get; init; }

        public string? ItemKeyPropertyName { get; init; }
    }

    private sealed record RuleDto
    {
        public RuleKind? Kind { get; init; }

        public string? ErrorMessage { get; init; }

        public decimal? Min { get; init; }

        public decimal? Max { get; init; }

        public string? Pattern { get; init; }

        public IReadOnlyList<string>? Values { get; init; }

        public int Length { get; init; }
    }

    private enum RuleKind
    {
        Required,
        Range,
        Regex,
        AllowedValues,
        MaxLength,
        MinLength
    }
}
