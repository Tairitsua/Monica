using Microsoft.Extensions.Configuration;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Models.Internal;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Tool.Extensions;

namespace Monica.Configuration.Facades;

/// <summary>
/// Host-facing entry point for configuration management APIs and UI consumers.
/// </summary>
public sealed class ConfigurationFacade(
    ConfigurationDefinitionResolver definitionResolver,
    IConfigurationDefinitionRegistry definitionRegistry,
    IConfigurationDefinitionMaintenanceStore definitionMaintenanceStore,
    IConfigurationDefinitionChangeImpactService definitionChangeImpactService,
    IConfigurationMutationGroupApplyService mutationGroupApplyService,
    IConfigurationHistoryService historyService,
    IConfigurationMutationGroupService mutationGroupService,
    IConfigurationRollbackService rollbackService,
    IConfigurationUnifiedVersionService unifiedVersionService,
    IConfigurationEffectiveValueStore effectiveValueStore,
    IConfigurationHistoryStore historyStore,
    IConfigurationMetadataStore metadataStore,
    IEnumerable<IConfigurationChangeNotifier> changeNotifiers,
    IConfigurationStoreStateTracker storeStateTracker,
    ConfigurationEffectiveStateReader effectiveStateReader,
    IConfigurationSourceInspector sourceInspector,
    IConfigurationJsonFileSourceWriter sourceWriter,
    ConfigurationRuntimeContext runtimeContext,
    IConfigurationRuntimeValidationService runtimeValidationService,
    IConfigurationOptionsValidationDiagnostics optionsValidationDiagnostics,
    IConfigurationCandidateValidationService candidateValidationService,
    IConfigurationRuntimeReloadService runtimeReloadService,
    IConfigurationReloadBroadcastService reloadBroadcastService)
{
    private readonly ConfigurationDefinitionLifecycleService _definitionLifecycleService = new(
        definitionRegistry,
        definitionMaintenanceStore,
        definitionResolver);

    /// <summary>
    /// Gets active configuration definition summaries for operational consumers.
    /// </summary>
    /// <returns>Definition summaries.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationDefinitionSummary>>> GetDefinitionsAsync()
    {
        try
        {
            IReadOnlyList<ConfigurationDefinitionSummary> summaries = (await definitionResolver.GetMergedDefinitionsAsync(CancellationToken.None))
                .Select(ToSummary)
                .ToArray();

            return Res.Ok(summaries);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definitions: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets a fault-isolated active and retired definition catalog for management UI diagnostics.
    /// </summary>
    /// <remarks>
    /// This method is intentionally separate from <see cref="GetDefinitionsAsync"/>. Authoritative consumers such
    /// as mutation, export, source redaction, and unified-version capture must continue to fail closed when metadata
    /// is incomplete instead of treating a partial catalog as complete.
    /// </remarks>
    /// <returns>Available definitions, unavailable metadata entries, and any store-wide read diagnostic.</returns>
    public async Task<Res<ConfigurationDefinitionCatalog>> GetDefinitionCatalogAsync()
    {
        try
        {
            var snapshot = await definitionResolver.GetDiagnosticCatalogAsync(CancellationToken.None);
            return Res.Ok(new ConfigurationDefinitionCatalog
            {
                Definitions = snapshot.Entries.Select(ToSummary).ToArray(),
                StoreDiagnostic = snapshot.StoreDiagnostic
            });
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definition diagnostics: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets one configuration definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <returns>The definition detail.</returns>
    public async Task<Res<ConfigurationDefinitionDetail>> GetDefinitionAsync(string definitionKey)
    {
        try
        {
            var detail = new ConfigurationDefinitionDetail
            {
                Definition = await definitionResolver.GetRequiredForReadAsync(definitionKey, CancellationToken.None)
            };
            return detail;
        }
        catch (ConfigurationDefinitionNotFoundException ex)
        {
            return Res.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definition: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets source-aware runtime validation diagnostics for the current process.
    /// </summary>
    /// <returns>The runtime validation report.</returns>
    public Task<Res<ConfigurationValidationReport>> GetRuntimeValidationReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Res.Ok(runtimeValidationService.GetReport()));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<ConfigurationValidationReport>>(
                Res.Fail(SafeMutationFailure(ex)));
        }
    }

    /// <summary>
    /// Validates a complete candidate JSON value against the mutation-time constraints for one definition scope.
    /// </summary>
    /// <param name="definition">The active configuration definition that owns the candidate value.</param>
    /// <param name="scopePath">The logical path whose complete value is represented by <paramref name="json"/>.</param>
    /// <param name="json">The normalized candidate JSON value.</param>
    /// <returns>A structured report containing every candidate validation issue.</returns>
    public Res<ConfigurationCandidateValidationReport> ValidateCandidateValue(
        ConfigurationDefinition definition,
        LogicalPath scopePath,
        string json)
    {
        try
        {
            return Res.Ok(candidateValidationService.Validate(definition, scopePath, json));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Gets runtime reload status for Monica-managed configuration definitions.
    /// </summary>
    /// <returns>The runtime reload status report.</returns>
    public async Task<Res<ConfigurationReloadStatusReport>> GetRuntimeReloadStatusAsync()
    {
        try
        {
            return Res.Ok(await runtimeReloadService.GetStatusAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get runtime configuration reload status: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Reloads runtime configuration providers and returns the refreshed reload status report.
    /// </summary>
    /// <returns>The refreshed runtime reload status report.</returns>
    public async Task<Res<ConfigurationReloadStatusReport>> ReloadRuntimeConfigurationAsync()
    {
        try
        {
            return Res.Ok(await runtimeReloadService.ReloadAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to reload runtime configuration: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Reloads the current Monica projection and broadcasts a best-effort reload-all signal to other processes.
    /// </summary>
    /// <returns>The local reload and distributed publication outcome.</returns>
    public async Task<Res<ConfigurationReloadBroadcastResult>> BroadcastReloadAllAsync()
    {
        try
        {
            return Res.Ok(await reloadBroadcastService.BroadcastAllAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to broadcast configuration reload: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets the display-safe effective value for one configuration path.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="logicalPath">The logical path.</param>
    /// <returns>The effective value, if one exists.</returns>
    public async Task<Res<ConfigurationEffectiveValue>> GetEffectiveValueAsync(string definitionKey, LogicalPath logicalPath)
    {
        try
        {
            var definition = await definitionResolver.GetRequiredForReadAsync(definitionKey, CancellationToken.None);
            return await effectiveStateReader.ReadValueAsync(definition, logicalPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get effective configuration value: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets one definition and all display-safe effective values required by the management state view.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <returns>The definition state snapshot.</returns>
    public async Task<Res<ConfigurationDefinitionState>> GetDefinitionStateAsync(string definitionKey)
    {
        try
        {
            var definition = await definitionResolver.GetRequiredForReadAsync(definitionKey, CancellationToken.None);
            return Res.Ok(await effectiveStateReader.ReadDefinitionAsync(definition, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definition state: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets every definition and its display-safe effective values in one cohesive snapshot.
    /// </summary>
    /// <remarks>
    /// Definitions are resolved once and effective documents are loaded in one store operation. Results preserve the
    /// catalog order, and each state's non-root values follow schema preorder so callers can flatten the snapshot without
    /// issuing per-definition or per-node reads.
    /// </remarks>
    /// <returns>All definition state snapshots.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationDefinitionState>>> GetDefinitionStatesAsync()
    {
        try
        {
            var definitions = await definitionResolver.GetMergedDefinitionsAsync(CancellationToken.None);
            return Res.Ok(await effectiveStateReader.ReadDefinitionsAsync(definitions, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definition states: {ex.GetMessageRecursively()}");
        }
    }

    private static ConfigurationDefinitionSummary ToSummary(ConfigurationDefinition definition)
    {
        return new ConfigurationDefinitionSummary
        {
            DefinitionKey = definition.DefinitionKey,
            SectionPath = definition.SectionPath,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            ClrTypeName = definition.ClrTypeName,
            FromProject = definition.FromProject,
            Category = definition.Category,
            SchemaVersion = definition.SchemaVersion,
            DefinitionRevision = definition.DefinitionRevision,
            SchemaHash = definition.SchemaHash,
            Origin = definition.Origin,
            LifecycleState = ConfigurationDefinitionLifecycleState.Active
        };
    }

    private static ConfigurationDefinitionSummary ToSummary(ConfigurationDefinitionCatalogEntry entry)
    {
        if (entry.Diagnostic is not null && entry.PublishedMetadata is not null)
        {
            return ToDiagnosticSummary(entry);
        }

        if (entry.Definition is { } definition)
        {
            return ToSummary(definition) with
            {
                Availability = entry.Availability,
                MetadataDiagnostic = entry.Diagnostic,
                LifecycleState = entry.LifecycleState
            };
        }

        var metadata = entry.PublishedMetadata
                       ?? throw new InvalidOperationException("Unavailable catalog entry has no persisted metadata envelope.");
        return new ConfigurationDefinitionSummary
        {
            DefinitionKey = metadata.DefinitionKey,
            SectionPath = metadata.SectionPath,
            DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName)
                ? metadata.DefinitionKey
                : metadata.DisplayName,
            Description = metadata.Description,
            ClrTypeName = metadata.ClrTypeName,
            FromProject = metadata.FromProject,
            Category = metadata.Category,
            SchemaVersion = metadata.SchemaVersion,
            DefinitionRevision = metadata.DefinitionRevision,
            SchemaHash = metadata.SchemaHash,
            Origin = ConfigurationDefinitionOrigin.PublishedMetadata,
            Availability = entry.Availability,
            MetadataDiagnostic = entry.Diagnostic,
            LifecycleState = entry.LifecycleState
        };
    }

    private static ConfigurationDefinitionSummary ToDiagnosticSummary(ConfigurationDefinitionCatalogEntry entry)
    {
        var metadata = entry.PublishedMetadata!;
        var local = entry.Definition;
        var definitionKey = FirstNonEmpty(metadata.DefinitionKey, local?.DefinitionKey);
        return new ConfigurationDefinitionSummary
        {
            DefinitionKey = definitionKey,
            SectionPath = FirstNonEmpty(metadata.SectionPath, local?.SectionPath),
            DisplayName = FirstNonEmpty(metadata.DisplayName, local?.DisplayName, definitionKey),
            Description = metadata.Description ?? local?.Description,
            ClrTypeName = FirstNonEmpty(metadata.ClrTypeName, local?.ClrTypeName),
            FromProject = FirstNonEmpty(metadata.FromProject, local?.FromProject),
            Category = metadata.Category ?? local?.Category,
            SchemaVersion = metadata.SchemaVersion,
            DefinitionRevision = metadata.DefinitionRevision,
            SchemaHash = metadata.SchemaHash,
            Origin = ConfigurationDefinitionOrigin.PublishedMetadata,
            Availability = entry.Availability,
            MetadataDiagnostic = entry.Diagnostic,
            LifecycleState = entry.LifecycleState
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    /// <summary>
    /// Gets the active configuration storage overview.
    /// </summary>
    /// <returns>The storage overview.</returns>
    public Task<Res<ConfigurationStorageOverview>> GetStorageOverviewAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok(new ConfigurationStorageOverview
            {
                EffectiveValueStore = effectiveValueStore.Descriptor,
                HistoryStore = historyStore.Descriptor,
                MetadataStore = metadataStore.Descriptor,
                HasChangeNotifier = changeNotifiers.Any()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<ConfigurationStorageOverview>>(
                Res.Fail($"Failed to get configuration storage overview: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets current publisher state and revision history for one configuration definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="limit">Maximum number of newest entries to return.</param>
    /// <returns>The current publication overview with newest revisions first.</returns>
    public async Task<Res<ConfigurationDefinitionPublicationOverview>> GetDefinitionPublicationOverviewAsync(
        string definitionKey,
        int limit = 20)
    {
        try
        {
            var overview = await metadataStore.GetDefinitionPublicationOverviewAsync(
                definitionKey,
                limit,
                CancellationToken.None);
            return Res.Ok(_definitionLifecycleService.EnrichWithLocalRegistration(overview));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration definition publication overview: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets current logical-service and reload impact for prospective parameter changes.
    /// </summary>
    /// <param name="targets">Configuration parameters whose prospective changes are being reviewed.</param>
    /// <returns>
    /// Participating logical publishers, parameter reload behavior, and parameters without known consumers.
    /// </returns>
    public async Task<Res<ConfigurationDefinitionChangeImpact>> GetDefinitionChangeImpactAsync(
        IReadOnlyCollection<ConfigurationParameterChangeTarget> targets)
    {
        try
        {
            return Res.Ok(await definitionChangeImpactService.GetImpactAsync(
                targets,
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail(
                $"Failed to get configuration definition change impact: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Previews the records deleted and retained when permanently purging one retired definition.
    /// </summary>
    /// <param name="definitionKey">The stable definition key.</param>
    /// <returns>The purge impact and current concurrency revision.</returns>
    public async Task<Res<ConfigurationDefinitionPurgePreview>> PreviewDefinitionPurgeAsync(string definitionKey)
    {
        try
        {
            return Res.Ok(await _definitionLifecycleService.PreviewPurgeAsync(
                definitionKey,
                CancellationToken.None));
        }
        catch (KeyNotFoundException ex)
        {
            return Res.Fail(
                $"Failed to preview configuration definition purge: {ex.GetMessageRecursively()}",
                ResStatus.NotFound);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to preview configuration definition purge: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Permanently purges one retired definition after rechecking its publisher state and reviewed revision.
    /// </summary>
    /// <remarks>
    /// The canonical definition, definition-publication history, and current Monica effective value are deleted.
    /// Immutable value history, mutation groups, and unified-version snapshots remain available for audit.
    /// </remarks>
    /// <param name="request">The definition key and revision returned by the purge preview.</param>
    /// <returns>A successful result when the retired definition was purged.</returns>
    public async Task<Res> PurgeDefinitionAsync(ConfigurationDefinitionPurgeRequest request)
    {
        try
        {
            await _definitionLifecycleService.PurgeAsync(request, CancellationToken.None);
            return Res.Ok();
        }
        catch (ConfigurationConcurrencyConflictException ex)
        {
            return Res.Fail(
                $"Failed to purge configuration definition: {ex.GetMessageRecursively()}",
                ResStatus.Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Res.Fail(
                $"Failed to purge configuration definition: {ex.GetMessageRecursively()}",
                ResStatus.NotFound);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to purge configuration definition: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets all runtime store states currently known by this process.
    /// </summary>
    /// <returns>The store states.</returns>
    public Task<Res<IReadOnlyList<ConfigurationStoreState>>> GetStoreStatesAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok(storeStateTracker.GetStates()));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<IReadOnlyList<ConfigurationStoreState>>>(
                Res.Fail($"Failed to get configuration store states: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets all runtime Microsoft configuration sources.
    /// </summary>
    /// <returns>Configuration source descriptors ordered by runtime priority index.</returns>
    public Task<Res<IReadOnlyList<ConfigurationSourceDescriptor>>> GetConfigurationSourcesAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok(sourceInspector.GetSources()));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<IReadOnlyList<ConfigurationSourceDescriptor>>>(
                Res.Fail($"Failed to get configuration sources: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets managed configuration values supplied by each runtime Microsoft configuration source.
    /// </summary>
    /// <returns>Source inventories ordered from highest priority to lowest priority.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationSourceInventory>>> GetConfigurationSourceInventoriesAsync()
    {
        try
        {
            return Res.Ok(await sourceInspector.GetSourceInventoriesAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration source inventory: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets source contribution counts for one definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <returns>Source contribution rows.</returns>
    public Task<Res<IReadOnlyList<ConfigurationDefinitionSourceContribution>>> GetDefinitionSourceContributionsAsync(string definitionKey)
    {
        try
        {
            if (!definitionResolver.TryGetLocal(definitionKey, out var definition))
            {
                return Task.FromResult<Res<IReadOnlyList<ConfigurationDefinitionSourceContribution>>>(
                    Res.Fail("Configuration source-chain inspection is only available for definitions scanned by the current process."));
            }

            return Task.FromResult(Res.Ok(sourceInspector.GetDefinitionContributions(definition)));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<IReadOnlyList<ConfigurationDefinitionSourceContribution>>>(
                Res.Fail($"Failed to get configuration source contributions: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets the source chain for one configuration path.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="logicalPath">The logical path.</param>
    /// <returns>The source chain.</returns>
    public Task<Res<ConfigurationSourceChain>> GetSourceChainAsync(string definitionKey, LogicalPath logicalPath)
    {
        try
        {
            if (!definitionResolver.TryGetLocal(definitionKey, out var definition))
            {
                return Task.FromResult<Res<ConfigurationSourceChain>>(
                    Res.Fail("Configuration source-chain inspection is only available for definitions scanned by the current process."));
            }

            return Task.FromResult(Res.Ok(sourceInspector.GetSourceChain(definition, logicalPath)));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<ConfigurationSourceChain>>(
                Res.Fail($"Failed to get configuration source chain: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets source chains for several configuration paths from one runtime-provider snapshot.
    /// </summary>
    /// <param name="definitionKey">The locally scanned definition key.</param>
    /// <param name="logicalPaths">The logical paths to inspect, in result order.</param>
    /// <returns>One source chain for each requested path.</returns>
    public Task<Res<IReadOnlyList<ConfigurationSourceChain>>> GetSourceChainsAsync(
        string definitionKey,
        IReadOnlyList<LogicalPath> logicalPaths)
    {
        try
        {
            if (!definitionResolver.TryGetLocal(definitionKey, out var definition))
            {
                return Task.FromResult<Res<IReadOnlyList<ConfigurationSourceChain>>>(
                    Res.Fail("Configuration source-chain inspection is only available for definitions scanned by the current process."));
            }

            return Task.FromResult(Res.Ok(sourceInspector.GetSourceChains(definition, logicalPaths)));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<IReadOnlyList<ConfigurationSourceChain>>>(
                Res.Fail($"Failed to get configuration source chains: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets a display-safe JSON file view for one source.
    /// </summary>
    /// <param name="sourceKey">The source key.</param>
    /// <returns>The source file view.</returns>
    public async Task<Res<ConfigurationSourceFileView>> GetSourceFileViewAsync(string sourceKey)
    {
        try
        {
            return Res.Ok(await sourceInspector.GetSourceFileViewAsync(sourceKey, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration source file: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets the current Microsoft configuration debug view.
    /// </summary>
    /// <returns>The debug view text.</returns>
    public Task<Res<string>> GetDebugViewAsync()
    {
        try
        {
            var debugView = runtimeContext.Root is { } root
                ? root.GetDebugView()
                : runtimeContext.Configuration.AsEnumerable().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}")
                    .JoinAsString(Environment.NewLine);

            return Task.FromResult(Res.Ok<string>(debugView));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Res<string>>(Res.Fail($"Failed to get configuration debug view: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Applies a reviewed configuration mutation group through one persistence coordinator.
    /// </summary>
    /// <param name="request">The mutation group request.</param>
    /// <returns>The group outcome, including post-commit reload and notification issues.</returns>
    public async Task<Res<ConfigurationMutationGroupApplyResult>> ApplyMutationGroupAsync(
        ConfigurationMutationGroupApplyRequest request)
    {
        try
        {
            var result = await mutationGroupApplyService.ApplyAsync(request, CancellationToken.None);
            return result.Status == ConfigurationMutationGroupApplyStatus.Rejected
                ? Res.Fail("The complete configuration validation rejected this group before persistence.").WithData(result)
                : Res.Ok(result);
        }
        catch (ConfigurationValidationExecutionException ex)
        {
            return Res.Fail(SafeMutationFailure(ex)).WithData(new ConfigurationMutationGroupApplyResult
            {
                Status = ConfigurationMutationGroupApplyStatus.Rejected,
                ValidationPreview = new ConfigurationMutationGroupValidationPreview
                {
                    ValidationFingerprint = string.Empty,
                    Definitions = [new ConfigurationCandidateValidationReport
                    {
                        DefinitionKey = ex.DefinitionKey, DefinitionDisplayName = ex.DefinitionKey,
                        ScopePath = LogicalPath.Root, Scope = ConfigurationValidationScope.CompleteAggregate,
                        Coverage = ConfigurationValidationCoverage.Failed,
                        Issues = [new ConfigurationCandidateValidationIssue
                        {
                            DefinitionKey = ex.DefinitionKey, DefinitionDisplayName = ex.DefinitionKey,
                            LogicalPath = ex.LogicalPath, LogicalPaths = [ex.LogicalPath],
                            Kind = ex.Kind, NodeDisplayName = ex.DefinitionKey, Problem = ex.Message
                        }]
                    }],
                    Problems = [new ConfigurationMutationValidationProblem
                    {
                        Code = "ValidationExecution", DefinitionKey = ex.DefinitionKey,
                        Message = "The complete validation could not execute safely."
                    }]
                }
            });
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>Previews complete, target-aware validation without persisting any configuration.</summary>
    /// <param name="request">The proposed mutation commands and their physical targets.</param>
    /// <param name="cancellationToken">Cancellation token owned by the caller.</param>
    /// <returns>The complete validation reports and review fingerprint.</returns>
    public async Task<Res<ConfigurationMutationGroupValidationPreview>> PreviewMutationGroupAsync(
        ConfigurationMutationGroupApplyRequest request, CancellationToken cancellationToken = default)
    {
        try { return Res.Ok(await mutationGroupApplyService.PreviewAsync(request, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Res.Fail(SafeMutationFailure(ex)); }
    }

    /// <summary>Reads the latest observed default-options attempt without resolving or retaining options instances.</summary>
    /// <param name="definitionKey">The locally discovered configuration owner.</param>
    /// <param name="cancellationToken">Cancellation token owned by the caller.</param>
    /// <returns>The latest attempt, or null when default options have not been observed.</returns>
    public Task<Res<ConfigurationOptionsValidationReport?>> GetOptionsValidationReportAsync(
        string definitionKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Res.Ok(optionsValidationDiagnostics.GetLatestReport(definitionKey)));
    }

    /// <summary>Reads bounded latest default-options attempts separately from effective-source validation.</summary>
    /// <param name="cancellationToken">Cancellation token owned by the caller.</param>
    /// <returns>The latest observed attempts for locally discovered definitions.</returns>
    public Task<Res<IReadOnlyList<ConfigurationOptionsValidationReport>>> GetOptionsValidationReportsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Res.Ok(optionsValidationDiagnostics.GetReports()));
    }

    private static string SafeMutationFailure(Exception exception) => exception switch
    {
        ConfigurationValidationExecutionException => "Configuration validation could not execute safely. No configuration values were persisted.",
        ConfigurationConcurrencyConflictException => "The reviewed configuration changed. Request a new preview before applying it.",
        ConfigurationSchemaMismatchException => "The reviewed configuration contract changed. Request a new preview before applying it.",
        _ => "The configuration operation failed. Review the current configuration before retrying."
    };

    /// <summary>
    /// Mutates a configuration value.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <returns>The mutation result.</returns>
    public async Task<Res<ConfigurationMutationResult>> MutateAsync(ConfigurationMutationRequest request)
    {
        try
        {
            var applied = await mutationGroupApplyService.ApplyAsync(new ConfigurationMutationGroupApplyRequest
            {
                Label = $"Change {request.DefinitionKey} {request.LogicalPath.ToCanonicalString()}",
                Reason = request.Context.Reason,
                Context = request.Context,
                Commands =
                [
                    new ConfigurationMutationCommand
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        DefinitionKey = request.DefinitionKey,
                        LogicalPath = request.LogicalPath,
                        MutationKind = request.MutationKind,
                        Value = request.Value,
                        ExpectedSchemaVersion = request.ExpectedSchemaVersion,
                        Target = new ConfigurationEffectiveStoreMutationTarget
                        {
                            ExpectedVersion = request.ExpectedValueVersion
                        }
                    }
                ]
            }, CancellationToken.None);
            return Res.Ok(GetSingleAppliedResult(applied));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Mutates one external runtime configuration source.
    /// </summary>
    /// <param name="request">The source mutation request.</param>
    /// <returns>The mutation result.</returns>
    public async Task<Res<ConfigurationMutationResult>> MutateSourceAsync(ConfigurationSourceMutationRequest request)
    {
        try
        {
            var applied = await mutationGroupApplyService.ApplyAsync(new ConfigurationMutationGroupApplyRequest
            {
                Label = $"Change {request.DefinitionKey} {request.LogicalPath.ToCanonicalString()}",
                Reason = request.Context.Reason,
                Context = request.Context,
                Commands =
                [
                    new ConfigurationMutationCommand
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        DefinitionKey = request.DefinitionKey,
                        LogicalPath = request.LogicalPath,
                        MutationKind = request.MutationKind,
                        Value = request.Value,
                        ExpectedSchemaVersion = request.ExpectedSchemaVersion,
                        Target = new ConfigurationExternalSourceMutationTarget
                        {
                            SourceKey = request.SourceKey,
                            ExpectedRevision = request.ExpectedSourceRevision
                        }
                    }
                ]
            }, CancellationToken.None);
            return Res.Ok(GetSingleAppliedResult(applied));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    private static ConfigurationMutationResult GetSingleAppliedResult(ConfigurationMutationGroupApplyResult applied)
    {
        var outcome = applied.Outcomes.Single();
        if (outcome is not { Status: ConfigurationMutationOutcomeStatus.Applied, Result: not null })
        {
            throw new InvalidOperationException(outcome.ErrorMessage ?? "The configuration mutation was not applied.");
        }

        return outcome.Result with { PostCommitIssues = applied.PostCommitIssues };
    }

    /// <summary>
    /// Gets the current source revision hash for optimistic source mutation.
    /// </summary>
    /// <param name="sourceKey">The source key.</param>
    /// <returns>The current source revision, or null when no revision is available.</returns>
    public async Task<Res<string?>> GetSourceRevisionAsync(string sourceKey)
    {
        try
        {
            var source = sourceInspector.GetRequiredSource(sourceKey);
            return Res.Ok<string?>(await sourceWriter.GetRevisionAsync(source, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration source revision: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets mutation history for one value.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="logicalPath">The logical path.</param>
    /// <returns>History records.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationValueHistory>>> GetHistoryAsync(string definitionKey, LogicalPath logicalPath)
    {
        try
        {
            var history = await historyService.GetHistoryAsync(definitionKey, logicalPath, CancellationToken.None);
            return Res.Ok(history);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration value history: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Queries mutation history across definitions and paths.
    /// </summary>
    /// <param name="from">Earliest modification time to include.</param>
    /// <param name="to">Latest modification time to include.</param>
    /// <param name="definitionKey">Definition key filter.</param>
    /// <param name="logicalPath">Logical path filter.</param>
    /// <param name="mutationGroupId">Mutation group filter.</param>
    /// <returns>The matching history rows.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationValueHistory>>> QueryHistoryAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? definitionKey = null,
        LogicalPath? logicalPath = null,
        string? mutationGroupId = null)
    {
        try
        {
            return Res.Ok(await historyService.QueryHistoryAsync(from, to, definitionKey, logicalPath, mutationGroupId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to query configuration value history: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Queries a bounded page of mutation history without splitting matching mutation groups across pages.
    /// </summary>
    /// <param name="request">The filters and mutation-unit pagination bounds.</param>
    /// <returns>The matching history page in deterministic newest-first order.</returns>
    public async Task<Res<ConfigurationHistoryPageResult>> QueryHistoryPageAsync(
        ConfigurationHistoryPageRequest request)
    {
        try
        {
            return Res.Ok(await historyService.QueryHistoryPageAsync(request, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to query configuration value history page: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Lists unified configuration versions.
    /// </summary>
    /// <param name="from">Earliest creation time to include.</param>
    /// <param name="to">Latest creation time to include.</param>
    /// <param name="definitionKey">Definition key filter.</param>
    /// <param name="limit">Maximum number of versions to return.</param>
    /// <returns>The matching unified version summaries.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationUnifiedVersionSummary>>> GetUnifiedVersionsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? definitionKey = null,
        int limit = 100)
    {
        try
        {
            return Res.Ok(await unifiedVersionService.ListVersionsAsync(
                from,
                to,
                definitionKey,
                limit,
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get unified configuration versions: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets one unified configuration version snapshot.
    /// </summary>
    /// <param name="version">The version number.</param>
    /// <returns>The version snapshot.</returns>
    public async Task<Res<ConfigurationUnifiedVersionSnapshot>> GetUnifiedVersionAsync(long version)
    {
        try
        {
            var snapshot = await unifiedVersionService.GetVersionAsync(version, CancellationToken.None);
            return snapshot is null
                ? Res.Fail($"Unified configuration version '{version}' was not found.")
                : Res.Ok(snapshot);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get unified configuration version: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Permanently deletes a historical unified configuration version and its captured definition documents.
    /// </summary>
    /// <remarks>
    /// The latest unified version represents the current version and cannot be deleted. Remaining versions keep their
    /// original numbers, and deleted version numbers are not reused.
    /// </remarks>
    /// <param name="version">The historical unified version number to delete.</param>
    /// <returns>
    /// A success result when the version has been deleted; otherwise, a failure result with the store diagnostic.
    /// </returns>
    public async Task<Res> DeleteUnifiedVersionAsync(long version)
    {
        try
        {
            await unifiedVersionService.DeleteVersionAsync(version, CancellationToken.None);
            return Res.Ok();
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to delete unified configuration version: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Compares two unified configuration versions.
    /// </summary>
    /// <param name="originVersion">The origin version number.</param>
    /// <param name="targetVersion">The target version number.</param>
    /// <returns>The version comparison.</returns>
    public async Task<Res<ConfigurationUnifiedVersionComparison>> CompareUnifiedVersionsAsync(
        long originVersion,
        long targetVersion)
    {
        try
        {
            return Res.Ok(await unifiedVersionService.CompareVersionsAsync(
                originVersion,
                targetVersion,
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to compare unified configuration versions: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Previews applying one unified configuration version to current sources.
    /// </summary>
    /// <param name="version">The version number.</param>
    /// <returns>The apply preview.</returns>
    /// <param name="cancellationToken">Cancellation token owned by the caller.</param>
    public async Task<Res<ConfigurationUnifiedVersionApplyPreview>> PreviewUnifiedVersionRollbackAsync(
        long version, CancellationToken cancellationToken = default)
    {
        try
        {
            return Res.Ok(await unifiedVersionService.PreviewRollbackAsync(version, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Applies the changed definition values from a reviewed unified-version rollback preview.
    /// </summary>
    /// <param name="request">The reviewed rollback request, including its preview fingerprint and acknowledgements.</param>
    /// <returns>The rollback result.</returns>
    public async Task<Res<ConfigurationUnifiedVersionRollbackResult>> RollbackUnifiedVersionAsync(
        ConfigurationUnifiedVersionRollbackRequest request)
    {
        try
        {
            return Res.Ok(await unifiedVersionService.RollbackToVersionAsync(
                request,
                CancellationToken.None));
        }
        catch (ConfigurationConcurrencyConflictException ex)
        {
            return Res.Fail(
                SafeMutationFailure(ex),
                ResStatus.Conflict);
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Lists mutation groups.
    /// </summary>
    /// <param name="from">Earliest creation time to include.</param>
    /// <param name="to">Latest creation time to include.</param>
    /// <param name="definitionKey">Definition key filter.</param>
    /// <returns>The matching groups.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationMutationGroup>>> GetMutationGroupsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? definitionKey = null)
    {
        try
        {
            return Res.Ok(await mutationGroupService.ListAsync(from, to, definitionKey, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration mutation groups: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Queries a bounded page of persisted mutation groups.
    /// </summary>
    /// <param name="request">The filters and pagination bounds.</param>
    /// <returns>The matching group page in deterministic newest-first order.</returns>
    public async Task<Res<ConfigurationMutationGroupPageResult>> QueryMutationGroupsPageAsync(
        ConfigurationMutationGroupPageRequest request)
    {
        try
        {
            return Res.Ok(await mutationGroupService.QueryPageAsync(request, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to query configuration mutation group page: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets one mutation group.
    /// </summary>
    /// <param name="groupId">The group identity.</param>
    /// <returns>The group.</returns>
    public async Task<Res<ConfigurationMutationGroup>> GetMutationGroupAsync(string groupId)
    {
        try
        {
            var group = await mutationGroupService.GetAsync(groupId, CancellationToken.None);
            return group is null
                ? Res.Fail($"Configuration mutation group '{groupId}' was not found.")
                : Res.Ok(group);
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration mutation group: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets history rows for a mutation group.
    /// </summary>
    /// <param name="groupId">The group identity.</param>
    /// <returns>The group history rows.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationValueHistory>>> GetGroupHistoryAsync(string groupId)
    {
        try
        {
            return Res.Ok(await mutationGroupService.GetGroupHistoryAsync(groupId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail($"Failed to get configuration mutation group history: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Previews selected history rollbacks against the current physical target values.
    /// </summary>
    /// <param name="historyIds">The history identities to preview.</param>
    /// <param name="cancellationToken">Cancellation token owned by the caller.</param>
    /// <returns>The current values and concurrency-bound rollback plan.</returns>
    public async Task<Res<ConfigurationHistoryRollbackPreview>> PreviewHistoryRollbackAsync(
        IReadOnlyList<string> historyIds, CancellationToken cancellationToken = default)
    {
        try
        {
            return Res.Ok(await rollbackService.PreviewHistoriesAsync(historyIds, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Rolls one history row back to its previous value.
    /// </summary>
    /// <param name="historyId">The history record identity.</param>
    /// <param name="planToken">The current-state preview token reviewed by the operator.</param>
    /// <param name="reason">Optional rollback reason.</param>
    /// <returns>The rollback mutation result.</returns>
    public async Task<Res<ConfigurationMutationResult>> RollbackHistoryAsync(
        string historyId,
        string planToken,
        string? reason = null)
    {
        try
        {
            return Res.Ok(await rollbackService.RollbackHistoryAsync(
                historyId,
                planToken,
                new ConfigurationMutationContext { Reason = reason },
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Rolls selected history rows back in reverse history order.
    /// </summary>
    /// <param name="historyIds">The history record identities.</param>
    /// <param name="planToken">The current-state preview token reviewed by the operator.</param>
    /// <param name="reason">Optional rollback reason.</param>
    /// <returns>The rollback mutation results.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationMutationResult>>> RollbackHistoriesAsync(
        IReadOnlyList<string> historyIds,
        string planToken,
        string? reason = null)
    {
        try
        {
            return Res.Ok(await rollbackService.RollbackHistoriesAsync(
                historyIds,
                planToken,
                new ConfigurationMutationContext { Reason = reason },
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }

    /// <summary>
    /// Rolls one mutation group back in reverse history order.
    /// </summary>
    /// <param name="groupId">The group identity.</param>
    /// <param name="planToken">The current-state preview token reviewed by the operator.</param>
    /// <param name="reason">Optional rollback reason.</param>
    /// <returns>The rollback mutation results.</returns>
    public async Task<Res<IReadOnlyList<ConfigurationMutationResult>>> RollbackGroupAsync(
        string groupId,
        string planToken,
        string? reason = null)
    {
        try
        {
            return Res.Ok(await rollbackService.RollbackGroupAsync(
                groupId,
                planToken,
                new ConfigurationMutationContext { Reason = reason },
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Res.Fail(SafeMutationFailure(ex));
        }
    }
}
