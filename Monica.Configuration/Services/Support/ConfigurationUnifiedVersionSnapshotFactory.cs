using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Models;
using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.Configuration.Services.Support;

internal sealed class ConfigurationUnifiedVersionSnapshotFactory(
    ConfigurationDefinitionResolver definitionResolver,
    ConfigurationEffectiveSnapshotReader effectiveSnapshotReader,
    IConfigurationSourceInspector sourceInspector,
    ConfigurationRuntimeContext runtimeContext,
    IEnumerable<IConfigurationUnifiedVersionFilter> filters,
    IOptions<ModuleConfigurationOption> options)
{
    private const string EFFECTIVE_SOURCE_KEY = "monica:effective";
    private const string EFFECTIVE_SOURCE_DISPLAY_NAME = "Monica Effective Store";
    private const int MAX_RUNTIME_SNAPSHOT_ATTEMPTS = 3;

    public bool IsEnabled => options.Value.UnifiedVersionControl.Enabled;

    public async Task<ConfigurationUnifiedVersionCreateRequest?> CreateRequestAsync(
        IReadOnlyList<string> triggerDefinitionKeys,
        string? mutationGroupId,
        ConfigurationMutationContext context,
        DateTimeOffset createdTime,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var selectedDefinitions = await GetSelectedDefinitionsAsync(cancellationToken);
        if (selectedDefinitions.Count == 0)
        {
            return null;
        }

        var normalizedTriggerKeys = triggerDefinitionKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedKeys = selectedDefinitions
            .Select(static definition => definition.DefinitionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedTriggerKeys.Length > 0 && normalizedTriggerKeys.All(key => !selectedKeys.Contains(key)))
        {
            return null;
        }

        var snapshots = await CaptureStableSnapshotsAsync(selectedDefinitions, cancellationToken);

        return new ConfigurationUnifiedVersionCreateRequest
        {
            MutationGroupId = mutationGroupId,
            TriggerDefinitionKeys = normalizedTriggerKeys,
            CreatedTime = createdTime,
            ModifierId = context.ModifierId,
            ModifierName = context.ModifierName,
            Reason = context.Reason,
            Definitions = snapshots
        };
    }

    private async Task<IReadOnlyList<ConfigurationUnifiedVersionDefinitionSnapshot>> CaptureStableSnapshotsAsync(
        IReadOnlyList<ConfigurationDefinition> selectedDefinitions,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MAX_RUNTIME_SNAPSHOT_ATTEMPTS; attempt++)
        {
            var reloadToken = runtimeContext.Root?.GetReloadToken();
            var first = await CaptureSnapshotsAsync(selectedDefinitions, cancellationToken);
            if (reloadToken?.HasChanged is true)
            {
                continue;
            }

            var second = await CaptureSnapshotsAsync(selectedDefinitions, cancellationToken);
            if (reloadToken?.HasChanged is not true && SnapshotsAreEquivalent(first, second))
            {
                return second;
            }
        }

        throw new InvalidOperationException(
            "Runtime configuration kept reloading while a unified-version snapshot was being captured. Retry after the providers stabilize.");
    }

    private async Task<IReadOnlyList<ConfigurationUnifiedVersionDefinitionSnapshot>> CaptureSnapshotsAsync(
        IReadOnlyList<ConfigurationDefinition> selectedDefinitions,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<ConfigurationUnifiedVersionDefinitionSnapshot>(selectedDefinitions.Count);
        var effectiveSnapshots = await effectiveSnapshotReader.ReadManyAsync(selectedDefinitions, cancellationToken);
        for (var index = 0; index < selectedDefinitions.Count; index++)
        {
            var definition = selectedDefinitions[index];
            var effectiveSnapshot = effectiveSnapshots[index];
            var isPublishedDefinition = definition.Origin == ConfigurationDefinitionOrigin.PublishedMetadata;
            snapshots.Add(new ConfigurationUnifiedVersionDefinitionSnapshot
            {
                DefinitionKey = definition.DefinitionKey,
                DisplayName = definition.DisplayName,
                Category = definition.Category,
                FromProject = definition.FromProject,
                SchemaVersion = definition.SchemaVersion,
                SchemaHash = definition.SchemaHash,
                EffectiveValueVersion = effectiveSnapshot.Version,
                Json = effectiveSnapshot.Json,
                SourceContributions = isPublishedDefinition
                    ? CapturePersistedSourceContribution(
                        definition,
                        effectiveSnapshot.RequirePersistedDocument(definition))
                    : CaptureRuntimeSourceContributions(definition)
            });
        }

        return snapshots;
    }

    private static bool SnapshotsAreEquivalent(
        IReadOnlyList<ConfigurationUnifiedVersionDefinitionSnapshot> left,
        IReadOnlyList<ConfigurationUnifiedVersionDefinitionSnapshot> right)
    {
        return left.Count == right.Count
               && left.Zip(right).All(pair =>
                   string.Equals(pair.First.DefinitionKey, pair.Second.DefinitionKey, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(pair.First.SchemaHash, pair.Second.SchemaHash, StringComparison.Ordinal)
                   && pair.First.SchemaVersion == pair.Second.SchemaVersion
                   && pair.First.EffectiveValueVersion == pair.Second.EffectiveValueVersion
                   && ConfigurationJsonSemanticComparer.Equals(pair.First.Json, pair.Second.Json)
                   && pair.First.SourceContributions.SequenceEqual(pair.Second.SourceContributions));
    }

    public async Task<IReadOnlyList<ConfigurationDefinition>> GetSelectedDefinitionsAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return [];
        }

        var activeFilters = filters.ToArray();
        if (activeFilters.Length == 0)
        {
            throw new InvalidOperationException(
                "Unified configuration version control is enabled, but no definition filters were registered. " +
                "Call IncludeUnifiedVersionCategories, IncludeUnifiedVersionDefinitions, or UseUnifiedVersionFilter.");
        }

        var definitions = await definitionResolver.GetMergedDefinitionsAsync(cancellationToken);
        return definitions
            .Where(definition => definition.Kind == ConfigurationKind.Managed && activeFilters.Any(filter => filter.ShouldInclude(definition)))
            .OrderBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<ConfigurationUnifiedVersionSourceContribution> CaptureRuntimeSourceContributions(
        ConfigurationDefinition definition)
    {
        try
        {
            return sourceInspector.GetDefinitionContributions(definition)
                .Select(contribution => new ConfigurationUnifiedVersionSourceContribution
                {
                    SourceKey = contribution.Source.SourceKey,
                    DisplayName = contribution.Source.DisplayName,
                    Kind = contribution.Source.Kind,
                    SuppliedValueCount = contribution.SuppliedValueCount,
                    EffectiveValueCount = contribution.EffectiveValueCount
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<ConfigurationUnifiedVersionSourceContribution> CapturePersistedSourceContribution(
        ConfigurationDefinition definition,
        ConfigurationEffectiveValueDocument document)
    {
        var valueCount = ConfigurationRollbackChangePlanner.EnumerateLeaves(definition.Root, document.Json).Count;
        if (valueCount == 0)
        {
            return [];
        }

        return
        [
            new ConfigurationUnifiedVersionSourceContribution
            {
                SourceKey = EFFECTIVE_SOURCE_KEY,
                DisplayName = EFFECTIVE_SOURCE_DISPLAY_NAME,
                Kind = ConfigurationSourceKind.MonicaEffectiveStore,
                SuppliedValueCount = valueCount,
                EffectiveValueCount = valueCount
            }
        ];
    }
}
