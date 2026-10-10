using Microsoft.Extensions.Logging;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;
using Monica.Core.Extensions;

namespace Monica.Configuration.Services;

internal sealed partial class ConfigurationMutationGroupApplyService
{
    private async Task ApplyExternalMutationsAsync(
        IReadOnlyList<PreparedConfigurationMutation> mutations,
        ConfigurationMutationContext context,
        IDictionary<string, ConfigurationMutationOutcome> outcomes,
        ICollection<ConfigurationPostCommitIssue> postCommitIssues,
        CompleteMutationValidationPlan validationPlan,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var sourceGroup in mutations.GroupBy(
                     static mutation => ((ConfigurationExternalSourceMutationTarget)mutation.Command.Target).SourceKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (failed)
            {
                foreach (var mutation in sourceGroup)
                {
                    outcomes[mutation.Command.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = mutation.Command.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Skipped,
                        ErrorMessage = "Skipped because an earlier external configuration source mutation failed."
                    };
                }

                continue;
            }

            var sourceMutations = sourceGroup.ToArray();
            var target = (ConfigurationExternalSourceMutationTarget)sourceMutations[0].Command.Target;
            var source = sourceInspector.GetRequiredSource(target.SourceKey);
            try
            {
                var write = await runtimeSnapshotLock.ExecuteAsync(async token =>
                {
                    ValidateReviewedSourceChains(sourceMutations);
                    await EnsureAdoptionBaselineAsync(validationPlan, token);
                    return await sourceWriter.WriteBatchAsync(
                        source,
                        sourceMutations.Select(static mutation => new ConfigurationJsonFileMutation
                        {
                            ConfigurationPath = mutation.ConfigurationPath,
                            MutationKind = mutation.Request.MutationKind,
                            Value = mutation.Request.Value
                        }).ToArray(),
                        target.ExpectedRevision,
                        token);
                }, cancellationToken);
                validationPlan.PhysicalRevisions[target.SourceKey] = write.NewRevision;

                for (var index = 0; index < sourceMutations.Length; index++)
                {
                    var mutation = sourceMutations[index];
                    var valueResult = write.Results[index];
                    var reloadBehavior = mutation.TargetNode.ResolveEffectiveReloadBehavior(mutation.Definition);
                    var result = new ConfigurationMutationResult
                    {
                        DefinitionKey = mutation.Definition.DefinitionKey,
                        LogicalPath = mutation.Command.LogicalPath,
                        NewVersion = 0,
                        SchemaVersion = mutation.Definition.SchemaVersion,
                        ModifiedTime = write.ModifiedTime,
                        RequiresRestart = reloadBehavior.RequiresProcessRestart()
                    };
                    outcomes[mutation.Command.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = mutation.Command.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Applied,
                        Result = result
                    };
                    metricsRecorder.RecordMutation(source.SourceKey);

                    try
                    {
                        await historyStore.AppendHistoryAsync(new ConfigurationValueHistory
                        {
                            HistoryId = Guid.NewGuid().ToString("N"),
                            DefinitionKey = mutation.Definition.DefinitionKey,
                            LogicalPath = mutation.Command.LogicalPath,
                            ConfigurationPath = mutation.ConfigurationPath,
                            TargetKind = ConfigurationMutationTargetKind.ExternalConfigurationSource,
                            SourceProviderType = source.ProviderType,
                            SourceDisplayName = source.DisplayName,
                            SourcePhysicalPath = source.PhysicalPath,
                            SourceConfigurationPath = mutation.ConfigurationPath,
                            MutationKind = mutation.Command.MutationKind,
                            Granularity = mutation.Granularity,
                            State = mutation.Command.MutationKind == ConfigurationMutationKind.Remove
                                ? ConfigurationValueState.Removed
                                : ConfigurationValueState.Active,
                            OldValue = valueResult.OldValue,
                            NewValue = valueResult.NewValue,
                            Version = 0,
                            SourceRevisionBefore = write.OldRevision,
                            SourceRevisionAfter = write.NewRevision,
                            SchemaVersion = mutation.Definition.SchemaVersion,
                            SchemaHash = mutation.Definition.SchemaHash,
                            ModifiedTime = write.ModifiedTime,
                            ModifierId = context.ModifierId,
                            ModifierName = context.ModifierName,
                            Reason = context.Reason,
                            MutationGroupId = context.MutationGroupId
                        }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "External configuration history persistence failed for request {RequestId} in group {GroupId}.",
                            mutation.Command.RequestId,
                            context.MutationGroupId);
                        postCommitIssues.Add(new ConfigurationPostCommitIssue
                        {
                            Kind = ConfigurationPostCommitIssueKind.AuditFinalization,
                            Source = historyStore.GetType().Name,
                            Message = "An external configuration source was saved, but its history row could not be persisted.",
                            Detail = "See host diagnostics for the audit persistence failure."
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "External configuration source {SourceKey} failed during group {GroupId}; later sources will be skipped.",
                    target.SourceKey,
                    context.MutationGroupId);
                failed = true;
                foreach (var mutation in sourceMutations)
                {
                    outcomes[mutation.Command.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = mutation.Command.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Failed,
                        ErrorMessage = "The external configuration source could not be saved. Review the current source before retrying."
                    };
                }
            }
        }
    }

    private async Task<MutationGroupFinalizationResult> FinalizeMixedOrExternalGroupAsync(
        ConfigurationMutationGroup currentGroup,
        IReadOnlyList<ConfigurationMutationOutcome> appliedOutcomes,
        bool allApplied,
        ICollection<ConfigurationPostCommitIssue> postCommitIssues,
        CancellationToken cancellationToken)
    {
        var definitionKeys = appliedOutcomes
            .Select(static outcome => outcome.Result!.DefinitionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            if (allApplied)
            {
                await mutationGroupService.CompleteAsync(
                    currentGroup.GroupId,
                    appliedOutcomes.Count,
                    definitionKeys,
                    cancellationToken);
            }
            else
            {
                await mutationGroupService.MarkPartialAsync(
                    currentGroup.GroupId,
                    appliedOutcomes.Count,
                    definitionKeys,
                    cancellationToken);
            }

            return new MutationGroupFinalizationResult(
                await mutationGroupService.GetAsync(currentGroup.GroupId, cancellationToken) ?? currentGroup,
                Succeeded: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Configuration mutation-group finalization failed for {GroupId} after {AppliedCount} values were applied.",
                currentGroup.GroupId,
                appliedOutcomes.Count);
            postCommitIssues.Add(new ConfigurationPostCommitIssue
            {
                Kind = allApplied
                    ? ConfigurationPostCommitIssueKind.UnifiedVersionCapture
                    : ConfigurationPostCommitIssueKind.AuditFinalization,
                Source = nameof(ConfigurationMutationGroupService),
                Message = "Configuration values were applied, but mutation-group finalization failed.",
                Detail = "See host diagnostics for the group finalization failure."
            });
            return new MutationGroupFinalizationResult(
                currentGroup with
                {
                    MutationCount = appliedOutcomes.Count,
                    DefinitionKeys = definitionKeys,
                    Status = allApplied
                        ? ConfigurationMutationGroupStatus.Applied
                        : ConfigurationMutationGroupStatus.PartiallyApplied
                },
                Succeeded: false);
        }
    }

    private sealed record MutationGroupFinalizationResult(
        ConfigurationMutationGroup MutationGroup,
        bool Succeeded);

    private void ValidateTargets(IReadOnlyList<PreparedConfigurationMutation> mutations)
    {
        var expectedRevisions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pathsByExternalTarget = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var monicaPathsByDefinition = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sourceKeysByPhysicalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            if (mutation.Command.Target is ConfigurationEffectiveStoreMutationTarget)
            {
                if (!monicaPathsByDefinition.TryGetValue(mutation.Definition.DefinitionKey, out var definitionPaths))
                {
                    definitionPaths = [];
                    monicaPathsByDefinition[mutation.Definition.DefinitionKey] = definitionPaths;
                }

                var containingPath = definitionPaths.FirstOrDefault(path =>
                    ConfigurationPathOverlapDetector.HasStrictContainment(path, mutation.ConfigurationPath));
                if (containingPath is not null)
                {
                    throw new ConfigurationValidationFailedException(
                        $"Mutation group contains ancestor/descendant paths '{containingPath}' and '{mutation.ConfigurationPath}' "
                        + $"for Monica definition '{mutation.Definition.DefinitionKey}'. Submit one mutation for the owning path so review and rollback semantics remain unambiguous.");
                }

                definitionPaths.Add(mutation.ConfigurationPath);
                continue;
            }

            if (mutation.Command.Target is not ConfigurationExternalSourceMutationTarget target)
            {
                throw new ConfigurationValidationFailedException(
                    $"Unsupported mutation target '{mutation.Command.Target.GetType().Name}'.");
            }

            var source = sourceInspector.GetRequiredSource(target.SourceKey);
            if (!source.IsWritable)
            {
                throw new ConfigurationValidationFailedException(
                    source.ReadOnlyReason ?? $"Configuration source '{source.DisplayName}' is read-only.");
            }

            if (expectedRevisions.TryGetValue(target.SourceKey, out var expectedRevision)
                && !string.Equals(expectedRevision, target.ExpectedRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationValidationFailedException(
                    $"Mutation group contains inconsistent expected revisions for source '{source.DisplayName}'.");
            }

            expectedRevisions[target.SourceKey] = target.ExpectedRevision;

            var externalTargetIdentity = string.IsNullOrWhiteSpace(source.PhysicalPath)
                ? target.SourceKey
                : source.PhysicalPath;
            if (sourceKeysByPhysicalPath.TryGetValue(externalTargetIdentity, out var previousSourceKey)
                && !string.Equals(previousSourceKey, target.SourceKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationValidationFailedException(
                    "One physical source cannot be changed through multiple provider aliases in the same group.");
            }
            sourceKeysByPhysicalPath[externalTargetIdentity] = target.SourceKey;
            if (!pathsByExternalTarget.TryGetValue(externalTargetIdentity, out var sourcePaths))
            {
                sourcePaths = [];
                pathsByExternalTarget[externalTargetIdentity] = sourcePaths;
            }

            var overlappingPath = sourcePaths.FirstOrDefault(path =>
                ConfigurationPathOverlapDetector.Overlaps(path, mutation.ConfigurationPath));
            if (overlappingPath is not null)
            {
                throw new ConfigurationValidationFailedException(
                    $"Mutation group contains overlapping paths '{overlappingPath}' and '{mutation.ConfigurationPath}' "
                    + $"for external source '{source.DisplayName}'. Submit one mutation for the owning path so the saved history has an unambiguous rollback order.");
            }

            sourcePaths.Add(mutation.ConfigurationPath);
        }
    }
}
