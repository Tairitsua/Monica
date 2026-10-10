using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

internal sealed partial class ConfigurationMutationGroupApplyService
{
    private async Task<MonicaCommitPlan> BuildMonicaCommitPlanAsync(
        ConfigurationMutationGroupApplyRequest request,
        ConfigurationMutationContext context,
        DateTimeOffset createdTime,
        IReadOnlyList<PreparedConfigurationMutation> mutations,
        bool hasExternalMutations,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, DefinitionMutationState>(StringComparer.OrdinalIgnoreCase);
        var orderedStates = new List<DefinitionMutationState>();
        var plannedResults = new List<PlannedMutationResult>(mutations.Count);

        foreach (var mutation in mutations)
        {
            var target = (ConfigurationEffectiveStoreMutationTarget)mutation.Command.Target;
            if (!states.TryGetValue(mutation.Definition.DefinitionKey, out var state))
            {
                var document = await effectiveValueStore.GetAsync(
                    mutation.Definition.DefinitionKey,
                    cancellationToken)
                    ?? new ConfigurationEffectiveValueDocument
                    {
                        DefinitionKey = mutation.Definition.DefinitionKey,
                        Json = seedFactory.CreateSeedJson(mutation.Definition),
                        Version = 0,
                        SchemaVersion = mutation.Definition.SchemaVersion,
                        LastModifiedTime = createdTime
                    };
                if (target.ExpectedVersion is not null && target.ExpectedVersion != document.Version)
                {
                    throw new ConfigurationConcurrencyConflictException(
                        $"Expected version {target.ExpectedVersion} for '{mutation.Definition.DefinitionKey}', but current version is {document.Version}.");
                }

                state = new DefinitionMutationState(mutation.Definition, document, target.ExpectedVersion);
                states.Add(mutation.Definition.DefinitionKey, state);
                orderedStates.Add(state);
            }
            else if (state.StagedExpectedVersion != target.ExpectedVersion)
            {
                throw new ConfigurationValidationFailedException(
                    $"Mutation group contains inconsistent expected versions for '{mutation.Definition.DefinitionKey}'.");
            }

            var oldValue = documentEditor.ReadValue(
                mutation.Definition,
                state.Json,
                mutation.Request.LogicalPath);
            var updatedJson = documentEditor.ApplyMutation(mutation.Definition, state.Json, mutation.Request);
            var newValue = mutation.Request.MutationKind == ConfigurationMutationKind.Remove
                ? ConfigurationStoredValue.Null
                : documentEditor.ReadValue(mutation.Definition, updatedJson, mutation.Request.LogicalPath)
                  ?? ConfigurationStoredValue.Null;
            var modifiedTime = DateTimeOffset.UtcNow;
            var newVersion = state.FinalVersion;
            var history = new ConfigurationValueHistory
            {
                HistoryId = Guid.NewGuid().ToString("N"),
                DefinitionKey = mutation.Definition.DefinitionKey,
                LogicalPath = mutation.Request.LogicalPath,
                ConfigurationPath = mutation.ConfigurationPath,
                MutationKind = mutation.Request.MutationKind,
                Granularity = mutation.Granularity,
                State = mutation.Request.MutationKind == ConfigurationMutationKind.Remove
                    ? ConfigurationValueState.Removed
                    : ConfigurationValueState.Active,
                OldValue = oldValue,
                NewValue = newValue,
                Version = newVersion,
                SchemaVersion = mutation.Definition.SchemaVersion,
                SchemaHash = mutation.Definition.SchemaHash,
                ModifiedTime = modifiedTime,
                ModifierId = context.ModifierId,
                ModifierName = context.ModifierName,
                Reason = context.Reason,
                MutationGroupId = context.MutationGroupId
            };
            state.RequestIds.Add(mutation.Command.RequestId);
            state.Histories.Add(history);
            var result = new ConfigurationMutationResult
            {
                DefinitionKey = mutation.Definition.DefinitionKey,
                LogicalPath = mutation.Request.LogicalPath,
                NewVersion = newVersion,
                SchemaVersion = mutation.Definition.SchemaVersion,
                ModifiedTime = modifiedTime,
                RequiresRestart = mutation.TargetNode
                    .ResolveEffectiveReloadBehavior(mutation.Definition)
                    .RequiresProcessRestart()
            };
            plannedResults.Add(new PlannedMutationResult(mutation.Command.RequestId, result));
            state.Advance(updatedJson);
        }

        var definitionKeys = mutations
            .Select(static mutation => mutation.Definition.DefinitionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var group = new ConfigurationMutationGroup
        {
            GroupId = context.MutationGroupId!,
            Label = NormalizeLabel(request.Label, createdTime),
            Reason = context.Reason,
            DefinitionKeys = definitionKeys,
            MutationCount = mutations.Count,
            CreatedTime = createdTime,
            ModifierId = context.ModifierId,
            ModifierName = context.ModifierName,
            Status = hasExternalMutations
                ? ConfigurationMutationGroupStatus.PartiallyApplied
                : ConfigurationMutationGroupStatus.Applied
        };
        return new MonicaCommitPlan(
            new ConfigurationMutationBatchCommitRequest
            {
                MutationGroup = group,
                Items = orderedStates.Select(state => new ConfigurationMutationBatchCommitItem
                {
                    RequestIds = state.RequestIds.ToArray(),
                    Histories = state.Histories.ToArray(),
                    SaveRequest = new ConfigurationEffectiveValueSaveRequest
                    {
                        Definition = state.Definition,
                        Json = state.Json,
                        ExpectedVersion = state.Version,
                        Context = context
                    }
                }).ToArray()
            },
            plannedResults);
    }

    private sealed class DefinitionMutationState(
        ConfigurationDefinition definition,
        ConfigurationEffectiveValueDocument document,
        long? stagedExpectedVersion)
    {
        public ConfigurationDefinition Definition { get; } = definition;

        public string Json { get; private set; } = document.Json;

        public long Version { get; } = document.Version;

        public long FinalVersion { get; } = checked(document.Version + 1);

        public long? StagedExpectedVersion { get; } = stagedExpectedVersion;

        public List<string> RequestIds { get; } = [];

        public List<ConfigurationValueHistory> Histories { get; } = [];

        public void Advance(string json) => Json = json;
    }

    private sealed record PlannedMutationResult(string RequestId, ConfigurationMutationResult Result);

    private sealed record MonicaCommitPlan(
        ConfigurationMutationBatchCommitRequest CommitRequest,
        IReadOnlyList<PlannedMutationResult> PlannedResults);
}
