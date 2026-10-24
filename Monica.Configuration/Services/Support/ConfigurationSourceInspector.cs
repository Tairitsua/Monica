using Monica.Core.Configuration;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Binding;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Serialization;
using Monica.Modules;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Inspects the active Microsoft configuration root and resolves source chains for Monica definitions.
/// </summary>
internal sealed class ConfigurationSourceInspector(
    ConfigurationRuntimeContext runtimeContext,
    IConfigurationDefinitionRegistry definitionRegistry,
    ConfigurationPathProjector pathProjector,
    IOptions<ModuleConfigurationOption> moduleOptions,
    ConfigurationDefinitionResolver? definitionResolver = null)
    : IConfigurationSourceInspector
{
    private static readonly JsonSerializerOptions READABLE_JSON_OPTIONS = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions JSON_DOCUMENT_OPTIONS = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Lists all runtime configuration sources.
    /// </summary>
    public IReadOnlyList<ConfigurationSourceDescriptor> GetSources()
    {
        if (runtimeContext.Root is not { } root)
        {
            return [];
        }

        var managedSources = ManagedJsonConfigurationSourceRegistry.Get(runtimeContext.Configuration);
        return root.Providers
            .Select((provider, index) => BuildDescriptor(provider, index, managedSources))
            .ToArray();
    }

    /// <summary>
    /// Gets one source descriptor.
    /// </summary>
    public ConfigurationSourceDescriptor GetRequiredSource(string sourceKey)
    {
        return GetSources().FirstOrDefault(source => string.Equals(source.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Configuration source '{sourceKey}' was not found.");
    }

    /// <summary>
    /// Gets all source values for one managed configuration path.
    /// </summary>
    public ConfigurationSourceChain GetSourceChain(ConfigurationDefinition definition, LogicalPath logicalPath, bool includeSensitive = false)
    {
        return GetSourceChains(definition, [logicalPath], includeSensitive)[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<ConfigurationSourceChain> GetSourceChains(
        ConfigurationDefinition definition,
        IReadOnlyList<LogicalPath> logicalPaths,
        bool includeSensitive = false)
    {
        if (definition.Kind != ConfigurationKind.Startup)
            return GetSourceChainsCore(definition, logicalPaths, includeSensitive, runtimeContext.Root);
        var startup = runtimeContext.StartupOptions
            ?? throw new InvalidOperationException("Startup configuration was not initialized during composition.");
        var candidate = startup.ReadCandidate(definition);
        var actual = GetSourceChainsCore(definition, logicalPaths, includeSensitive, startup.Snapshot);
        var current = GetSourceChainsCore(definition, logicalPaths, includeSensitive, candidate.Sources);
        return logicalPaths.Select((path, index) => current[index] with
        {
            Kind = ConfigurationKind.Startup,
            StartupValues = actual[index].Values,
            StartupActualValue = startup.ProjectValue(definition, path, candidate, actual[index], current[index], includeSensitive)
        }).ToArray();
    }

    private IReadOnlyList<ConfigurationSourceChain> GetSourceChainsCore(ConfigurationDefinition definition,
        IReadOnlyList<LogicalPath> logicalPaths, bool includeSensitive, IConfigurationRoot? root)
    {
        if (logicalPaths.Count == 0)
        {
            return [];
        }

        if (root is null)
        {
            return logicalPaths
                .Select(logicalPath => EmptySourceChain(
                    definition,
                    logicalPath,
                    pathProjector.Project(definition.SectionPath, logicalPath)))
                .ToArray();
        }

        var managedSources = ManagedJsonConfigurationSourceRegistry.Get(runtimeContext.Configuration);
        var descriptors = root.Providers.Select((provider, index) => BuildDescriptor(provider, index, managedSources))
            .ToDictionary(source => source.PriorityIndex);
        var providers = root.Providers.ToArray();
        var sources = new List<RuntimeConfigurationSource>(descriptors.Count);
        foreach (var (provider, index) in providers.Select((provider, index) => (provider, index)))
        {
            // Chained providers are aliases over another IConfiguration, not leaf sources. Querying them as direct
            // value sources can re-enter the active root and block source-chain reads.
            if (provider is not ChainedConfigurationProvider
                && descriptors.TryGetValue(index, out var descriptor))
            {
                sources.Add(new RuntimeConfigurationSource(provider, descriptor));
            }
        }

        return logicalPaths
            .Select(logicalPath => BuildSourceChain(
                definition,
                logicalPath,
                pathProjector.Project(definition.SectionPath, logicalPath),
                sources,
                providers,
                root, includeSensitive))
            .ToArray();
    }

    /// <inheritdoc />
    public string GetRuntimeProjectionRevision(ConfigurationDefinition definition, string sourceKey)
    {
        if (runtimeContext.Root is not { } root)
        {
            throw new InvalidOperationException("The runtime configuration root is not available.");
        }

        var source = GetRequiredSource(sourceKey);
        var provider = root.Providers.ElementAtOrDefault(source.PriorityIndex)
                       ?? throw new KeyNotFoundException($"Configuration source '{sourceKey}' is no longer active.");
        if (provider is ChainedConfigurationProvider)
        {
            throw new InvalidOperationException("Chained configuration aliases do not have a physical source projection revision.");
        }
        return ConfigurationProviderProjectionRevision.Compute(provider, definition);
    }

    /// <summary>
    /// Gets source contribution counts for a configuration definition.
    /// </summary>
    public IReadOnlyList<ConfigurationDefinitionSourceContribution> GetDefinitionContributions(ConfigurationDefinition definition)
    {
        if (runtimeContext.Root is not { } root)
        {
            return [];
        }

        var sources = GetSources().Select(source => definition.Kind == ConfigurationKind.Startup ? source with
        {
            IsWritable = false,
            ReadOnlyReason = "Startup configuration is deployment-owned. Change deployment inputs and restart the process."
        } : source).ToDictionary(source => source.PriorityIndex);
        var providers = root.Providers.ToArray();
        var counters = new Dictionary<string, ContributionCounter>(StringComparer.OrdinalIgnoreCase);
        var effectivePriorityByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<SourceContributionEntry>();

        foreach (var (provider, index) in providers.Select((provider, index) => (provider, index)))
        {
            if (!sources.TryGetValue(index, out var source))
            {
                continue;
            }

            var suppliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (configurationPath, _) in EnumerateProviderValues(provider, definition.SectionPath))
            {
                if (!suppliedPaths.Add(configurationPath) || ResolveNodeFromConfigurationPath(definition, configurationPath) is null)
                {
                    continue;
                }

                var counter = GetOrCreateCounter(counters, source);
                counter.SuppliedValueCount++;
                entries.Add(new SourceContributionEntry(source.SourceKey, index, configurationPath));

                if (!effectivePriorityByPath.TryGetValue(configurationPath, out var currentPriority) || index > currentPriority)
                {
                    effectivePriorityByPath[configurationPath] = index;
                }
            }
        }

        foreach (var entry in entries)
        {
            if (effectivePriorityByPath.TryGetValue(entry.ConfigurationPath, out var priority)
                && priority == entry.PriorityIndex
                && !IsHiddenByHigherProvider(entry.ConfigurationPath, priority, providers, root)
                && counters.TryGetValue(entry.SourceKey, out var counter))
            {
                counter.EffectiveValueCount++;
            }
        }

        return counters.Values
            .OrderByDescending(counter => counter.Source.PriorityIndex)
            .Select(counter => new ConfigurationDefinitionSourceContribution
            {
                Source = counter.Source,
                SuppliedValueCount = counter.SuppliedValueCount,
                EffectiveValueCount = counter.EffectiveValueCount
            })
            .ToArray();
    }

    /// <summary>
    /// Gets all visible configuration values supplied by each runtime source.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationSourceInventory>> GetSourceInventoriesAsync(
        CancellationToken cancellationToken, bool includeSensitive = false)
    {
        if (runtimeContext.Root is not { } root)
        {
            return [];
        }

        var definitions = await GetKnownDefinitionsAsync(cancellationToken);
        var sources = GetSources();
        var providers = root.Providers.ToArray();
        var sourcesByPriority = sources.ToDictionary(source => source.PriorityIndex);
        var builders = sources.ToDictionary(
            source => source.SourceKey,
            source => new SourceInventoryBuilder(source),
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<SourceInventoryEntry>();
        var effectivePriorityByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var includeUnmanaged = moduleOptions.Value.IncludeUnmanagedSourceInventoryItems;

        foreach (var (provider, index) in providers.Select((provider, index) => (provider, index)))
        {
            if (!sourcesByPriority.TryGetValue(index, out var source))
            {
                continue;
            }

            var suppliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                foreach (var (configurationPath, value) in EnumerateProviderValues(provider, definition.SectionPath))
                {
                    var node = ResolveNodeFromConfigurationPath(definition, configurationPath);
                    var isSensitive = IsSensitiveConfigurationPath(definition, configurationPath);
                    if (!suppliedPaths.Add(configurationPath))
                    {
                        PreserveMostRestrictiveSensitivity(
                            entries,
                            source.SourceKey,
                            configurationPath,
                            isSensitive, includeSensitive);
                        continue;
                    }

                    entries.Add(new SourceInventoryEntry(
                        source.SourceKey,
                        index,
                        new ConfigurationSourceInventoryItem
                        {
                            DefinitionKey = definition.DefinitionKey,
                            DefinitionDisplayName = definition.DisplayName,
                            ConfigurationPath = configurationPath,
                            RelativeConfigurationPath = RelativeConfigurationPath(definition.SectionPath, configurationPath),
                            NodeLabel = node?.DisplayName ?? node?.Name,
                            DisplayValue = isSensitive && !includeSensitive ? null : value,
                            IsSensitive = isSensitive
                        }));

                    if (!effectivePriorityByPath.TryGetValue(configurationPath, out var currentPriority) || index > currentPriority)
                    {
                        effectivePriorityByPath[configurationPath] = index;
                    }
                }
            }

            if (!includeUnmanaged)
            {
                continue;
            }

            foreach (var (configurationPath, value) in EnumerateProviderValues(provider, null))
            {
                if (!suppliedPaths.Add(configurationPath))
                {
                    continue;
                }

                entries.Add(new SourceInventoryEntry(
                    source.SourceKey,
                    index,
                    new ConfigurationSourceInventoryItem
                    {
                        IsManagedByMonica = false,
                        ConfigurationPath = configurationPath,
                        RelativeConfigurationPath = configurationPath,
                        DisplayValue = value
                    }));

                if (!effectivePriorityByPath.TryGetValue(configurationPath, out var currentPriority) || index > currentPriority)
                {
                    effectivePriorityByPath[configurationPath] = index;
                }
            }
        }

        foreach (var entry in entries)
        {
            var isEffective = effectivePriorityByPath.TryGetValue(entry.Item.ConfigurationPath, out var priority)
                              && priority == entry.PriorityIndex
                              && !IsHiddenByHigherProvider(entry.Item.ConfigurationPath, priority, providers, root);
            builders[entry.SourceKey].Add(entry.Item with { IsEffective = isEffective });
        }

        return builders.Values
            .OrderByDescending(builder => builder.Source.PriorityIndex)
            .Select(builder => builder.Build())
            .ToArray();
    }

    private static void PreserveMostRestrictiveSensitivity(
        IList<SourceInventoryEntry> entries,
        string sourceKey,
        string configurationPath,
        bool isSensitive,
        bool includeSensitive)
    {
        if (!isSensitive)
        {
            return;
        }

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var existing = entries[index];
            if (!string.Equals(existing.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    existing.Item.ConfigurationPath,
                    configurationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries[index] = existing with
            {
                Item = existing.Item with
                {
                    DisplayValue = includeSensitive ? existing.Item.DisplayValue : null,
                    IsSensitive = true
                }
            };
            return;
        }
    }

    /// <summary>
    /// Gets a display-safe JSON file view.
    /// </summary>
    public async Task<ConfigurationSourceFileView> GetSourceFileViewAsync(string sourceKey, CancellationToken cancellationToken, bool includeSensitive = false)
    {
        var source = GetRequiredSource(sourceKey);
        if (source.Kind != ConfigurationSourceKind.JsonFile || string.IsNullOrWhiteSpace(source.PhysicalPath))
        {
            throw new InvalidOperationException($"Configuration source '{source.DisplayName}' is not a readable JSON file source.");
        }

        var text = File.Exists(source.PhysicalPath)
            ? await File.ReadAllTextAsync(source.PhysicalPath, cancellationToken)
            : "{}";
        ConfigurationJsonStructureValidator.ValidateNoCaseInsensitiveDuplicates(
            text,
            JSON_DOCUMENT_OPTIONS,
            source.DisplayName);
        var definitions = await GetKnownDefinitionsAsync(cancellationToken);
        var root = NormalizeKnownRegexPatternPaths(
            JsonNode.Parse(text, documentOptions: JSON_DOCUMENT_OPTIONS) ?? new JsonObject(),
            definitions);
        var redacted = false;
        if (!includeSensitive)
            root = RedactKnownSensitivePaths(root, definitions, out redacted);
        return new ConfigurationSourceFileView
        {
            Source = source,
            Content = root.ToJsonString(READABLE_JSON_OPTIONS),
            HasRedactions = redacted
        };
    }

    private static ConfigurationSourceChain EmptySourceChain(
        ConfigurationDefinition definition,
        LogicalPath logicalPath,
        string configurationPath)
    {
        return new ConfigurationSourceChain
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = logicalPath,
            ConfigurationPath = configurationPath,
            Values = []
        };
    }

    private static ConfigurationSourceChain BuildSourceChain(
        ConfigurationDefinition definition,
        LogicalPath logicalPath,
        string configurationPath,
        IReadOnlyList<RuntimeConfigurationSource> sources,
        IReadOnlyList<IConfigurationProvider> providers,
        IConfigurationRoot activeRoot,
        bool includeSensitive)
    {
        var targetNode = ConfigurationSchemaNavigator.ResolveNode(definition.Root, logicalPath);
        var isSensitive = ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, logicalPath);
        var hits = new List<ConfigurationSourceValue>();
        foreach (var source in sources)
        {
            var value = BuildSourceValue(
                source.Provider,
                source.Descriptor,
                targetNode,
                configurationPath,
                isSensitive, includeSensitive);
            if (value is null)
            {
                continue;
            }

            hits.Add(value with
            {
                IsUnpersisted = source.Provider is MonicaConfigurationProvider monica
                    && monica.IsUnpersisted(definition.DefinitionKey)
            });
        }

        var effectiveIndex = hits.Count == 0 ? -1 : hits.Max(hit => hit.Source.PriorityIndex);
        if (effectiveIndex >= 0 && IsHiddenByHigherProvider(configurationPath, effectiveIndex, providers, activeRoot))
        {
            effectiveIndex = -1;
        }
        return new ConfigurationSourceChain
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = logicalPath,
            ConfigurationPath = configurationPath,
            Values = hits
                .Select(hit => hit with { IsEffective = hit.Source.PriorityIndex == effectiveIndex })
                .OrderByDescending(hit => hit.Source.PriorityIndex)
                .ToArray()
        };
    }

    private static bool IsHiddenByHigherProvider(string configurationPath, int priorityIndex,
        IReadOnlyList<IConfigurationProvider> providers, IConfigurationRoot activeRoot)
    {
        for (var index = priorityIndex + 1; index < providers.Count; index++)
        {
            if (providers[index] is ChainedConfigurationProvider chain)
            {
                var leaves = new List<IConfigurationProvider>();
                // Prove the entire independent graph before querying any leaf. Active-root aliases, cycles and
                // opaque non-root chains cannot establish that a lower physical source is still the winner.
                var visited = new HashSet<IConfiguration>(ReferenceEqualityComparer.Instance) { activeRoot };
                if (!TryCollectIndependentChainProviders(chain.Configuration, leaves, visited)) return true;
                if (leaves.Any(provider => ConfigurationShapeView.HasBarrier(provider, configurationPath)
                    || provider.TryGet(configurationPath, out _)
                    || provider is IConfigurationValueShapeProvider shapes && shapes.TryGetShape(configurationPath, out _)
                    || provider.GetChildKeys([], configurationPath.Length == 0 ? null : configurationPath).Any())) return true;
                continue;
            }
            if (ConfigurationShapeView.HasBarrier(providers[index], configurationPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCollectIndependentChainProviders(IConfiguration? configuration,
        ICollection<IConfigurationProvider> leaves, ISet<IConfiguration> visited)
    {
        if (configuration is not IConfigurationRoot root || !visited.Add(root)) return false;
        try
        {
            foreach (var provider in root.Providers)
            {
                if (provider is ChainedConfigurationProvider chain)
                {
                    if (!TryCollectIndependentChainProviders(chain.Configuration, leaves, visited)) return false;
                }
                else leaves.Add(provider);
            }
            return true;
        }
        finally { visited.Remove(root); }
    }

    private sealed record RuntimeConfigurationSource(
        IConfigurationProvider Provider,
        ConfigurationSourceDescriptor Descriptor);

    private static ConfigurationSourceValue? BuildSourceValue(
        IConfigurationProvider provider,
        ConfigurationSourceDescriptor source,
        ConfigurationNodeDefinition? targetNode,
        string configurationPath,
        bool isSensitive,
        bool includeSensitive)
    {
        if (targetNode?.NodeKind == ConfigurationNodeKind.Scalar || targetNode is null)
        {
            return provider.TryGet(configurationPath, out var value)
                ? new ConfigurationSourceValue
                {
                    Source = source,
                    DisplayValue = isSensitive && !includeSensitive ? null : DisplayScalarValue(targetNode, value),
                    IsSensitive = isSensitive
                }
                : null;
        }

        if (provider is IConfigurationValueShapeProvider shapes
            && shapes.TryGetShape(configurationPath, out var shape))
        {
            var explicitContainer = shape switch
            {
                ConfigurationValueShape.Null => "null",
                ConfigurationValueShape.Object when !provider.GetChildKeys([], configurationPath).Any() => "{}",
                ConfigurationValueShape.List when !provider.GetChildKeys([], configurationPath).Any() => "[]",
                _ => null
            };
            if (explicitContainer is not null)
            {
                return new ConfigurationSourceValue
                {
                    Source = source,
                    DisplayValue = isSensitive && !includeSensitive ? null : explicitContainer,
                    IsSensitive = isSensitive
                };
            }
        }

        var node = BuildProviderJsonNode(provider, targetNode, configurationPath, includeSensitive);
        if (node is null)
        {
            return null;
        }

        return new ConfigurationSourceValue
        {
            Source = source,
            DisplayValue = isSensitive && !includeSensitive ? null : node.ToJsonString(READABLE_JSON_OPTIONS),
            IsSensitive = isSensitive
        };
    }

    private static JsonNode? BuildProviderJsonNode(
        IConfigurationProvider provider,
        ConfigurationNodeDefinition schema,
        string configurationPath,
        bool includeSensitive)
    {
        var node = schema.NodeKind switch
        {
            ConfigurationNodeKind.Scalar => BuildProviderScalarNode(provider, schema, configurationPath, includeSensitive),
            ConfigurationNodeKind.Object => BuildProviderObjectNode(provider, schema, configurationPath, includeSensitive),
            ConfigurationNodeKind.Dictionary => BuildProviderDictionaryNode(provider, schema, configurationPath, includeSensitive),
            ConfigurationNodeKind.List => BuildProviderListNode(provider, schema, configurationPath, includeSensitive),
            _ => null
        };
        return node is not null && schema.IsSensitive && !includeSensitive ? JsonValue.Create("***") : node;
    }

    private static JsonNode? BuildProviderScalarNode(
        IConfigurationProvider provider,
        ConfigurationNodeDefinition schema,
        string configurationPath,
        bool includeSensitive)
    {
        if (!provider.TryGet(configurationPath, out var value))
        {
            return null;
        }

        if (schema.IsSensitive && !includeSensitive)
        {
            return JsonValue.Create("***");
        }

        return schema.ValueKind switch
        {
            ConfigurationValueKind.Boolean when bool.TryParse(value, out var parsed) => JsonValue.Create(parsed),
            ConfigurationValueKind.Integer when long.TryParse(value, out var parsed) => JsonValue.Create(parsed),
            ConfigurationValueKind.Decimal when decimal.TryParse(value, out var parsed) => JsonValue.Create(parsed),
            ConfigurationValueKind.Floating when double.TryParse(value, out var parsed) => JsonValue.Create(parsed),
            _ => JsonValue.Create(ConfigurationRegexTextCodec.NormalizeDisplayValue(schema, value))
        };
    }

    private static JsonNode? BuildProviderObjectNode(
        IConfigurationProvider provider,
        ConfigurationNodeDefinition schema,
        string configurationPath,
        bool includeSensitive)
    {
        var result = new JsonObject();
        foreach (var child in schema.Children)
        {
            var childPath = string.IsNullOrWhiteSpace(configurationPath)
                ? child.Name
                : $"{configurationPath}:{child.Name}";
            var childNode = BuildProviderJsonNode(provider, child, childPath, includeSensitive);
            if (childNode is not null)
            {
                result[child.Name] = childNode;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonNode? BuildProviderDictionaryNode(
        IConfigurationProvider provider,
        ConfigurationNodeDefinition schema,
        string configurationPath,
        bool includeSensitive)
    {
        if (schema.DictionaryTemplate is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var childKey in GetOrderedProviderChildKeys(provider, configurationPath))
        {
            var childPath = $"{configurationPath}:{childKey}";
            var childNode = BuildProviderJsonNode(provider, schema.DictionaryTemplate.ValueTemplate, childPath, includeSensitive);
            if (childNode is not null)
            {
                result[childKey] = childNode;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonNode? BuildProviderListNode(
        IConfigurationProvider provider,
        ConfigurationNodeDefinition schema,
        string configurationPath,
        bool includeSensitive)
    {
        if (schema.ListTemplate is null)
        {
            return null;
        }

        var result = new JsonArray();
        foreach (var childKey in GetOrderedProviderChildKeys(provider, configurationPath))
        {
            var childPath = $"{configurationPath}:{childKey}";
            var childNode = BuildProviderJsonNode(provider, schema.ListTemplate.ItemTemplate, childPath, includeSensitive);
            if (childNode is not null)
            {
                result.Add(childNode);
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static IReadOnlyList<string> GetOrderedProviderChildKeys(IConfigurationProvider provider, string configurationPath)
    {
        return provider.GetChildKeys([], configurationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => int.TryParse(key, out var index) ? index : int.MaxValue)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ConfigurationSourceDescriptor BuildDescriptor(
        IConfigurationProvider provider,
        int index,
        IReadOnlyList<ManagedJsonConfigurationSourceRegistration> managedSources)
    {
        if (provider is ConfigurationProviderSnapshot snapshot)
        {
            var origin = snapshot.Origin;
            var type = origin.ProviderType;
            return new ConfigurationSourceDescriptor
            {
                SourceKey = $"startup:{origin.ProviderPath}",
                PriorityIndex = index,
                DisplayName = origin.SourcePath is { Length: > 0 } path ? Path.GetFileName(path) : type.Name,
                ProviderType = type.FullName ?? type.Name,
                Kind = typeof(JsonConfigurationProvider).IsAssignableFrom(type) ? ConfigurationSourceKind.JsonFile
                    : typeof(EnvironmentVariablesConfigurationProvider).IsAssignableFrom(type) ? ConfigurationSourceKind.EnvironmentVariables
                    : typeof(CommandLineConfigurationProvider).IsAssignableFrom(type) ? ConfigurationSourceKind.CommandLine
                    : typeof(MemoryConfigurationProvider).IsAssignableFrom(type) ? ConfigurationSourceKind.Memory
                    : ConfigurationSourceKind.Unsupported,
                IsWritable = false,
                ReadOnlyReason = "Startup configuration is deployment-owned. Change deployment inputs and restart the process.",
                SourcePath = origin.SourcePath,
                PhysicalPath = origin.PhysicalPath,
                Optional = origin.Optional,
                ReloadOnChange = origin.ReloadOnChange
            };
        }
        if (provider is MonicaConfigurationProvider)
        {
            return new ConfigurationSourceDescriptor
            {
                SourceKey = "monica:effective",
                PriorityIndex = index,
                DisplayName = "Monica Effective Store",
                ProviderType = provider.GetType().Name,
                Kind = ConfigurationSourceKind.MonicaEffectiveStore,
                IsWritable = true,
                Description = "Projected values from Monica.Configuration effective-value store."
            };
        }

        if (provider is JsonConfigurationProvider jsonProvider)
        {
            return BuildJsonDescriptor(jsonProvider, index, managedSources);
        }

        var providerType = provider.GetType();
        var kind = provider switch
        {
            EnvironmentVariablesConfigurationProvider => ConfigurationSourceKind.EnvironmentVariables,
            CommandLineConfigurationProvider => ConfigurationSourceKind.CommandLine,
            MemoryConfigurationProvider => ConfigurationSourceKind.Memory,
            _ => ConfigurationSourceKind.Unsupported
        };

        return new ConfigurationSourceDescriptor
        {
            SourceKey = $"provider:{index}",
            PriorityIndex = index,
            DisplayName = providerType.Name,
            ProviderType = providerType.FullName ?? providerType.Name,
            Kind = kind,
            IsWritable = false,
            ReadOnlyReason = "This provider type is not supported for UI mutation."
        };
    }

    private ConfigurationSourceDescriptor BuildJsonDescriptor(
        JsonConfigurationProvider provider,
        int index,
        IReadOnlyList<ManagedJsonConfigurationSourceRegistration> managedSources)
    {
        var source = provider.Source;
        var sourcePath = source.Path ?? string.Empty;
        var physicalPath = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : source.FileProvider?.GetFileInfo(sourcePath)?.PhysicalPath;
        var normalizedPhysicalPath = string.IsNullOrWhiteSpace(physicalPath) ? null : Path.GetFullPath(physicalPath);
        var managed = FindManagedRegistration(sourcePath, normalizedPhysicalPath, managedSources);
        var deploymentOwned = provider is IDeploymentConfigurationProvider;
        var canWrite = runtimeContext.AutonomousWork && !deploymentOwned && !string.IsNullOrWhiteSpace(normalizedPhysicalPath)
                       && CanWriteJsonFile(normalizedPhysicalPath)
                       && (managed?.IsWritable ?? true);

        return new ConfigurationSourceDescriptor
        {
            SourceKey = normalizedPhysicalPath is null ? $"json:{index}" : $"json:{index}:{Hash(normalizedPhysicalPath)}",
            PriorityIndex = index,
            DisplayName = managed?.DisplayName ?? Path.GetFileName(sourcePath),
            ProviderType = provider.GetType().FullName ?? provider.GetType().Name,
            Kind = ConfigurationSourceKind.JsonFile,
            IsManagedByMonica = managed is not null,
            IsWritable = canWrite,
            ReadOnlyReason = !runtimeContext.AutonomousWork
                ? "External JSON sources are read-only in Manual mode. Use the guarded Monica effective-store target."
                : deploymentOwned ? "Deployment configuration sources are read-only. Change the deployment input outside this process."
                : canWrite ? null : ResolveJsonReadOnlyReason(normalizedPhysicalPath, managed),
            SourcePath = sourcePath,
            PhysicalPath = normalizedPhysicalPath,
            Optional = source.Optional,
            ReloadOnChange = source.ReloadOnChange,
            Description = managed?.Description
        };
    }

    private static ManagedJsonConfigurationSourceRegistration? FindManagedRegistration(
        string sourcePath,
        string? physicalPath,
        IReadOnlyList<ManagedJsonConfigurationSourceRegistration> registrations)
    {
        return registrations.FirstOrDefault(registration =>
            string.Equals(registration.Path, sourcePath, StringComparison.OrdinalIgnoreCase)
            || physicalPath is not null
            && string.Equals(Path.GetFullPath(registration.Path), physicalPath, StringComparison.OrdinalIgnoreCase));
    }

    // Writability of one physical path rarely changes within a process lifetime, and descriptors are
    // rebuilt on every GetSources call, so probe results are cached per path.
    private static readonly ConcurrentDictionary<string, bool> WritableProbeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Probes whether a JSON file path is actually writable, beyond its directory existing.
    /// Read-only bind mounts (for example ConfigMap subPath volumes) must not register as writable
    /// mutation destinations that would always fail at write time. Results are cached per path.
    /// </summary>
    internal static bool CanWriteJsonFile(string physicalPath)
    {
        return WritableProbeCache.GetOrAdd(physicalPath, static path =>
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return false;
                }

                // A directory check alone misclassifies read-only bind mounts (for example ConfigMap
                // subPath volumes) as writable, which would let mutation planning offer destinations
                // that always fail at write time. Probe the actual file system instead.
                if (File.Exists(path))
                {
                    using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    return true;
                }

                var probePath = Path.Combine(directory, $".monica-write-probe-{Guid.NewGuid():N}.tmp");
                try
                {
                    using var _ = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    return true;
                }
                finally
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        });
    }

    private static string ResolveJsonReadOnlyReason(string? physicalPath, ManagedJsonConfigurationSourceRegistration? managed)
    {
        if (managed is { IsWritable: false })
        {
            return "This Monica-managed source was registered as read-only.";
        }

        return string.IsNullOrWhiteSpace(physicalPath)
            ? "The JSON provider does not expose a physical file path."
            : "The JSON file directory does not exist or is not writable.";
    }

    private static JsonNode RedactKnownSensitivePaths(
        JsonNode root,
        IReadOnlyList<ConfigurationDefinition> definitions,
        out bool hasRedactions)
    {
        hasRedactions = false;
        foreach (var definition in definitions)
        {
            root = RedactPath(
                root,
                SplitConfigurationPath(definition.SectionPath),
                definition.Root,
                out var definitionRedacted);
            hasRedactions |= definitionRedacted;
        }

        return root;
    }

    private static JsonNode NormalizeKnownRegexPatternPaths(
        JsonNode root,
        IReadOnlyList<ConfigurationDefinition> definitions)
    {
        var normalized = root;
        foreach (var definition in definitions)
        {
            normalized = NormalizePath(
                normalized,
                definition.SectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries),
                definition.Root);
        }

        return normalized;
    }

    private async Task<IReadOnlyList<ConfigurationDefinition>> GetKnownDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        return definitionResolver is null
            ? definitionRegistry.GetAll().ToArray()
            : await definitionResolver.GetMergedDefinitionsAsync(cancellationToken);
    }

    private static JsonNode NormalizePath(
        JsonNode root,
        IReadOnlyList<string> segments,
        ConfigurationNodeDefinition schema)
    {
        if (segments.Count == 0)
        {
            return ConfigurationRegexTextCodec.NormalizeJsonNode(schema, root) ?? root;
        }

        var current = root;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            current = GetPathChild(current, segments[index]);

            if (current is null)
            {
                return root;
            }
        }

        var last = segments[^1];
        if (current is JsonObject currentObject
            && TryGetCaseInsensitiveProperty(currentObject, last, out var actualName, out var objectValue)
            && objectValue is not null)
        {
            currentObject[actualName] = ConfigurationRegexTextCodec.NormalizeJsonNode(schema, objectValue);
        }
        else if (current is JsonArray currentArray
                 && int.TryParse(last, out var lastIndex)
                 && lastIndex >= 0
                 && lastIndex < currentArray.Count)
        {
            currentArray[lastIndex] = ConfigurationRegexTextCodec.NormalizeJsonNode(schema, currentArray[lastIndex]);
        }

        return root;
    }

    private static string? DisplayScalarValue(ConfigurationNodeDefinition? schema, string? value)
    {
        return value is null || schema is null ? value : ConfigurationRegexTextCodec.NormalizeDisplayValue(schema, value);
    }

    private static JsonNode RedactPath(
        JsonNode root,
        IReadOnlyList<string> segments,
        ConfigurationNodeDefinition schema,
        out bool redacted)
    {
        if (segments.Count == 0)
        {
            return RedactSchemaNode(root, schema, out redacted) ?? root;
        }

        var current = root;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            current = GetPathChild(current, segments[index]);

            if (current is null)
            {
                redacted = false;
                return root;
            }
        }

        var last = segments[^1];
        if (current is JsonObject currentObject
            && TryGetCaseInsensitiveProperty(currentObject, last, out var actualName, out var objectValue))
        {
            var replacement = RedactSchemaNode(objectValue, schema, out redacted);
            if (!ReferenceEquals(replacement, objectValue))
            {
                currentObject[actualName] = replacement;
            }

            return root;
        }

        if (current is JsonArray currentArray
            && int.TryParse(last, out var lastIndex)
            && lastIndex >= 0
            && lastIndex < currentArray.Count)
        {
            var currentValue = currentArray[lastIndex];
            var replacement = RedactSchemaNode(currentValue, schema, out redacted);
            if (!ReferenceEquals(replacement, currentValue))
            {
                currentArray[lastIndex] = replacement;
            }

            return root;
        }

        redacted = false;
        return root;
    }

    private static JsonNode? RedactSchemaNode(
        JsonNode? value,
        ConfigurationNodeDefinition schema,
        out bool redacted)
    {
        if (schema.IsSensitive)
        {
            redacted = true;
            return JsonValue.Create("***");
        }

        redacted = false;
        // Walk collection templates against every physical item; wildcard schema paths cannot identify those values directly.
        switch (value)
        {
            case JsonObject jsonObject when schema.NodeKind == ConfigurationNodeKind.Object:
                foreach (var child in schema.Children)
                {
                    if (!TryGetCaseInsensitiveProperty(jsonObject, child.Name, out var actualName, out var childValue))
                    {
                        continue;
                    }

                    var replacement = RedactSchemaNode(childValue, child, out var childRedacted);
                    redacted |= childRedacted;
                    if (!ReferenceEquals(replacement, childValue))
                    {
                        jsonObject[actualName] = replacement;
                    }
                }

                break;
            case JsonObject jsonObject when schema.NodeKind == ConfigurationNodeKind.Dictionary
                                            && schema.DictionaryTemplate is { } dictionaryTemplate:
                foreach (var pair in jsonObject.ToArray())
                {
                    var replacement = RedactSchemaNode(
                        pair.Value,
                        dictionaryTemplate.ValueTemplate,
                        out var itemRedacted);
                    redacted |= itemRedacted;
                    if (!ReferenceEquals(replacement, pair.Value))
                    {
                        jsonObject[pair.Key] = replacement;
                    }
                }

                break;
            case JsonArray jsonArray when schema.NodeKind == ConfigurationNodeKind.List
                                          && schema.ListTemplate is { } listTemplate:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var itemValue = jsonArray[index];
                    var replacement = RedactSchemaNode(itemValue, listTemplate.ItemTemplate, out var itemRedacted);
                    redacted |= itemRedacted;
                    if (!ReferenceEquals(replacement, itemValue))
                    {
                        jsonArray[index] = replacement;
                    }
                }

                break;
        }

        return value;
    }

    private static JsonNode? GetPathChild(JsonNode? parent, string segment)
    {
        if (parent is JsonObject jsonObject
            && TryGetCaseInsensitiveProperty(jsonObject, segment, out _, out var objectValue))
        {
            return objectValue;
        }

        if (parent is JsonArray jsonArray
            && int.TryParse(segment, out var arrayIndex)
            && arrayIndex >= 0
            && arrayIndex < jsonArray.Count)
        {
            return jsonArray[arrayIndex];
        }

        return null;
    }

    private static bool TryGetCaseInsensitiveProperty(
        JsonObject jsonObject,
        string propertyName,
        out string actualName,
        out JsonNode? value)
    {
        foreach (var property in jsonObject)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                value = property.Value;
                return true;
            }
        }

        actualName = string.Empty;
        value = null;
        return false;
    }

    private static IEnumerable<(string ConfigurationPath, string? Value)> EnumerateProviderValues(
        IConfigurationProvider provider,
        string? parentPath)
    {
        // Aliases are never attributed as physical supplied values or queried through the active root.
        if (provider is ChainedConfigurationProvider) yield break;
        var childKeys = provider.GetChildKeys([], parentPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        foreach (var childKey in childKeys)
        {
            var configurationPath = string.IsNullOrWhiteSpace(parentPath)
                ? childKey
                : $"{parentPath}:{childKey}";

            if (provider.TryGet(configurationPath, out var value))
            {
                yield return (configurationPath, value);
            }

            foreach (var descendant in EnumerateProviderValues(provider, configurationPath))
            {
                yield return descendant;
            }
        }
    }

    private static string RelativeConfigurationPath(string sectionPath, string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(sectionPath))
        {
            return configurationPath;
        }

        return configurationPath.Length <= sectionPath.Length
            ? string.Empty
            : configurationPath[(sectionPath.Length + 1)..];
    }

    private static ConfigurationNodeDefinition? ResolveNodeFromConfigurationPath(
        ConfigurationDefinition definition,
        string configurationPath)
    {
        var sectionSegments = SplitConfigurationPath(definition.SectionPath);
        var pathSegments = SplitConfigurationPath(configurationPath);
        if (pathSegments.Length < sectionSegments.Length || !HasPrefix(pathSegments, sectionSegments))
        {
            return null;
        }

        var current = definition.Root;
        for (var index = sectionSegments.Length; index < pathSegments.Length; index++)
        {
            var segment = pathSegments[index];
            current = current.NodeKind switch
            {
                ConfigurationNodeKind.Object => current.Children.FirstOrDefault(child =>
                    string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase)),
                ConfigurationNodeKind.Dictionary => current.DictionaryTemplate?.ValueTemplate,
                ConfigurationNodeKind.List => current.ListTemplate?.ItemTemplate,
                _ => null
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static bool IsSensitiveConfigurationPath(
        ConfigurationDefinition definition,
        string configurationPath)
    {
        var sectionSegments = SplitConfigurationPath(definition.SectionPath);
        var pathSegments = SplitConfigurationPath(configurationPath);
        if (pathSegments.Length < sectionSegments.Length || !HasPrefix(pathSegments, sectionSegments))
        {
            return false;
        }

        var current = definition.Root;
        if (current.IsSensitive)
        {
            return true;
        }

        for (var index = sectionSegments.Length; index < pathSegments.Length; index++)
        {
            current = current.NodeKind switch
            {
                ConfigurationNodeKind.Object => current.Children.FirstOrDefault(child =>
                    string.Equals(child.Name, pathSegments[index], StringComparison.OrdinalIgnoreCase)),
                ConfigurationNodeKind.Dictionary => current.DictionaryTemplate?.ValueTemplate,
                ConfigurationNodeKind.List => current.ListTemplate?.ItemTemplate,
                _ => null
            };

            if (current is null)
            {
                return false;
            }

            if (current.IsSensitive)
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitConfigurationPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasPrefix(IReadOnlyList<string> pathSegments, IReadOnlyList<string> prefixSegments)
    {
        for (var index = 0; index < prefixSegments.Count; index++)
        {
            if (!string.Equals(pathSegments[index], prefixSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private sealed class ContributionCounter(ConfigurationSourceDescriptor source)
    {
        public ConfigurationSourceDescriptor Source { get; } = source;

        public int SuppliedValueCount { get; set; }

        public int EffectiveValueCount { get; set; }
    }

    private static ContributionCounter GetOrCreateCounter(
        Dictionary<string, ContributionCounter> counters,
        ConfigurationSourceDescriptor source)
    {
        if (counters.TryGetValue(source.SourceKey, out var counter))
        {
            return counter;
        }

        counter = new ContributionCounter(source);
        counters[source.SourceKey] = counter;
        return counter;
    }

    private sealed record SourceContributionEntry(
        string SourceKey,
        int PriorityIndex,
        string ConfigurationPath);

    private sealed class SourceInventoryBuilder(ConfigurationSourceDescriptor source)
    {
        private readonly List<ConfigurationSourceInventoryItem> _items = [];

        public ConfigurationSourceDescriptor Source { get; } = source;

        public void Add(ConfigurationSourceInventoryItem item)
        {
            _items.Add(item);
        }

        public ConfigurationSourceInventory Build()
        {
            return new ConfigurationSourceInventory
            {
                Source = Source,
                SuppliedValueCount = _items.Count,
                EffectiveValueCount = _items.Count(item => item.IsEffective),
                ManagedValueCount = _items.Count(item => item.IsManagedByMonica),
                UnmanagedValueCount = _items.Count(item => !item.IsManagedByMonica),
                Items = _items
                    .OrderBy(item => item.IsManagedByMonica ? 0 : 1)
                    .ThenBy(item => item.DefinitionDisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.RelativeConfigurationPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    private sealed record SourceInventoryEntry(
        string SourceKey,
        int PriorityIndex,
        ConfigurationSourceInventoryItem Item);
}
