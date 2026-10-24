using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>
/// Reads display-safe effective configuration state while sharing one persisted document across a definition snapshot.
/// </summary>
/// <param name="serviceProvider">Resolves the effective-value store only when a managed definition is read.</param>
/// <param name="seedFactory">The factory for persisted and runtime fallback JSON values.</param>
/// <param name="documentEditor">The logical-path reader for effective-value documents.</param>
/// <param name="sourceInspector">The runtime configuration source inspector.</param>
public sealed class ConfigurationEffectiveStateReader(
    IServiceProvider serviceProvider,
    ConfigurationEffectiveValueSeedFactory seedFactory,
    ConfigurationEffectiveValueDocumentEditor documentEditor,
    IConfigurationSourceInspector sourceInspector)
{
    private IConfigurationEffectiveValueStore EffectiveValueStore => serviceProvider.GetRequiredService<IConfigurationEffectiveValueStore>();
    private bool IsManual => serviceProvider.GetService<ConfigurationRuntimeContext>()?.AutonomousWork == false;
    private bool IsUnpersisted(ConfigurationDefinition definition) => IsManual
        && serviceProvider.GetService<MonicaConfigurationProviderAccessor>()?.Provider?.IsUnpersisted(definition.DefinitionKey) == true;
    /// <summary>
    /// Reads one display-safe effective value.
    /// </summary>
    /// <param name="definition">The definition that owns the value.</param>
    /// <param name="logicalPath">The logical path to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeSensitive">Whether to include annotated sensitive values; false masks them by default.</param>
    /// <returns>The display-safe effective value.</returns>
    public async Task<ConfigurationEffectiveValue> ReadValueAsync(
        ConfigurationDefinition definition,
        LogicalPath logicalPath,
        CancellationToken cancellationToken,
        bool includeSensitive = false)
    {
        if (definition.Kind == ConfigurationKind.Startup)
            return sourceInspector.GetSourceChain(definition, logicalPath, includeSensitive).StartupActualValue
                ?? throw new InvalidOperationException("Startup configuration diagnostics are unavailable.");
        var document = await EffectiveValueStore.GetAsync(definition.DefinitionKey, cancellationToken);
        var targetNode = ConfigurationSchemaNavigator.ResolveNode(definition.Root, logicalPath);
        var snapshotJson = CreateSnapshotJson(definition, document);
        var storedValue = documentEditor.ReadValue(definition, snapshotJson, logicalPath);
        var runtimeSourceValue = definition.Origin == ConfigurationDefinitionOrigin.LocalScan
                                 && targetNode?.NodeKind == ConfigurationNodeKind.Scalar
            ? sourceInspector.GetSourceChain(definition, logicalPath, includeSensitive).Values.FirstOrDefault(value => value.IsEffective)
            : null;
        return MaterializeValue(
            definition,
            logicalPath,
            targetNode,
            document,
            storedValue,
            runtimeSourceValue,
            includeSensitive);
    }

    /// <summary>
    /// Reads the effective management state for every statically declared value in one definition.
    /// </summary>
    /// <param name="definition">The definition to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeSensitive">Whether to include annotated sensitive values; false masks them by default.</param>
    /// <returns>The definition and its display-safe effective values.</returns>
    public async Task<ConfigurationDefinitionState> ReadDefinitionAsync(
        ConfigurationDefinition definition,
        CancellationToken cancellationToken,
        bool includeSensitive = false)
    {
        if (definition.Kind == ConfigurationKind.Startup) return ReadStartupDefinition(definition, includeSensitive);
        var document = await EffectiveValueStore.GetAsync(definition.DefinitionKey, cancellationToken);
        return MaterializeDefinition(definition, document, includeSensitive);
    }

    /// <summary>
    /// Reads effective management state for several definitions with one effective-value store operation.
    /// </summary>
    /// <param name="definitions">The definitions to read, in required result order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeSensitive">Whether to include annotated sensitive values; false masks them by default.</param>
    /// <returns>Definition states in the same order as <paramref name="definitions"/>.</returns>
    public async Task<IReadOnlyList<ConfigurationDefinitionState>> ReadDefinitionsAsync(
        IReadOnlyList<ConfigurationDefinition> definitions,
        CancellationToken cancellationToken,
        bool includeSensitive = false)
    {
        if (definitions.Count == 0)
        {
            return [];
        }

        var managed = definitions.Where(definition => definition.Kind == ConfigurationKind.Managed).ToArray();
        IReadOnlyList<ConfigurationEffectiveValueDocument?> documents = managed.Length == 0 ? []
            : await EffectiveValueStore.GetManyAsync(managed.Select(static definition => definition.DefinitionKey).ToArray(), cancellationToken);
        if (documents.Count != managed.Length)
        {
            throw new InvalidOperationException(
                "The effective-value store returned a different number of documents than requested definitions.");
        }

        var states = new ConfigurationDefinitionState[definitions.Count];
        var managedIndex = 0;
        for (var index = 0; index < definitions.Count; index++)
        {
            states[index] = definitions[index].Kind == ConfigurationKind.Startup
                ? ReadStartupDefinition(definitions[index], includeSensitive)
                : MaterializeDefinition(definitions[index], documents[managedIndex++], includeSensitive);
        }

        return states;
    }

    private ConfigurationDefinitionState ReadStartupDefinition(ConfigurationDefinition definition, bool includeSensitive)
    {
        var paths = EnumerateNodes(definition.Root).Select(node => node.RelativePath).ToArray();
        var chains = sourceInspector.GetSourceChains(definition, paths, includeSensitive);
        return new ConfigurationDefinitionState
        {
            Definition = definition,
            StartupCandidate = chains[0].StartupActualValue?.StartupCandidate,
            EffectiveValues = chains.Skip(1).Select(chain => chain.StartupActualValue
                ?? throw new InvalidOperationException("Startup configuration diagnostics are unavailable.")).ToArray()
        };
    }

    private ConfigurationDefinitionState MaterializeDefinition(
        ConfigurationDefinition definition,
        ConfigurationEffectiveValueDocument? document,
        bool includeSensitive)
    {
        var stateNodes = EnumerateNodes(definition.Root)
            .Where(static node => node.RelativePath.Depth > 0)
            .ToArray();
        var snapshotJson = CreateSnapshotJson(definition, document);
        var storedValues = documentEditor.ReadValues(
            definition,
            snapshotJson,
            stateNodes.Select(static node => node.RelativePath).ToArray());
        var runtimeSourceValues = ReadRuntimeSourceValues(definition, stateNodes, includeSensitive);
        var values = new ConfigurationEffectiveValue[stateNodes.Length];
        for (var index = 0; index < stateNodes.Length; index++)
        {
            values[index] = MaterializeValue(
                definition,
                stateNodes[index].RelativePath,
                stateNodes[index],
                document,
                storedValues[index],
                runtimeSourceValues?[index],
                includeSensitive);
        }

        return new ConfigurationDefinitionState
        {
            Definition = definition,
            IsUnpersisted = IsUnpersisted(definition),
            EffectiveValueVersion = IsUnpersisted(definition) ? null : document?.Version,
            EffectiveValues = values
        };
    }

    private string CreateSnapshotJson(
        ConfigurationDefinition definition,
        ConfigurationEffectiveValueDocument? document)
    {
        return definition.Origin == ConfigurationDefinitionOrigin.PublishedMetadata
            ? document?.Json ?? seedFactory.CreateSeedJson(definition)
            : seedFactory.CreateRuntimeJson(definition.Root, definition.SectionPath);
    }

    private ConfigurationEffectiveValue MaterializeValue(
        ConfigurationDefinition definition,
        LogicalPath logicalPath,
        ConfigurationNodeDefinition? targetNode,
        ConfigurationEffectiveValueDocument? document,
        ConfigurationStoredValue? snapshotStoredValue,
        ConfigurationSourceValue? runtimeSourceValue,
        bool includeSensitive)
    {
        var isSensitive = ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, logicalPath)
                          || targetNode is not null && ContainsSensitiveValue(targetNode);
        var configurationPath = targetNode?.ConfigurationPath
                                ?? ProjectPath(definition, logicalPath);

        if (runtimeSourceValue is not null)
        {
            return new ConfigurationEffectiveValue
            {
                DefinitionKey = definition.DefinitionKey,
                LogicalPath = logicalPath,
                ConfigurationPath = configurationPath,
                DisplayValue = !includeSensitive && (isSensitive || runtimeSourceValue.IsSensitive)
                    ? null
                    : runtimeSourceValue.DisplayValue,
                IsSensitive = isSensitive || runtimeSourceValue.IsSensitive,
                IsUnpersisted = runtimeSourceValue.IsUnpersisted,
                Version = !runtimeSourceValue.IsUnpersisted && runtimeSourceValue.Source.Kind == ConfigurationSourceKind.MonicaEffectiveStore
                    ? document?.Version
                    : null,
                EffectiveSource = runtimeSourceValue.Source
            };
        }

        return new ConfigurationEffectiveValue
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = logicalPath,
            ConfigurationPath = configurationPath,
            DisplayValue = isSensitive && !includeSensitive ? null : ToDisplayValue(snapshotStoredValue, targetNode),
            IsSensitive = isSensitive,
            IsUnpersisted = IsUnpersisted(definition),
            Version = IsUnpersisted(definition) ? null : document?.Version,
            EffectiveSource = definition.Origin == ConfigurationDefinitionOrigin.PublishedMetadata
                              && targetNode?.NodeKind == ConfigurationNodeKind.Scalar
                ? BuildEffectiveStoreSource()
                : null
        };
    }

    private ConfigurationSourceValue?[]? ReadRuntimeSourceValues(
        ConfigurationDefinition definition,
        IReadOnlyList<ConfigurationNodeDefinition> stateNodes,
        bool includeSensitive)
    {
        if (definition.Origin != ConfigurationDefinitionOrigin.LocalScan)
        {
            return null;
        }

        var scalarNodes = stateNodes
            .Select((node, index) => (Node: node, Index: index))
            .Where(static item => item.Node.NodeKind == ConfigurationNodeKind.Scalar)
            .ToArray();
        if (scalarNodes.Length == 0)
        {
            return new ConfigurationSourceValue?[stateNodes.Count];
        }

        var sourceChains = sourceInspector.GetSourceChains(
            definition,
            scalarNodes.Select(static item => item.Node.RelativePath).ToArray(), includeSensitive);
        if (sourceChains.Count != scalarNodes.Length)
        {
            throw new InvalidOperationException(
                "The source inspector returned a different number of source chains than requested paths.");
        }

        var values = new ConfigurationSourceValue?[stateNodes.Count];
        for (var index = 0; index < scalarNodes.Length; index++)
        {
            values[scalarNodes[index].Index] = sourceChains[index].Values.FirstOrDefault(value => value.IsEffective);
        }

        return values;
    }

    private ConfigurationSourceDescriptor BuildEffectiveStoreSource()
    {
        return new ConfigurationSourceDescriptor
        {
            SourceKey = EffectiveValueStore.Descriptor.StoreKey,
            DisplayName = EffectiveValueStore.Descriptor.DisplayName,
            ProviderType = EffectiveValueStore.Descriptor.Kind.ToString(),
            Kind = ConfigurationSourceKind.MonicaEffectiveStore,
            IsManagedByMonica = true,
            IsWritable = EffectiveValueStore.Descriptor.IsWritable
        };
    }

    private static string ProjectPath(ConfigurationDefinition definition, LogicalPath logicalPath)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.SectionPath))
        {
            parts.Add(definition.SectionPath);
        }

        parts.AddRange(logicalPath.Segments.Select(segment => segment.Value));
        return string.Join(':', parts);
    }

    internal static string? ToDisplayValue(ConfigurationStoredValue? storedValue, ConfigurationNodeDefinition? node)
    {
        if (storedValue is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(storedValue.Json);
            if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (node?.NodeKind == ConfigurationNodeKind.Scalar)
            {
                var displayValue = document.RootElement.ValueKind == JsonValueKind.String
                    ? document.RootElement.GetString()
                    : document.RootElement.GetRawText();
                return node.IsRegexPatternText
                    ? ConfigurationRegexTextCodec.NormalizePattern(displayValue)
                    : displayValue;
            }

            if (node is not null)
            {
                var normalized = ConfigurationRegexTextCodec.NormalizeJsonNode(
                    node,
                    JsonNode.Parse(document.RootElement.GetRawText()));
                return normalized?.ToJsonString(ConfigurationPersistedJsonOptions.ReadableValue)
                       ?? JsonSerializer.Serialize(document.RootElement, ConfigurationPersistedJsonOptions.ReadableValue);
            }

            return JsonSerializer.Serialize(document.RootElement, ConfigurationPersistedJsonOptions.ReadableValue);
        }
        catch (JsonException)
        {
            return storedValue.Json;
        }
    }

    private static IEnumerable<ConfigurationNodeDefinition> EnumerateNodes(ConfigurationNodeDefinition node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    internal static bool ContainsSensitiveValue(ConfigurationNodeDefinition node)
    {
        if (node.IsSensitive || node.Children.Any(ContainsSensitiveValue))
        {
            return true;
        }

        return node.DictionaryTemplate?.ValueTemplate is { } dictionaryValue
                   && ContainsSensitiveValue(dictionaryValue)
               || node.ListTemplate?.ItemTemplate is { } listItem
                   && ContainsSensitiveValue(listItem);
    }
}
