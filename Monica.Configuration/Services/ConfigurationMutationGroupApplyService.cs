using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;
using Monica.Core.Extensions;
using Monica.Modules;

namespace Monica.Configuration.Services;

/// <summary>
/// Coordinates validated mutation groups across the Monica store and writable external sources.
/// </summary>
internal sealed partial class ConfigurationMutationGroupApplyService(
    ConfigurationMutationPlanner mutationPlanner,
    ConfigurationRuntimeContext runtimeContext,
    ConfigurationValidationCoordinator validationCoordinator,
    ConfigurationDefinitionResolver definitionResolver,
    IConfigurationEffectiveValueStore effectiveValueStore,
    IConfigurationMutationBatchStore batchStore,
    IConfigurationHistoryStore historyStore,
    IConfigurationMutationGroupService mutationGroupService,
    IConfigurationSourceInspector sourceInspector,
    IConfigurationJsonFileSourceWriter sourceWriter,
    ConfigurationEffectiveValueSeedFactory seedFactory,
    ConfigurationEffectiveSnapshotReader effectiveSnapshotReader,
    ConfigurationEffectiveValueDocumentEditor documentEditor,
    IConfigurationUnifiedVersionCoordinator unifiedVersionCoordinator,
    IConfigurationReloadCoordinator reloadCoordinator,
    ConfigurationRuntimeSnapshotLock runtimeSnapshotLock,
    ConfigurationReloadNotificationDispatcher notificationDispatcher,
    IOptions<ModuleConfigurationOption> moduleOptions,
    ConfigurationMetricsRecorder metricsRecorder,
    ILogger<ConfigurationMutationGroupApplyService> logger)
    : IConfigurationMutationGroupApplyService
{
    private static readonly SemaphoreSlim PROCESS_APPLY_LOCK = new(1, 1);

    public async Task<ConfigurationMutationGroupApplyResult> ApplyAsync(
        ConfigurationMutationGroupApplyRequest request,
        CancellationToken cancellationToken)
    {
        // Keep persistence, reload, postcondition verification, and unified capture in one process-local order.
        // Provider reloads are separately excluded while the verified runtime snapshot is captured.
        await PROCESS_APPLY_LOCK.WaitAsync(cancellationToken);
        try
        {
            return await ApplyCoreAsync(request, cancellationToken);
        }
        finally
        {
            PROCESS_APPLY_LOCK.Release();
        }
    }

    private async Task<ConfigurationMutationGroupApplyResult> ApplyCoreAsync(
        ConfigurationMutationGroupApplyRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var createdTime = DateTimeOffset.UtcNow;
        var groupId = Guid.NewGuid().ToString("N");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var context = request.Context with
        {
            MutationGroupId = groupId,
            Reason = reason
        };
        var validationPlan = await BuildValidationPlanAsync(request, context, cancellationToken, throwOnExecutionFault: true);
        var prepared = validationPlan.Mutations;
        var validationPreview = validationPlan.Preview;
        if (!string.IsNullOrEmpty(request.ExpectedValidationFingerprint)
            && !string.Equals(request.ExpectedValidationFingerprint, validationPreview.ValidationFingerprint, StringComparison.Ordinal))
        {
            validationPreview = validationPreview with
            {
                Problems = [.. validationPreview.Problems, new ConfigurationMutationValidationProblem
                {
                    Code = "StaleAggregate",
                    Message = "A contributing value or validation contract changed. Review the complete group again."
                }]
            };
        }
        if (!validationPreview.CanApply)
        {
            return new ConfigurationMutationGroupApplyResult
            {
                Status = ConfigurationMutationGroupApplyStatus.Rejected,
                ValidationPreview = validationPreview,
                Outcomes = request.Commands.Select(command => new ConfigurationMutationOutcome
                {
                    RequestId = command.RequestId,
                    Status = ConfigurationMutationOutcomeStatus.Skipped,
                    ErrorMessage = "Complete configuration validation rejected the group before persistence."
                }).ToArray()
            };
        }
        var monicaMutations = prepared
            .Where(static mutation => mutation.Command.Target is ConfigurationEffectiveStoreMutationTarget)
            .ToArray();
        var externalMutations = prepared
            .Where(static mutation => mutation.Command.Target is ConfigurationExternalSourceMutationTarget)
            .ToArray();
        var outcomeByRequestId = new Dictionary<string, ConfigurationMutationOutcome>(StringComparer.Ordinal);
        var postCommitIssues = new List<ConfigurationPostCommitIssue>();
        IReadOnlyDictionary<string, ConfigurationEffectiveValueDocument> committedDocuments =
            new Dictionary<string, ConfigurationEffectiveValueDocument>(StringComparer.OrdinalIgnoreCase);
        var monicaPersistenceFailed = false;
        ConfigurationMutationGroup mutationGroup;

        if (monicaMutations.Length > 0)
        {
            var plan = await BuildMonicaCommitPlanAsync(
                request,
                context,
                createdTime,
                monicaMutations,
                externalMutations.Length > 0,
                cancellationToken);
            var commit = await runtimeSnapshotLock.ExecuteAsync(async token =>
            {
                ValidateReviewedSourceChains(monicaMutations);
                await EnsureValidationBaselineAsync(request, context, validationPreview.ValidationFingerprint, token);
                return await batchStore.CommitAsync(plan.CommitRequest, token);
            }, cancellationToken);
            mutationGroup = commit.MutationGroup;
            committedDocuments = commit.Documents;
            foreach (var document in commit.Documents.Values)
                validationPlan.DocumentVersions[document.DefinitionKey] = document.Version;
            monicaPersistenceFailed = commit.Failure is not null;
            postCommitIssues.AddRange(commit.PostCommitIssues);
            var appliedRequestIds = commit.AppliedRequestIds.ToHashSet(StringComparer.Ordinal);
            foreach (var planned in plan.PlannedResults)
            {
                if (appliedRequestIds.Contains(planned.RequestId))
                {
                    outcomeByRequestId[planned.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = planned.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Applied,
                        Result = planned.Result
                    };
                    metricsRecorder.RecordMutation(effectiveValueStore.Descriptor.StoreKey);
                }
                else if (commit.Failure is { } failure
                         && plan.CommitRequest.Items.Any(item => item.RequestIds.Contains(failure.RequestId)
                             && item.RequestIds.Contains(planned.RequestId)))
                {
                    outcomeByRequestId[planned.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = planned.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Failed,
                        ErrorMessage = failure.Message
                    };
                }
                else
                {
                    outcomeByRequestId[planned.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = planned.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Skipped,
                        ErrorMessage = "Skipped because an earlier effective-store mutation failed."
                    };
                }
            }
        }
        else
        {
            await runtimeSnapshotLock.ExecuteAsync(token =>
                EnsureValidationBaselineAsync(request, context, validationPreview.ValidationFingerprint, token), cancellationToken);
            mutationGroup = await mutationGroupService.BeginAsync(
                NormalizeLabel(request.Label, createdTime),
                reason,
                context,
                cancellationToken);
        }

        if (externalMutations.Length > 0)
        {
            if (monicaPersistenceFailed)
            {
                foreach (var mutation in externalMutations)
                {
                    outcomeByRequestId[mutation.Command.RequestId] = new ConfigurationMutationOutcome
                    {
                        RequestId = mutation.Command.RequestId,
                        Status = ConfigurationMutationOutcomeStatus.Skipped,
                        ErrorMessage = "Skipped because the effective-store persistence segment failed."
                    };
                }
            }
            else
            {
                await ApplyExternalMutationsAsync(
                    externalMutations,
                    context,
                    outcomeByRequestId,
                    postCommitIssues,
                    validationPlan,
                    cancellationToken);
            }
        }

        var orderedOutcomes = request.Commands
            .Select(command => outcomeByRequestId.GetValueOrDefault(command.RequestId)
                ?? new ConfigurationMutationOutcome
                {
                    RequestId = command.RequestId,
                    Status = ConfigurationMutationOutcomeStatus.Skipped,
                    ErrorMessage = "The mutation was not attempted."
                })
            .ToArray();
        var appliedOutcomes = orderedOutcomes
            .Where(static outcome => outcome.Status == ConfigurationMutationOutcomeStatus.Applied)
            .ToArray();
        var allApplied = appliedOutcomes.Length == orderedOutcomes.Length;
        var groupFinalizationSucceeded = true;

        var runtimeReloadSucceeded = true;
        if (committedDocuments.Count > 0)
        {
            var reloadIssues = await ReloadCommittedDefinitionsAsync(committedDocuments, cancellationToken);
            runtimeReloadSucceeded = reloadIssues.Count == 0;
            postCommitIssues.AddRange(reloadIssues);
        }

        if (externalMutations.Any(mutation =>
                outcomeByRequestId.GetValueOrDefault(mutation.Command.RequestId)?.Status
                == ConfigurationMutationOutcomeStatus.Applied))
        {
            try
            {
                await reloadCoordinator.ReloadRuntimeConfigurationAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                runtimeReloadSucceeded = false;
                logger.LogWarning(
                    ex,
                    "External configuration sources were saved, but runtime configuration reload failed for group {GroupId}.",
                    mutationGroup.GroupId);
                postCommitIssues.Add(new ConfigurationPostCommitIssue
                {
                    Kind = ConfigurationPostCommitIssueKind.LocalReload,
                    Source = nameof(ConfigurationMutationGroupApplyService),
                    Message = "External configuration sources were saved, but the current process could not reload them.",
                    Detail = "See host diagnostics for the runtime reload failure."
                });
            }
        }

        if (externalMutations.Length > 0)
        {
            var finalization = await FinalizeMixedOrExternalGroupAsync(
                mutationGroup,
                appliedOutcomes,
                allApplied,
                postCommitIssues,
                cancellationToken);
            mutationGroup = finalization.MutationGroup;
            groupFinalizationSucceeded = finalization.Succeeded;
        }

        if (allApplied && runtimeReloadSucceeded)
        {
            await runtimeSnapshotLock.ExecuteAsync(async stableRuntimeCancellationToken =>
            {
                var effectiveValuesVerified = true;
                if (request.ExpectedEffectiveValues.Count > 0)
                {
                    var verificationIssues = await VerifyExpectedEffectiveValuesAsync(
                        request.ExpectedEffectiveValues,
                        stableRuntimeCancellationToken);
                    effectiveValuesVerified = verificationIssues.Count == 0;
                    postCommitIssues.AddRange(verificationIssues);
                }

                if (!groupFinalizationSucceeded || !effectiveValuesVerified)
                {
                    return;
                }

                try
                {
                    await unifiedVersionCoordinator.CaptureMutationGroupAsync(
                        mutationGroup,
                        request.ExpectedEffectiveValues,
                        stableRuntimeCancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Unified configuration version capture failed for mutation group {GroupId}.",
                        mutationGroup.GroupId);
                    postCommitIssues.Add(new ConfigurationPostCommitIssue
                    {
                        Kind = ConfigurationPostCommitIssueKind.UnifiedVersionCapture,
                        Source = nameof(IConfigurationUnifiedVersionCoordinator),
                        Message = "Configuration values were applied, but the unified-version snapshot could not be persisted.",
                        Detail = "See host diagnostics for the unified-version capture failure."
                    });
                }
            }, cancellationToken);
        }
        else if (allApplied && groupFinalizationSucceeded)
        {
            postCommitIssues.Add(new ConfigurationPostCommitIssue
            {
                Kind = ConfigurationPostCommitIssueKind.UnifiedVersionCapture,
                Source = nameof(ConfigurationMutationGroupApplyService),
                Message = "Configuration values were applied, but unified-version capture was skipped because the current process could not reload the committed values.",
                Detail = "Capturing before a successful reload could persist stale pre-mutation runtime values."
            });
        }

        var monicaRequestIds = monicaMutations
            .Select(static mutation => mutation.Command.RequestId)
            .ToHashSet(StringComparer.Ordinal);
        var committedMonicaOutcomes = appliedOutcomes
            .Where(outcome => monicaRequestIds.Contains(outcome.RequestId))
            .ToArray();
        if (committedMonicaOutcomes.Length > 0)
        {
            var committedVersions = committedMonicaOutcomes
                .Select(static outcome => outcome.Result)
                .Where(static result => result is not null)
                .GroupBy(static result => result!.DefinitionKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.MaxBy(static result => result!.NewVersion)!)
                .Select(static result => new ConfigurationReloadDefinitionVersion
                {
                    DefinitionKey = result.DefinitionKey,
                    Version = result.NewVersion
                })
                .ToArray();
            var signal = new ConfigurationReloadSignal
            {
                SignalId = Guid.NewGuid().ToString("N"),
                OriginInstanceId = moduleOptions.Value.InstanceId,
                StoreKey = effectiveValueStore.Descriptor.StoreKey,
                Kind = ConfigurationReloadSignalKind.DefinitionsChanged,
                Definitions = committedVersions,
                ChangedTime = committedMonicaOutcomes.Max(static outcome => outcome.Result!.ModifiedTime)
            };
            postCommitIssues.AddRange(await notificationDispatcher.DispatchAsync(
                signal,
                "mutation_group",
                cancellationToken));
        }

        return new ConfigurationMutationGroupApplyResult
        {
            Status = allApplied
                ? ConfigurationMutationGroupApplyStatus.Applied
                : ConfigurationMutationGroupApplyStatus.PartiallyApplied,
            MutationGroup = mutationGroup,
            Outcomes = orderedOutcomes,
            PostCommitIssues = postCommitIssues
        };
    }

    private async Task<IReadOnlyList<ConfigurationPostCommitIssue>> ReloadCommittedDefinitionsAsync(
        IReadOnlyDictionary<string, ConfigurationEffectiveValueDocument> documents,
        CancellationToken cancellationToken)
    {
        var issues = new List<ConfigurationPostCommitIssue>();
        foreach (var document in documents.Values.OrderBy(static document => document.DefinitionKey, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await reloadCoordinator.ReloadMonicaProjectionAsync(
                    document.DefinitionKey,
                    document.Version,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Local Monica projection reload failed for definition {DefinitionKey} at version {Version}.",
                    document.DefinitionKey,
                    document.Version);
                issues.Add(new ConfigurationPostCommitIssue
                {
                    Kind = ConfigurationPostCommitIssueKind.LocalReload,
                    Source = nameof(ConfigurationMutationGroupApplyService),
                    Message = $"Configuration '{document.DefinitionKey}' was saved, but the current process could not reload it.",
                    Detail = "See host diagnostics for the local reload failure."
                });
            }
        }

        return issues;
    }

    private static void ValidateRequest(ConfigurationMutationGroupApplyRequest request)
    {
        if (request.Commands.Count == 0)
        {
            throw new ConfigurationValidationFailedException("At least one configuration mutation command is required.");
        }

        var duplicateRequestId = request.Commands
            .GroupBy(static command => command.RequestId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicateRequestId is not null)
        {
            throw new ConfigurationValidationFailedException(
                $"Mutation request id '{duplicateRequestId}' is duplicated in the group.");
        }

        var duplicateExpectedDefinition = request.ExpectedEffectiveValues
            .GroupBy(static expected => expected.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicateExpectedDefinition is not null)
        {
            throw new ConfigurationValidationFailedException(
                $"Expected effective definition '{duplicateExpectedDefinition}' is duplicated in the group.");
        }

        foreach (var expected in request.ExpectedEffectiveValues)
        {
            if (string.IsNullOrWhiteSpace(expected.DefinitionKey))
            {
                throw new ConfigurationValidationFailedException(
                    "Expected effective definition keys cannot be empty.");
            }

            try
            {
                using var document = JsonDocument.Parse(expected.Json);
            }
            catch (JsonException)
            {
                throw new ConfigurationValidationFailedException(
                    "An expected effective value is not valid JSON.");
            }
        }
    }

    private async Task<IReadOnlyList<ConfigurationPostCommitIssue>> VerifyExpectedEffectiveValuesAsync(
        IReadOnlyList<ConfigurationExpectedEffectiveValue> expectedValues,
        CancellationToken cancellationToken)
    {
        var mismatches = new List<string>();
        foreach (var expected in expectedValues)
        {
            ConfigurationDefinition definition;
            try
            {
                definition = await definitionResolver.GetRequiredAsync(expected.DefinitionKey, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Effective-value postcondition verification failed for definition {DefinitionKey}.",
                    expected.DefinitionKey);
                mismatches.Add(expected.DefinitionKey);
                continue;
            }

            try
            {
                var effectiveSnapshot = await effectiveSnapshotReader.ReadAsync(definition, cancellationToken);
                if (!ConfigurationJsonSemanticComparer.Equals(effectiveSnapshot.Json, expected.Json))
                {
                    mismatches.Add(expected.DefinitionKey);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Effective-value postcondition verification failed for definition {DefinitionKey}.",
                    expected.DefinitionKey);
                mismatches.Add(expected.DefinitionKey);
            }
        }

        if (mismatches.Count == 0)
        {
            return [];
        }

        return
        [
            new ConfigurationPostCommitIssue
            {
                Kind = ConfigurationPostCommitIssueKind.EffectiveValueVerification,
                Source = nameof(ConfigurationMutationGroupApplyService),
                Message = "Configuration values were persisted, but the reloaded effective state did not match the reviewed result. Unified-version capture was skipped.",
                Detail = $"Definitions with mismatched effective values: {string.Join(", ", mismatches.Order(StringComparer.OrdinalIgnoreCase))}."
            }
        ];
    }

    private static string NormalizeLabel(string label, DateTimeOffset createdTime)
    {
        return string.IsNullOrWhiteSpace(label)
            ? $"Changes {createdTime:yyyy-MM-dd HH:mm}"
            : label.Trim();
    }

    private void ValidateReviewedSourceChains(IEnumerable<PreparedConfigurationMutation> mutations)
    {
        foreach (var mutation in mutations.Where(static mutation =>
                     !string.IsNullOrWhiteSpace(mutation.Command.ExpectedSourceChainRevision)))
        {
            var currentRevision = ConfigurationSourceChainRevision.Compute(
                sourceInspector.GetSourceChain(mutation.Definition, mutation.Command.LogicalPath));
            if (!string.Equals(
                    currentRevision,
                    mutation.Command.ExpectedSourceChainRevision,
                    StringComparison.Ordinal))
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"The effective source chain for '{mutation.Definition.DefinitionKey}' at '{mutation.Command.LogicalPath}' changed after review.");
            }
        }
    }

}
