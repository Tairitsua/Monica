using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Localization;
using Monica.Configuration.UI.Models;
using Monica.Configuration.UI.State;
using Monica.Core.Results;

namespace Monica.Configuration.UI.Support;

/// <summary>
/// Creates and analyzes Monica-managed configuration parameter packages.
/// </summary>
internal sealed class ConfigurationParameterPackageService(
    ConfigurationFacade facade,
    ConfigurationJsonDraftService draftService,
    IStringLocalizer<ConfigurationUIResource> localizer,
    ConfigurationValueVisibility visibility)
{
    /// <summary>
    /// Creates a versioned export document from current runtime effective values.
    /// </summary>
    /// <param name="request">The export request.</param>
    /// <param name="progress">Optional progress sink for reporting export packaging stages and discovered counts.</param>
    /// <returns>The export document.</returns>
    public async Task<ConfigurationExportDocument> CreateExportAsync(
        ConfigurationExportRequest request,
        IProgress<ConfigurationPackageProgress>? progress = null)
    {
        var includeSensitive = request.IncludeSensitive || visibility.ShowSensitiveValues;
        var definitions = await LoadExportDefinitionsAsync(request, progress);

        var exportedDefinitions = new List<ConfigurationExportDefinition>();
        var exportedDefinitionCount = 0;
        var redactedPathCount = 0;
        ReportExportProgress(progress, definitions.Count, definitions.Count, exportedDefinitionCount, redactedPathCount);

        foreach (var definition in definitions)
        {
            ReportExportProgress(
                progress,
                definitions.Count,
                definitions.Count,
                exportedDefinitionCount,
                redactedPathCount,
                definition.DisplayName,
                definition.DefinitionKey);
            var exportedDefinition = await CreateExportDefinitionAsync(definition, includeSensitive);
            exportedDefinitions.Add(exportedDefinition);
            exportedDefinitionCount++;
            redactedPathCount += exportedDefinition.RedactedPaths.Count;
            ReportExportProgress(
                progress,
                definitions.Count,
                definitions.Count,
                exportedDefinitionCount,
                redactedPathCount,
                definition.DisplayName,
                definition.DefinitionKey);
        }

        return new ConfigurationExportDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            ExportedBy = request.ExportedBy,
            SystemVersion = request.SystemVersion,
            EnvironmentName = request.EnvironmentName,
            IncludeSensitive = includeSensitive,
            Definitions = exportedDefinitions
        };
    }

    /// <summary>
    /// Analyzes an uploaded export document and converts it into UI staged state.
    /// </summary>
    /// <param name="document">The import document.</param>
    /// <param name="fileName">The uploaded file name.</param>
    /// <param name="progress">Optional progress sink for reporting import analysis stages and discovered counts.</param>
    /// <returns>The import report.</returns>
    public async Task<ConfigurationImportReport> AnalyzeImportAsync(
        ConfigurationExportDocument document,
        string? fileName,
        IProgress<ConfigurationPackageProgress>? progress = null)
    {
        var diagnostics = new List<ConfigurationImportDiagnostic>();
        if (document.FormatVersion != 1)
        {
            diagnostics.Add(new ConfigurationImportDiagnostic
            {
                Severity = ConfigurationImportDiagnosticSeverity.Warning,
                Message = localizer["ImportExport:Diagnostics:FormatVersionMismatch", document.FormatVersion]
            });
        }

        var totalPackageDefinitions = document.Definitions.Count;
        var definitions = await LoadDefinitionsAsync(
            progress,
            ConfigurationPackageProgressOperation.Import,
            totalPackageDefinitions);
        var definitionsByKey = definitions.ToDictionary(definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase);
        var drafts = new List<ConfigurationJsonDraftResult>();
        var processedPackageDefinitions = 0;
        var changeCount = 0;
        var validationIssueCount = 0;
        var diagnosticCount = diagnostics.Count;

        foreach (var exportedDefinition in document.Definitions)
        {
            if (!definitionsByKey.TryGetValue(exportedDefinition.DefinitionKey, out var definition))
            {
                diagnostics.Add(new ConfigurationImportDiagnostic
                {
                    Severity = ConfigurationImportDiagnosticSeverity.Warning,
                    DefinitionKey = exportedDefinition.DefinitionKey,
                    Message = localizer["ImportExport:Diagnostics:UnknownDefinition", exportedDefinition.DefinitionKey]
                });
                diagnosticCount++;
                processedPackageDefinitions++;
                ReportAnalyzeProgress(
                    progress,
                    definitions.Count,
                    totalPackageDefinitions,
                    processedPackageDefinitions,
                    exportedDefinition.DisplayName,
                    exportedDefinition.DefinitionKey,
                    changeCount,
                    validationIssueCount,
                    diagnosticCount);
                continue;
            }

            ReportAnalyzeProgress(
                progress,
                definitions.Count,
                totalPackageDefinitions,
                processedPackageDefinitions,
                definition.DisplayName,
                definition.DefinitionKey,
                changeCount,
                validationIssueCount,
                diagnosticCount);

            var definitionDiagnostics = new List<ConfigurationImportDiagnostic>();
            if (exportedDefinition.SchemaVersion != definition.SchemaVersion)
            {
                definitionDiagnostics.Add(new ConfigurationImportDiagnostic
                {
                    Severity = ConfigurationImportDiagnosticSeverity.Warning,
                    DefinitionKey = definition.DefinitionKey,
                    Message = localizer["ImportExport:Diagnostics:SchemaVersionMismatch", exportedDefinition.SchemaVersion, definition.SchemaVersion]
                });
            }

            if (!string.IsNullOrWhiteSpace(exportedDefinition.SchemaHash)
                && !string.Equals(exportedDefinition.SchemaHash, definition.SchemaHash, StringComparison.Ordinal))
            {
                definitionDiagnostics.Add(new ConfigurationImportDiagnostic
                {
                    Severity = ConfigurationImportDiagnosticSeverity.Warning,
                    DefinitionKey = definition.DefinitionKey,
                    Message = localizer["ImportExport:Diagnostics:SchemaHashMismatch",
                        definition.DisplayName,
                        definition.DefinitionKey,
                        HashForDisplay(exportedDefinition.SchemaHash),
                        HashForDisplay(definition.SchemaHash)]
                });
            }

            var effectiveValue = await LoadEffectiveValueAsync(definition.DefinitionKey, LogicalPath.Root);
            var scalarValues = await LoadScalarEffectiveValuesAsync(definition, definition.Root, effectiveValue);
            var json = exportedDefinition.Value?.ToJsonString(ConfigurationJsonDisplayFormatter.ReadableJsonOptions) ?? "null";
            var draft = draftService.Analyze(new ConfigurationJsonDraftRequest
            {
                Definition = definition,
                ScopeNode = definition.Root,
                EffectiveValue = effectiveValue,
                ScalarEffectiveValues = scalarValues,
                Json = json,
                RedactedPaths = exportedDefinition.RedactedPaths.ToHashSet(StringComparer.Ordinal),
                // Keep imported state at field granularity so the state page editors can reflect staged values.
                CompactChanges = false
            });

            drafts.Add(draft with
            {
                Diagnostics = definitionDiagnostics.Concat(draft.Diagnostics).ToArray()
            });
            changeCount += draft.Changes.Count;
            validationIssueCount += draft.ValidationIssues.Count;
            diagnosticCount += definitionDiagnostics.Count + draft.Diagnostics.Count;
            processedPackageDefinitions++;
            ReportAnalyzeProgress(
                progress,
                definitions.Count,
                totalPackageDefinitions,
                processedPackageDefinitions,
                definition.DisplayName,
                definition.DefinitionKey,
                changeCount,
                validationIssueCount,
                diagnosticCount);
        }

        return new ConfigurationImportReport
        {
            FileName = fileName,
            Document = document,
            Drafts = drafts,
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// Loads current effective scalar values for a definition.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="scopeNode">The schema scope represented by <paramref name="effectiveValue"/>.</param>
    /// <param name="effectiveValue">The effective-value snapshot whose concrete collection paths should be inspected.</param>
    /// <returns>Effective values keyed by logical path.</returns>
    public async Task<IReadOnlyDictionary<LogicalPath, ConfigurationEffectiveValue>> LoadScalarEffectiveValuesAsync(
        ConfigurationDefinition definition,
        ConfigurationNodeDefinition scopeNode,
        ConfigurationEffectiveValue effectiveValue)
    {
        var paths = ConfigurationConcreteScalarPathEnumerator.Enumerate(
            scopeNode,
            effectiveValue.DisplayValue);
        if (definition.Origin == ConfigurationDefinitionOrigin.LocalScan)
        {
            return await LoadLocalScalarEffectiveValuesAsync(definition, effectiveValue.Version, paths);
        }

        var values = new Dictionary<LogicalPath, ConfigurationEffectiveValue>();
        foreach (var path in paths)
        {
            values[path] = await LoadEffectiveValueAsync(definition.DefinitionKey, path);
        }

        return values;
    }

    private async Task<IReadOnlyDictionary<LogicalPath, ConfigurationEffectiveValue>> LoadLocalScalarEffectiveValuesAsync(
        ConfigurationDefinition definition,
        long? effectiveStoreVersion,
        IReadOnlyList<LogicalPath> paths)
    {
        var result = await facade.GetSourceChainsAsync(definition.DefinitionKey, paths, visibility.ShowSensitiveValues);
        if (result.IsFailed(out var error, out var chains))
        {
            throw new InvalidOperationException(error.Message);
        }

        return chains.ToDictionary(
            static chain => chain.LogicalPath,
            chain =>
            {
                var effectiveSourceValue = chain.Values.FirstOrDefault(static value => value.IsEffective);
                var source = effectiveSourceValue?.Source;
                return new ConfigurationEffectiveValue
                {
                    DefinitionKey = chain.DefinitionKey,
                    LogicalPath = chain.LogicalPath,
                    ConfigurationPath = chain.ConfigurationPath,
                    DisplayValue = effectiveSourceValue?.DisplayValue,
                    IsSensitive = effectiveSourceValue?.IsSensitive is true,
                    Version = source?.Kind is null or ConfigurationSourceKind.MonicaEffectiveStore
                        ? effectiveStoreVersion
                        : null,
                    EffectiveSource = source
                };
            });
    }

    /// <summary>
    /// Loads all managed configuration definitions.
    /// </summary>
    /// <param name="progress">Optional progress sink for reporting definition loading progress.</param>
    /// <param name="operation">The package operation that owns the definition loading work.</param>
    /// <param name="totalProcessDefinitionCount">The total number of definitions that will be processed after loading completes.</param>
    /// <returns>Definitions ordered by display name.</returns>
    public async Task<IReadOnlyList<ConfigurationDefinition>> LoadDefinitionsAsync(
        IProgress<ConfigurationPackageProgress>? progress = null,
        ConfigurationPackageProgressOperation operation = ConfigurationPackageProgressOperation.Import,
        int? totalProcessDefinitionCount = null)
    {
        var summariesResult = await facade.GetDefinitionsAsync();
        if (summariesResult.IsFailed(out var summaryError, out var summaries))
        {
            throw new InvalidOperationException(summaryError.Message);
        }

        var definitions = new List<ConfigurationDefinition>();
        var orderedSummaries = summaries
            .Where(summary => summary.Kind == ConfigurationKind.Managed)
            .OrderBy(summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolvedTotalProcessDefinitionCount = totalProcessDefinitionCount ?? orderedSummaries.Length;
        ReportLoadDefinitionsProgress(
            progress,
            operation,
            0,
            orderedSummaries.Length,
            resolvedTotalProcessDefinitionCount);

        foreach (var summary in orderedSummaries)
        {
            definitions.Add(await LoadDefinitionAsync(summary.DefinitionKey));
            ReportLoadDefinitionsProgress(
                progress,
                operation,
                definitions.Count,
                orderedSummaries.Length,
                resolvedTotalProcessDefinitionCount,
                summary.DisplayName,
                summary.DefinitionKey);
        }

        return definitions;
    }

    private async Task<IReadOnlyList<ConfigurationDefinition>> LoadExportDefinitionsAsync(
        ConfigurationExportRequest request,
        IProgress<ConfigurationPackageProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(request.DefinitionKey))
        {
            return await LoadDefinitionsAsync(progress, ConfigurationPackageProgressOperation.Export);
        }

        ReportLoadDefinitionsProgress(
            progress,
            ConfigurationPackageProgressOperation.Export,
            0,
            1,
            1);
        var definition = await LoadDefinitionAsync(request.DefinitionKey);
        if (definition.Kind == ConfigurationKind.Startup)
        {
            throw new InvalidOperationException(localizer["Startup:ReadOnly"]);
        }
        ReportLoadDefinitionsProgress(
            progress,
            ConfigurationPackageProgressOperation.Export,
            1,
            1,
            1,
            definition.DisplayName,
            definition.DefinitionKey);
        return [definition];
    }

    private static void ReportLoadDefinitionsProgress(
        IProgress<ConfigurationPackageProgress>? progress,
        ConfigurationPackageProgressOperation operation,
        int loadedDefinitionCount,
        int totalDefinitionCount,
        int totalProcessDefinitionCount,
        string? currentDefinitionDisplayName = null,
        string? currentDefinitionKey = null)
    {
        progress?.Report(new ConfigurationPackageProgress
        {
            Operation = operation,
            Stage = ConfigurationPackageProgressStage.LoadingDefinitions,
            LoadedDefinitionCount = loadedDefinitionCount,
            TotalDefinitionCount = totalDefinitionCount,
            TotalProcessDefinitionCount = totalProcessDefinitionCount,
            CurrentDefinitionDisplayName = currentDefinitionDisplayName,
            CurrentDefinitionKey = currentDefinitionKey
        });
    }

    private static void ReportAnalyzeProgress(
        IProgress<ConfigurationPackageProgress>? progress,
        int totalLoadedDefinitions,
        int totalPackageDefinitions,
        int processedPackageDefinitions,
        string? currentDefinitionDisplayName,
        string? currentDefinitionKey,
        int changeCount,
        int validationIssueCount,
        int diagnosticCount)
    {
        progress?.Report(new ConfigurationPackageProgress
        {
            Operation = ConfigurationPackageProgressOperation.Import,
            Stage = ConfigurationPackageProgressStage.AnalyzingDefinitions,
            LoadedDefinitionCount = totalLoadedDefinitions,
            TotalDefinitionCount = totalLoadedDefinitions,
            ProcessedDefinitionCount = processedPackageDefinitions,
            TotalProcessDefinitionCount = totalPackageDefinitions,
            CurrentDefinitionDisplayName = currentDefinitionDisplayName,
            CurrentDefinitionKey = currentDefinitionKey,
            ChangeCount = changeCount,
            ValidationIssueCount = validationIssueCount,
            DiagnosticCount = diagnosticCount
        });
    }

    private static void ReportExportProgress(
        IProgress<ConfigurationPackageProgress>? progress,
        int totalLoadedDefinitions,
        int totalExportDefinitions,
        int exportedDefinitionCount,
        int redactedPathCount,
        string? currentDefinitionDisplayName = null,
        string? currentDefinitionKey = null)
    {
        progress?.Report(new ConfigurationPackageProgress
        {
            Operation = ConfigurationPackageProgressOperation.Export,
            Stage = ConfigurationPackageProgressStage.ExportingDefinitions,
            LoadedDefinitionCount = totalLoadedDefinitions,
            TotalDefinitionCount = totalLoadedDefinitions,
            ProcessedDefinitionCount = exportedDefinitionCount,
            TotalProcessDefinitionCount = totalExportDefinitions,
            CurrentDefinitionDisplayName = currentDefinitionDisplayName,
            CurrentDefinitionKey = currentDefinitionKey,
            RedactedPathCount = redactedPathCount
        });
    }

    private string HashForDisplay(string? schemaHash)
    {
        if (string.IsNullOrWhiteSpace(schemaHash))
        {
            return localizer["Common:States:Empty"].Value;
        }

        const int PREFIX_LENGTH = 19;
        const int SUFFIX_LENGTH = 8;
        return schemaHash.Length <= PREFIX_LENGTH + SUFFIX_LENGTH + 3
            ? schemaHash
            : $"{schemaHash[..PREFIX_LENGTH]}...{schemaHash[^SUFFIX_LENGTH..]}";
    }

    private async Task<ConfigurationExportDefinition> CreateExportDefinitionAsync(
        ConfigurationDefinition definition,
        bool includeSensitive)
    {
        // Read the complete document before field-level redaction; a masked root cannot reconstruct public siblings.
        var effectiveValue = await LoadEffectiveValueAsync(definition.DefinitionKey, LogicalPath.Root, includeSensitive: true);
        var scalarValues = await LoadScalarEffectiveValuesAsync(definition, definition.Root, effectiveValue);
        var root = ParseJson(effectiveValue.DisplayValue) ?? ConfigurationPendingValueDocumentBuilder.CreateDefaultJsonFor(definition.Root);
        IReadOnlyList<string> redactedPaths = [];
        if (!includeSensitive)
        {
            root = draftService.RedactForExport(root, definition.Root, out redactedPaths);
        }

        return new ConfigurationExportDefinition
        {
            DefinitionKey = definition.DefinitionKey,
            DisplayName = definition.DisplayName,
            FromProject = definition.FromProject,
            Category = definition.Category,
            ClrTypeName = definition.ClrTypeName,
            SchemaVersion = definition.SchemaVersion,
            SchemaHash = definition.SchemaHash,
            ValueVersion = effectiveValue.Version,
            SourceSummary = scalarValues.Values
                .Select(value => value.EffectiveSource?.DisplayName)
                .OfType<string>()
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Value = root,
            RedactedPaths = redactedPaths
        };
    }

    private async Task<ConfigurationDefinition> LoadDefinitionAsync(string definitionKey)
    {
        var result = await facade.GetDefinitionAsync(definitionKey);
        if (result.IsFailed(out var error, out var detail))
        {
            throw new InvalidOperationException(error.Message);
        }

        return detail.Definition;
    }

    /// <summary>
    /// Loads one effective value.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    /// <param name="includeSensitive">Overrides the host disclosure setting for an explicit export request.</param>
    /// <returns>The effective value.</returns>
    public async Task<ConfigurationEffectiveValue> LoadEffectiveValueAsync(string definitionKey, LogicalPath path, bool? includeSensitive = null)
    {
        var result = await facade.GetEffectiveValueAsync(definitionKey, path, includeSensitive ?? visibility.ShowSensitiveValues);
        if (result.IsFailed(out var error, out var effectiveValue))
        {
            throw new InvalidOperationException(error.Message);
        }

        return effectiveValue;
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

}
