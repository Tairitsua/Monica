using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>
/// Default rollback service that translates inverse mutations into one coordinated mutation group.
/// </summary>
internal sealed class ConfigurationRollbackService(
    IConfigurationHistoryService historyService,
    IConfigurationMutationGroupApplyService mutationGroupApplyService,
    IConfigurationEffectiveValueStore effectiveValueStore,
    IConfigurationSourceInspector sourceInspector,
    IConfigurationJsonFileSourceWriter sourceWriter,
    IConfigurationMutationGroupService groupService,
    ConfigurationDefinitionResolver definitionResolver,
    ConfigurationEffectiveValueDocumentEditor documentEditor,
    ConfigurationPathProjector pathProjector,
    ILogger<ConfigurationRollbackService> logger)
    : IConfigurationRollbackService
{
    /// <inheritdoc />
    public async Task<ConfigurationHistoryRollbackPreview> PreviewHistoriesAsync(
        IReadOnlyList<string> historyIds,
        CancellationToken cancellationToken)
    {
        var rows = await GetRequiredHistoriesAsync(historyIds, cancellationToken);
        return (await BuildPlanAsync(rows, cancellationToken)).Preview;
    }

    /// <inheritdoc />
    public async Task<ConfigurationMutationResult> RollbackHistoryAsync(
        string historyId,
        string planToken,
        ConfigurationMutationContext context,
        CancellationToken cancellationToken)
    {
        var history = await GetRequiredHistoryAsync(historyId, cancellationToken);
        var applyResult = await ApplyRowsAsync(
            [history],
            $"Rollback {history.ModifiedTime:yyyy-MM-dd HH:mm:ss}",
            planToken,
            context,
            cancellationToken);

        return GetRequiredAppliedResults(applyResult).Single();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationMutationResult>> RollbackHistoriesAsync(
        IReadOnlyList<string> historyIds,
        string planToken,
        ConfigurationMutationContext context,
        CancellationToken cancellationToken)
    {
        var orderedRows = await GetRequiredHistoriesAsync(historyIds, cancellationToken);
        var applyResult = await ApplyRowsAsync(
            orderedRows,
            $"Rollback {orderedRows.Length} selected configuration changes",
            planToken,
            context,
            cancellationToken);

        return GetRequiredAppliedResults(applyResult);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationMutationResult>> RollbackGroupAsync(
        string groupId,
        string planToken,
        ConfigurationMutationContext context,
        CancellationToken cancellationToken)
    {
        var originalGroup = await groupService.GetAsync(groupId, cancellationToken)
            ?? throw new KeyNotFoundException($"Configuration mutation group '{groupId}' was not found.");
        var rows = await groupService.GetGroupHistoryAsync(groupId, cancellationToken);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Configuration mutation group '{groupId}' has no history rows to roll back.");
        }

        var orderedRows = rows
            .OrderByDescending(static row => row.ModifiedTime)
            .ThenByDescending(static row => row.Version)
            .ToArray();
        var applyResult = await ApplyRowsAsync(
            orderedRows,
            $"Rollback {originalGroup.Label}",
            planToken,
            context,
            cancellationToken);

        if (applyResult.Status == ConfigurationMutationGroupApplyStatus.Applied)
        {
            applyResult = await TryMarkOriginalGroupRolledBackAsync(groupId, applyResult, cancellationToken);
        }

        return GetRequiredAppliedResults(applyResult);
    }

    private async Task<ConfigurationMutationGroupApplyResult> ApplyRowsAsync(
        IReadOnlyList<ConfigurationValueHistory> rows,
        string label,
        string planToken,
        ConfigurationMutationContext context,
        CancellationToken cancellationToken)
    {
        var plan = await BuildPlanAsync(rows, cancellationToken);
        if (!string.Equals(plan.Preview.PlanToken, planToken, StringComparison.Ordinal))
        {
            throw new ConfigurationConcurrencyConflictException(
                "The rollback preview is stale because a target value, schema, or source revision changed. Review the rollback again before applying it.");
        }

        var commands = BuildCommands(rows, plan);
        return await mutationGroupApplyService.ApplyAsync(new ConfigurationMutationGroupApplyRequest
        {
            Label = label,
            Reason = context.Reason,
            Context = context with { MutationGroupId = null },
            Commands = commands,
            ExpectedValidationFingerprint = plan.Preview.ValidationFingerprint
        }, cancellationToken);
    }

    private static IReadOnlyList<ConfigurationMutationCommand> BuildCommands(
        IReadOnlyList<ConfigurationValueHistory> rows,
        HistoryRollbackPlan plan)
    {
        var commands = new List<ConfigurationMutationCommand>(rows.Count);

        // Replaying several selected histories for one exact path has the same final state as restoring the oldest
        // selected pre-mutation value once. Collapsing that chain avoids order-only intermediate writes and gives
        // external stores one auditable command that matches the reviewed preview.
        foreach (var chain in rows.GroupBy(BuildRollbackCommandKey, RollbackCommandKeyComparer.Instance))
        {
            var history = chain
                .OrderBy(static row => row.ModifiedTime)
                .ThenBy(static row => row.Version)
                .ThenBy(static row => row.HistoryId, StringComparer.OrdinalIgnoreCase)
                .First();
            commands.Add(new ConfigurationMutationCommand
            {
                RequestId = $"rollback:{history.HistoryId}",
                DefinitionKey = history.DefinitionKey,
                LogicalPath = history.LogicalPath,
                MutationKind = history.OldValue is null
                    ? ConfigurationMutationKind.Remove
                    : ConfigurationMutationKind.Set,
                Value = history.OldValue ?? ConfigurationStoredValue.Null,
                ExpectedSchemaVersion = history.SchemaVersion,
                ExpectedSchemaHash = history.SchemaHash
                    ?? throw new InvalidOperationException(
                        $"Configuration history '{history.HistoryId}' has no exact schema hash and cannot be rolled back safely."),
                Target = plan.TargetsByHistoryId[history.HistoryId]
            });
        }

        return commands;
    }

    private static RollbackCommandKey BuildRollbackCommandKey(ConfigurationValueHistory history)
    {
        if (history.TargetKind == ConfigurationMutationTargetKind.MonicaEffectiveStore)
        {
            return new RollbackCommandKey(
                history.TargetKind,
                PhysicalTarget: null,
                ConfigurationPath: null,
                history.DefinitionKey,
                history.LogicalPath.ToCanonicalString(),
                UniqueHistoryId: null);
        }

        if (string.IsNullOrWhiteSpace(history.SourcePhysicalPath)
            || string.IsNullOrWhiteSpace(history.SourceConfigurationPath))
        {
            return new RollbackCommandKey(
                history.TargetKind,
                PhysicalTarget: null,
                ConfigurationPath: null,
                history.DefinitionKey,
                history.LogicalPath.ToCanonicalString(),
                history.HistoryId);
        }

        return new RollbackCommandKey(
            history.TargetKind,
            history.SourcePhysicalPath,
            history.SourceConfigurationPath,
            history.DefinitionKey,
            history.LogicalPath.ToCanonicalString(),
            UniqueHistoryId: null);
    }

    private async Task<HistoryRollbackPlan> BuildPlanAsync(
        IReadOnlyList<ConfigurationValueHistory> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("At least one configuration history row is required for rollback preview.");
        }

        EnsureRollbackPreviewCanRepresent(rows);
        var definitions = await ResolveDefinitionsAsync(rows, cancellationToken);
        var preparedRows = PrepareRows(rows, definitions);
        var statesByHistoryId = new Dictionary<string, HistoryRollbackTargetState>(StringComparer.OrdinalIgnoreCase);
        await ReadEffectiveTargetStatesAsync(preparedRows, statesByHistoryId, cancellationToken);
        await ReadExternalTargetStatesAsync(preparedRows, statesByHistoryId, cancellationToken);

        var currentValues = new Dictionary<string, ConfigurationStoredValue?>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, ConfigurationMutationTarget>(StringComparer.OrdinalIgnoreCase);
        var tokenEntries = new List<HistoryRollbackPlanTokenEntry>(rows.Count);

        foreach (var prepared in preparedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = prepared.History;
            var state = statesByHistoryId[history.HistoryId];
            currentValues[history.HistoryId] = state.CurrentValue;
            targets[history.HistoryId] = state.Target;
            tokenEntries.Add(new HistoryRollbackPlanTokenEntry
            {
                HistoryId = history.HistoryId,
                DefinitionKey = history.DefinitionKey,
                LogicalPath = history.LogicalPath.ToCanonicalString(),
                SchemaVersion = history.SchemaVersion,
                SchemaHash = history.SchemaHash!,
                TargetKind = history.TargetKind,
                CurrentValueExists = state.CurrentValue is not null,
                CurrentJson = state.CurrentValue?.Json,
                RollbackValueExists = history.OldValue is not null,
                RollbackJson = history.OldValue?.Json,
                EffectiveVersion = state.EffectiveVersion,
                SourceKey = state.SourceKey,
                SourceConfigurationPath = state.SourceConfigurationPath,
                SourceRevision = state.SourceRevision
            });
        }

        var plan = new HistoryRollbackPlan(
            new ConfigurationHistoryRollbackPreview
            {
                PlanToken = ComputePlanToken(tokenEntries),
                CurrentValuesByHistoryId = currentValues
            },
            targets);
        var validation = await mutationGroupApplyService.PreviewAsync(new ConfigurationMutationGroupApplyRequest
        {
            Label = "Rollback validation preview",
            Commands = BuildCommands(rows, plan)
        }, cancellationToken);
        var tokenBytes = Encoding.UTF8.GetBytes($"{plan.Preview.PlanToken}|{validation.ValidationFingerprint}");
        return plan with
        {
            Preview = plan.Preview with
            {
                PlanToken = $"sha256:{Convert.ToHexString(SHA256.HashData(tokenBytes)).ToLowerInvariant()}",
                ValidationReports = validation.Definitions,
                Problems = validation.Problems,
                ValidationFingerprint = validation.ValidationFingerprint
            }
        };
    }

    private async Task<IReadOnlyDictionary<string, ConfigurationDefinition>> ResolveDefinitionsAsync(
        IReadOnlyList<ConfigurationValueHistory> rows,
        CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, ConfigurationDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definitionKey in rows
                     .Select(static row => row.DefinitionKey)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            definitions[definitionKey] = await definitionResolver.GetRequiredAsync(definitionKey, cancellationToken);
        }

        return definitions;
    }

    private IReadOnlyList<PreparedHistoryRollbackRow> PrepareRows(
        IReadOnlyList<ConfigurationValueHistory> rows,
        IReadOnlyDictionary<string, ConfigurationDefinition> definitions)
    {
        var sourcesByPhysicalPath = BuildJsonSourcesByPhysicalPath();
        var preparedRows = new List<PreparedHistoryRollbackRow>(rows.Count);
        foreach (var history in rows)
        {
            var definition = definitions[history.DefinitionKey];
            EnsureExactHistorySchema(history, definition);
            if (history.TargetKind != ConfigurationMutationTargetKind.ExternalConfigurationSource)
            {
                preparedRows.Add(new PreparedHistoryRollbackRow(history, definition, null, null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(history.SourcePhysicalPath))
            {
                throw new InvalidOperationException(
                    $"Configuration history row '{history.HistoryId}' does not contain a physical source path.");
            }

            var physicalPath = Path.GetFullPath(history.SourcePhysicalPath);
            if (!sourcesByPhysicalPath.TryGetValue(physicalPath, out var source))
            {
                throw new InvalidOperationException(
                    $"Configuration source '{history.SourcePhysicalPath}' is no longer registered and cannot be rolled back.");
            }

            var sourceConfigurationPath = pathProjector.Project(definition.SectionPath, history.LogicalPath);
            if (string.IsNullOrWhiteSpace(history.SourceConfigurationPath)
                || !string.Equals(
                    sourceConfigurationPath,
                    history.SourceConfigurationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration history '{history.HistoryId}' targets external path '{history.SourceConfigurationPath ?? "<missing>"}', "
                    + $"but the current schema projects '{sourceConfigurationPath}'. The rollback cannot be applied safely.");
            }

            preparedRows.Add(new PreparedHistoryRollbackRow(
                history,
                definition,
                source,
                sourceConfigurationPath));
        }

        return preparedRows;
    }

    private IReadOnlyDictionary<string, ConfigurationSourceDescriptor> BuildJsonSourcesByPhysicalPath()
    {
        var sources = new Dictionary<string, ConfigurationSourceDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceInspector.GetSources())
        {
            if (source.Kind == ConfigurationSourceKind.JsonFile
                && !string.IsNullOrWhiteSpace(source.PhysicalPath))
            {
                sources.TryAdd(Path.GetFullPath(source.PhysicalPath), source);
            }
        }

        return sources;
    }

    private async Task ReadEffectiveTargetStatesAsync(
        IReadOnlyList<PreparedHistoryRollbackRow> preparedRows,
        IDictionary<string, HistoryRollbackTargetState> statesByHistoryId,
        CancellationToken cancellationToken)
    {
        foreach (var definitionGroup in preparedRows
                     .Where(static row => row.History.TargetKind != ConfigurationMutationTargetKind.ExternalConfigurationSource)
                     .GroupBy(static row => row.History.DefinitionKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = definitionGroup.ToArray();
            var document = await effectiveValueStore.GetAsync(definitionGroup.Key, cancellationToken);
            var effectiveVersion = document?.Version ?? 0;
            var target = new ConfigurationEffectiveStoreMutationTarget
            {
                ExpectedVersion = effectiveVersion
            };
            var currentValues = document is null
                ? null
                : documentEditor.ReadValues(
                    rows[0].Definition,
                    document.Json,
                    rows.Select(static row => row.History.LogicalPath).ToArray());
            for (var index = 0; index < rows.Length; index++)
            {
                var prepared = rows[index];
                statesByHistoryId[prepared.History.HistoryId] = new HistoryRollbackTargetState(
                    currentValues?[index],
                    target,
                    effectiveVersion,
                    SourceKey: null,
                    SourceConfigurationPath: null,
                    SourceRevision: null);
            }
        }
    }

    private async Task ReadExternalTargetStatesAsync(
        IReadOnlyList<PreparedHistoryRollbackRow> preparedRows,
        IDictionary<string, HistoryRollbackTargetState> statesByHistoryId,
        CancellationToken cancellationToken)
    {
        foreach (var sourceGroup in preparedRows
                     .Where(static row => row.History.TargetKind == ConfigurationMutationTargetKind.ExternalConfigurationSource)
                     .GroupBy(static row => row.Source!.SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = sourceGroup.ToArray();
            var source = rows[0].Source!;
            var configurationPaths = rows
                .Select(static row => row.SourceConfigurationPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = await sourceWriter.ReadPhysicalValuesAsync(
                source,
                configurationPaths,
                cancellationToken);
            var target = new ConfigurationExternalSourceMutationTarget
            {
                SourceKey = source.SourceKey,
                ExpectedRevision = snapshot.Revision
            };
            foreach (var prepared in rows)
            {
                snapshot.Values.TryGetValue(prepared.SourceConfigurationPath!, out var currentValue);
                statesByHistoryId[prepared.History.HistoryId] = new HistoryRollbackTargetState(
                    currentValue,
                    target,
                    EffectiveVersion: null,
                    source.SourceKey,
                    prepared.SourceConfigurationPath,
                    snapshot.Revision);
            }
        }
    }

    private static void EnsureRollbackPreviewCanRepresent(IReadOnlyList<ConfigurationValueHistory> rows)
    {
        for (var leftIndex = 0; leftIndex < rows.Count; leftIndex++)
        {
            var left = rows[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < rows.Count; rightIndex++)
            {
                var right = rows[rightIndex];
                if (HasStrictContainmentInSameTarget(left, right))
                {
                    throw new InvalidOperationException(
                        $"Configuration histories '{left.HistoryId}' and '{right.HistoryId}' target ancestor/descendant paths in the same physical target. "
                        + "This selection cannot be represented by an exact rollback preview; select one owning path or roll back the rows separately.");
                }

                if (HasConflictingExternalPathOwnership(left, right))
                {
                    throw new InvalidOperationException(
                        $"Configuration histories '{left.HistoryId}' and '{right.HistoryId}' map different configuration definitions or logical paths "
                        + "to the same external-source path. This duplicated ownership cannot be rolled back safely as one selection.");
                }

                if (!HasAmbiguousExternalOrder(left, right))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Configuration histories '{left.HistoryId}' and '{right.HistoryId}' contain overlapping external-source writes "
                    + "without a persisted operation order. This legacy selection cannot be rolled back safely; restore the source manually or select a non-overlapping history row.");
            }
        }
    }

    private static bool HasConflictingExternalPathOwnership(
        ConfigurationValueHistory left,
        ConfigurationValueHistory right)
    {
        if (!HasSameExternalConfigurationPath(left, right))
        {
            return false;
        }

        return !string.Equals(left.DefinitionKey, right.DefinitionKey, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(
                   left.LogicalPath.ToCanonicalString(),
                   right.LogicalPath.ToCanonicalString(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStrictContainmentInSameTarget(
        ConfigurationValueHistory left,
        ConfigurationValueHistory right)
    {
        if (left.TargetKind != right.TargetKind)
        {
            return false;
        }

        if (left.TargetKind == ConfigurationMutationTargetKind.MonicaEffectiveStore)
        {
            return string.Equals(left.DefinitionKey, right.DefinitionKey, StringComparison.OrdinalIgnoreCase)
                   && ConfigurationPathOverlapDetector.HasStrictContainment(
                       left.LogicalPath,
                       right.LogicalPath);
        }

        return HasSameExternalPhysicalTarget(left, right)
               && !string.IsNullOrWhiteSpace(left.SourceConfigurationPath)
               && !string.IsNullOrWhiteSpace(right.SourceConfigurationPath)
               && ConfigurationPathOverlapDetector.HasStrictContainment(
                   left.SourceConfigurationPath,
                   right.SourceConfigurationPath);
    }

    private static bool HasAmbiguousExternalOrder(
        ConfigurationValueHistory left,
        ConfigurationValueHistory right)
    {
        if (left.TargetKind != ConfigurationMutationTargetKind.ExternalConfigurationSource
            || right.TargetKind != ConfigurationMutationTargetKind.ExternalConfigurationSource
            || !HasSameExternalConfigurationPath(left, right))
        {
            return false;
        }

        var sameOrderingCoordinates = left.ModifiedTime == right.ModifiedTime
                                      && left.Version == right.Version;
        var sameAtomicWrite = !string.IsNullOrWhiteSpace(left.SourceRevisionBefore)
                              && string.Equals(
                                  left.SourceRevisionBefore,
                                  right.SourceRevisionBefore,
                                  StringComparison.Ordinal)
                              && !string.IsNullOrWhiteSpace(left.SourceRevisionAfter)
                              && string.Equals(
                                  left.SourceRevisionAfter,
                                  right.SourceRevisionAfter,
                                  StringComparison.Ordinal);
        return sameOrderingCoordinates || sameAtomicWrite;
    }

    private static bool HasSameExternalConfigurationPath(
        ConfigurationValueHistory left,
        ConfigurationValueHistory right)
    {
        return HasSameExternalPhysicalTarget(left, right)
               && !string.IsNullOrWhiteSpace(left.SourceConfigurationPath)
               && !string.IsNullOrWhiteSpace(right.SourceConfigurationPath)
               && string.Equals(
                   left.SourceConfigurationPath,
                   right.SourceConfigurationPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameExternalPhysicalTarget(
        ConfigurationValueHistory left,
        ConfigurationValueHistory right)
    {
        return !string.IsNullOrWhiteSpace(left.SourcePhysicalPath)
               && !string.IsNullOrWhiteSpace(right.SourcePhysicalPath)
               && string.Equals(
                   left.SourcePhysicalPath,
                   right.SourcePhysicalPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureExactHistorySchema(
        ConfigurationValueHistory history,
        ConfigurationDefinition definition)
    {
        if (history.SchemaVersion != definition.SchemaVersion
            || string.IsNullOrWhiteSpace(history.SchemaHash)
            || !string.Equals(history.SchemaHash, definition.SchemaHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration history '{history.HistoryId}' was captured with a different or unverified schema and cannot be rolled back safely.");
        }
    }

    private static string ComputePlanToken(IReadOnlyList<HistoryRollbackPlanTokenEntry> entries)
    {
        var ordered = entries
            .OrderBy(static entry => entry.HistoryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var json = JsonSerializer.Serialize(ordered, ConfigurationPersistedJsonOptions.CompactValue);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task<ConfigurationMutationGroupApplyResult> TryMarkOriginalGroupRolledBackAsync(
        string originalGroupId,
        ConfigurationMutationGroupApplyResult applyResult,
        CancellationToken cancellationToken)
    {
        try
        {
            await groupService.MarkRolledBackAsync(
                originalGroupId,
                applyResult.MutationGroup!.GroupId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return applyResult;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Configuration group {OriginalGroupId} was rolled back by {RollbackGroupId}, but the original group audit marker could not be updated.",
                originalGroupId,
                applyResult.MutationGroup!.GroupId);
            return applyResult with
            {
                PostCommitIssues =
                [
                    .. applyResult.PostCommitIssues,
                    new ConfigurationPostCommitIssue
                    {
                        Kind = ConfigurationPostCommitIssueKind.AuditFinalization,
                        Source = groupService.GetType().Name,
                        Message = "The rollback was applied, but the original mutation-group audit marker could not be updated.",
                        Detail = "Review server logs for the original mutation-group audit failure."
                    }
                ]
            };
        }
    }

    private async Task<ConfigurationValueHistory> GetRequiredHistoryAsync(
        string historyId,
        CancellationToken cancellationToken)
    {
        return await historyService.GetHistoryByIdAsync(historyId, cancellationToken)
               ?? throw new KeyNotFoundException($"Configuration history row '{historyId}' was not found.");
    }

    private async Task<ConfigurationValueHistory[]> GetRequiredHistoriesAsync(
        IReadOnlyList<string> historyIds,
        CancellationToken cancellationToken)
    {
        if (historyIds.Count == 0)
        {
            throw new InvalidOperationException("At least one configuration history row is required.");
        }

        var distinctIds = historyIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var histories = await historyService.GetHistoriesByIdsAsync(distinctIds, cancellationToken);
        var historyById = histories
            .GroupBy(static history => history.HistoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<ConfigurationValueHistory>(distinctIds.Length);
        foreach (var historyId in distinctIds)
        {
            if (!historyById.TryGetValue(historyId, out var history))
            {
                throw new KeyNotFoundException($"Configuration history row '{historyId}' was not found.");
            }

            rows.Add(history);
        }

        return rows
            .OrderByDescending(static row => row.ModifiedTime)
            .ThenByDescending(static row => row.Version)
            .ThenBy(static row => row.HistoryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ConfigurationMutationResult> GetRequiredAppliedResults(
        ConfigurationMutationGroupApplyResult applyResult)
    {
        var results = applyResult.Outcomes
            .Where(static outcome => outcome is
                { Status: ConfigurationMutationOutcomeStatus.Applied, Result: not null })
            .Select(outcome => outcome.Result! with { PostCommitIssues = applyResult.PostCommitIssues })
            .ToArray();
        if (applyResult.Status != ConfigurationMutationGroupApplyStatus.Applied
            || results.Length != applyResult.Outcomes.Count)
        {
            throw new InvalidOperationException(DescribeIncompleteRollback(applyResult));
        }

        return results;
    }

    private static string DescribeIncompleteRollback(ConfigurationMutationGroupApplyResult applyResult)
    {
        var diagnostics = applyResult.Outcomes
            .Where(static outcome => outcome.Status != ConfigurationMutationOutcomeStatus.Applied)
            .Select(static outcome => $"{outcome.RequestId}: {outcome.ErrorMessage ?? outcome.Status.ToString()}");
        var appliedCount = applyResult.Outcomes.Count(static outcome =>
            outcome.Status == ConfigurationMutationOutcomeStatus.Applied);
        return $"Rollback completed with status '{applyResult.Status}': {appliedCount} of {applyResult.Outcomes.Count} mutations were applied. "
               + string.Join("; ", diagnostics);
    }

    private sealed record HistoryRollbackPlan(
        ConfigurationHistoryRollbackPreview Preview,
        IReadOnlyDictionary<string, ConfigurationMutationTarget> TargetsByHistoryId);

    private sealed record PreparedHistoryRollbackRow(
        ConfigurationValueHistory History,
        ConfigurationDefinition Definition,
        ConfigurationSourceDescriptor? Source,
        string? SourceConfigurationPath);

    private sealed record HistoryRollbackTargetState(
        ConfigurationStoredValue? CurrentValue,
        ConfigurationMutationTarget Target,
        long? EffectiveVersion,
        string? SourceKey,
        string? SourceConfigurationPath,
        string? SourceRevision);

    private sealed record RollbackCommandKey(
        ConfigurationMutationTargetKind TargetKind,
        string? PhysicalTarget,
        string? ConfigurationPath,
        string DefinitionKey,
        string LogicalPath,
        string? UniqueHistoryId);

    private sealed class RollbackCommandKeyComparer : IEqualityComparer<RollbackCommandKey>
    {
        public static RollbackCommandKeyComparer Instance { get; } = new();

        public bool Equals(RollbackCommandKey? left, RollbackCommandKey? right)
        {
            return ReferenceEquals(left, right)
                   || left is not null
                   && right is not null
                   && left.TargetKind == right.TargetKind
                   && StringComparer.OrdinalIgnoreCase.Equals(left.PhysicalTarget, right.PhysicalTarget)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.ConfigurationPath, right.ConfigurationPath)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.DefinitionKey, right.DefinitionKey)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.LogicalPath, right.LogicalPath)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.UniqueHistoryId, right.UniqueHistoryId);
        }

        public int GetHashCode(RollbackCommandKey key)
        {
            var hash = new HashCode();
            hash.Add(key.TargetKind);
            hash.Add(key.PhysicalTarget, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.ConfigurationPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.DefinitionKey, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.LogicalPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.UniqueHistoryId, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private sealed record HistoryRollbackPlanTokenEntry
    {
        public required string HistoryId { get; init; }

        public required string DefinitionKey { get; init; }

        public required string LogicalPath { get; init; }

        public int SchemaVersion { get; init; }

        public required string SchemaHash { get; init; }

        public ConfigurationMutationTargetKind TargetKind { get; init; }

        public bool CurrentValueExists { get; init; }

        public string? CurrentJson { get; init; }

        public bool RollbackValueExists { get; init; }

        public string? RollbackJson { get; init; }

        public long? EffectiveVersion { get; init; }

        public string? SourceKey { get; init; }

        public string? SourceConfigurationPath { get; init; }

        public string? SourceRevision { get; init; }
    }
}
