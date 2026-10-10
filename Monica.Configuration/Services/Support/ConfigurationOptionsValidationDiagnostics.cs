using System.Collections.Concurrent;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Owns immutable diagnostics for observed actual default-options attempts.</summary>
internal sealed class ConfigurationOptionsValidationDiagnostics(IConfigurationDefinitionRegistry definitions)
    : IConfigurationOptionsValidationDiagnostics
{
    private readonly ConcurrentDictionary<string, ConfigurationOptionsValidationReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationOptionsValidationReport? GetLatestReport(string definitionKey) => _reports.GetValueOrDefault(definitionKey);

    public IReadOnlyList<ConfigurationOptionsValidationReport> GetReports() => Array.AsReadOnly(
        _reports.Values.OrderBy(report => report.DefinitionKey, StringComparer.OrdinalIgnoreCase).ToArray());

    internal ConfigurationOptionsValidationReport Record(ConfigurationDefinition definition, ConfigurationCompleteValidationResult result)
    {
        var report = new ConfigurationOptionsValidationReport
        {
            DefinitionKey = definition.DefinitionKey,
            ValidationRevision = result.ValidationRevision,
            Coverage = result.Coverage,
            Issues = Array.AsReadOnly(result.Issues.Select(issue => ConfigurationValidationReportFactory.ToRuntimeIssue(
                definition, issue, actualOptions: true)).ToArray())
        };
        _reports[definition.DefinitionKey] = report;
        return report;
    }

    internal void RecordFault(string definitionKey, ConfigurationValidationExecutionException fault)
    {
        var definition = definitions.GetRequired(definitionKey);
        Record(definition, new ConfigurationCompleteValidationResult
        {
            Coverage = ConfigurationValidationCoverage.Failed,
            Scope = ConfigurationValidationScope.ActualOptions,
            ValidationRevision = definition.ValidationContract.Revision,
            Issues = [new ConfigurationValueValidationIssue
            {
                Kind = fault.Kind,
                LogicalPath = fault.LogicalPath,
                Node = ConfigurationSchemaNavigator.ResolveNode(definition.Root, fault.LogicalPath) ?? definition.Root,
                Message = fault.Message
            }]
        });
    }
}
