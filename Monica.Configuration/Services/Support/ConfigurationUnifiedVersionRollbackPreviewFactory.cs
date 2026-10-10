using System.Security.Cryptography;
using System.Text;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Builds the complete, source-bound preview used by both unified-version rollback review and apply.
/// </summary>
internal sealed class ConfigurationUnifiedVersionRollbackPreviewFactory(
    ConfigurationDefinitionResolver definitionResolver,
    ConfigurationEffectiveSnapshotReader effectiveSnapshotReader,
    ConfigurationValidationCoordinator validationCoordinator,
    ConfigurationRollbackPersistencePlanner persistencePlanner,
    IConfigurationMutationGroupApplyService mutationGroupApplyService)
{
    public async Task<ConfigurationUnifiedVersionApplyPreview> CreateAsync(
        ConfigurationUnifiedVersionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Definitions.Count == 0)
        {
            return CreatePreview(snapshot.Summary.Version, []);
        }

        var targetDefinitions = await ResolveTargetDefinitionsAsync(snapshot.Definitions, cancellationToken);
        var effectiveSnapshots = await effectiveSnapshotReader.ReadManyAsync(
            targetDefinitions.OfType<ConfigurationDefinition>().ToArray(),
            cancellationToken);
        var targets = new List<ConfigurationUnifiedVersionApplyTarget>(snapshot.Definitions.Count);
        var effectiveSnapshotIndex = 0;
        for (var index = 0; index < snapshot.Definitions.Count; index++)
        {
            var document = snapshot.Definitions[index];
            var definition = targetDefinitions[index];
            if (definition is null)
            {
                targets.Add(CreateMissingTarget(document));
                continue;
            }

            // Missing historical definitions are excluded from the batch, so only resolved targets advance this index.
            targets.Add(await ResolveTargetAsync(
                document,
                definition,
                effectiveSnapshots[effectiveSnapshotIndex++],
                cancellationToken));
        }

        var preview = CreatePreview(snapshot.Summary.Version, targets);
        if (!preview.CanApply) return preview;
        var validation = await mutationGroupApplyService.PreviewAsync(new ConfigurationMutationGroupApplyRequest
        {
            Label = "Unified rollback validation preview",
            Commands = Services.ConfigurationUnifiedVersionService.BuildCommands(snapshot.Summary.Version, preview)
        }, cancellationToken);
        var token = Encoding.UTF8.GetBytes($"{preview.PreviewFingerprint}|{validation.ValidationFingerprint}");
        return preview with
        {
            PreviewFingerprint = $"sha256:{Convert.ToHexString(SHA256.HashData(token)).ToLowerInvariant()}",
            ValidationFingerprint = validation.ValidationFingerprint,
            ValidationReports = [.. preview.ValidationReports.Where(report =>
                !validation.Definitions.Any(candidate => candidate.DefinitionKey == report.DefinitionKey)), .. validation.Definitions],
            Problems = validation.Problems
        };
    }

    private async Task<IReadOnlyList<ConfigurationDefinition?>> ResolveTargetDefinitionsAsync(
        IReadOnlyList<ConfigurationUnifiedVersionDefinitionSnapshot> documents,
        CancellationToken cancellationToken)
    {
        var definitions = new ConfigurationDefinition?[documents.Count];
        for (var index = 0; index < documents.Count; index++)
        {
            try
            {
                definitions[index] = await definitionResolver.GetRequiredAsync(
                    documents[index].DefinitionKey,
                    cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                definitions[index] = null;
            }
        }

        return definitions;
    }

    private static ConfigurationUnifiedVersionApplyPreview CreatePreview(
        long version,
        IReadOnlyList<ConfigurationUnifiedVersionApplyTarget> targets)
    {
        return new ConfigurationUnifiedVersionApplyPreview
        {
            Version = version,
            PreviewFingerprint = ConfigurationUnifiedVersionRollbackPreviewFingerprint.Compute(
                version,
                targets),
            Targets = targets,
            ValidationReports = targets.Select(static target => target.ValidationReport).OfType<ConfigurationCandidateValidationReport>().ToArray()
        };
    }

    private static ConfigurationUnifiedVersionApplyTarget CreateMissingTarget(
        ConfigurationUnifiedVersionDefinitionSnapshot document)
    {
        return new ConfigurationUnifiedVersionApplyTarget
        {
            DefinitionKey = document.DefinitionKey,
            DisplayName = document.DisplayName,
            TargetJson = document.Json,
            CapturedSchemaHash = document.SchemaHash,
            Status = ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition
        };
    }

    private async Task<ConfigurationUnifiedVersionApplyTarget> ResolveTargetAsync(
        ConfigurationUnifiedVersionDefinitionSnapshot document,
        ConfigurationDefinition definition,
        ConfigurationResolvedEffectiveSnapshot currentSnapshot,
        CancellationToken cancellationToken)
    {
        var currentJson = currentSnapshot.Json;
        var target = new ConfigurationUnifiedVersionApplyTarget
        {
            DefinitionKey = document.DefinitionKey,
            DisplayName = document.DisplayName,
            CurrentJson = currentJson,
            TargetJson = document.Json,
            CapturedSchemaHash = document.SchemaHash,
            CurrentSchemaHash = definition.SchemaHash,
            CurrentSchemaVersion = definition.SchemaVersion
        };
        var valuesEqual = ConfigurationJsonSemanticComparer.Equals(currentJson, document.Json);
        var schemaDrift = !string.Equals(definition.SchemaHash, document.SchemaHash, StringComparison.Ordinal);
        ConfigurationCompleteValidationResult validation;
        try
        {
            validation = validationCoordinator.ValidateCompleteValue(definition, document.Json, ConfigurationValidationProfile.CapturedValue);
        }
        catch (ConfigurationValidationExecutionException exception)
        {
            return target with
            {
                Status = ConfigurationUnifiedVersionApplyTargetStatus.ValidationRejected,
                ValidationReport = new ConfigurationCandidateValidationReport
                {
                    DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
                    ScopePath = LogicalPath.Root, Scope = ConfigurationValidationScope.CompleteAggregate,
                    Coverage = ConfigurationValidationCoverage.Failed, ValidationRevision = definition.ValidationContract.Revision,
                    Issues = [new ConfigurationCandidateValidationIssue
                    {
                        DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
                        LogicalPath = exception.LogicalPath, LogicalPaths = [exception.LogicalPath], Kind = exception.Kind,
                        NodeDisplayName = definition.DisplayName, Problem = exception.Message
                    }]
                }
            };
        }
        target = target with
        {
            ValidationReport = new ConfigurationCandidateValidationReport
            {
                DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
                ScopePath = LogicalPath.Root, Scope = validation.Scope, Coverage = validation.Coverage,
                ValidationRevision = validation.ValidationRevision,
                Issues = validation.Issues.Select(issue => Services.ConfigurationCandidateValidationService.ToCandidateIssue(definition, issue)).ToArray()
            }
        };
        if (!validation.IsValid)
        {
            return target with
            {
                Status = ConfigurationUnifiedVersionApplyTargetStatus.ValidationRejected,
                ValidationIssues = validation.Issues.Select(issue => CreateValidationIssue(definition, issue)).ToArray()
            };
        }

        var persistenceDrifts = definition.Origin == ConfigurationDefinitionOrigin.LocalScan
            ? await persistencePlanner.FindRuntimeSourceDriftsAsync(
                definition,
                currentSnapshot,
                cancellationToken)
            : [];
        if (persistenceDrifts.Count > 0)
        {
            return target with
            {
                Mutations = persistenceDrifts,
                Status = ConfigurationUnifiedVersionApplyTargetStatus.RuntimeOutOfSync
            };
        }

        if (valuesEqual)
        {
            return target with { Status = ConfigurationUnifiedVersionApplyTargetStatus.Unchanged };
        }

        var mutations = definition.Origin == ConfigurationDefinitionOrigin.PublishedMetadata
            ? persistencePlanner.PlanEffectiveStore(
                definition,
                currentJson,
                document.Json,
                currentSnapshot.RequireVersion(definition))
            : await persistencePlanner.PlanAsync(
                definition,
                currentSnapshot,
                document.Json,
                cancellationToken);
        if (mutations.Count == 0)
        {
            // The semantic difference cannot be written back (for example properties the current schema
            // no longer declares), so the effective configuration does not change.
            return target with { Status = ConfigurationUnifiedVersionApplyTargetStatus.Unchanged };
        }
        var blockedMutation = mutations.FirstOrDefault(static mutation =>
            mutation.Status != ConfigurationUnifiedVersionApplyMutationStatus.Ready);
        if (blockedMutation is not null)
        {
            return target with
            {
                Mutations = mutations,
                Status = blockedMutation.Status switch
                {
                    ConfigurationUnifiedVersionApplyMutationStatus.ReadOnlyOverride =>
                        ConfigurationUnifiedVersionApplyTargetStatus.ReadOnlyOverride,
                    ConfigurationUnifiedVersionApplyMutationStatus.CompositeSourceConflict =>
                        ConfigurationUnifiedVersionApplyTargetStatus.CompositeSourceConflict,
                    ConfigurationUnifiedVersionApplyMutationStatus.LowerPriorityFallback =>
                        ConfigurationUnifiedVersionApplyTargetStatus.LowerPriorityFallback,
                    ConfigurationUnifiedVersionApplyMutationStatus.RuntimeOutOfSync =>
                        ConfigurationUnifiedVersionApplyTargetStatus.RuntimeOutOfSync,
                    _ => ConfigurationUnifiedVersionApplyTargetStatus.UnsupportedSource
                }
            };
        }

        return target with
        {
            Mutations = mutations,
            Status = schemaDrift
                ? ConfigurationUnifiedVersionApplyTargetStatus.CompatibleSchemaDrift
                : ConfigurationUnifiedVersionApplyTargetStatus.Ready
        };
    }

    private static ConfigurationUnifiedVersionValidationIssue CreateValidationIssue(
        ConfigurationDefinition definition,
        ConfigurationValueValidationIssue issue)
    {
        // Schema drift alone must not hide the incompatibility reason: values captured by an older schema
        // drift almost by definition, and hiding every reason made skipped definitions undiagnosable
        // (a missing required property surfaced only as an opaque "hard incompatible"). Withhold details
        // only when the current schema proves the path sensitive — or cannot resolve the path at all,
        // in which case its sensitivity is unknown.
        var isSensitive = ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, issue.LogicalPath);
        return new ConfigurationUnifiedVersionValidationIssue
        {
            LogicalPath = isSensitive ? string.Empty : issue.LogicalPath.ToCanonicalString(),
            Message = isSensitive
                ? "Validation details are hidden because the affected historical path may contain sensitive metadata."
                : issue.Message,
            ValidationRules = isSensitive ? [] : issue.ValidationRules,
            IsSensitive = isSensitive,
            DetailsHidden = isSensitive
        };
    }

}
