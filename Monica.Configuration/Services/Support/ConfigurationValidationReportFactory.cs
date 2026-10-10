using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Projects validation results without attaching values to object-code or actual-instance failures.</summary>
internal static class ConfigurationValidationReportFactory
{
    internal static ConfigurationValidationReport Create(ConfigurationDefinition definition,
        ConfigurationCompleteValidationResult result, IConfigurationSourceInspector? sourceInspector = null)
    {
        return new ConfigurationValidationReport
        {
            Coverage = result.Coverage,
            Scope = result.Scope,
            ValidationRevision = result.ValidationRevision,
            DefinitionReports = Array.AsReadOnly<ConfigurationValidationDefinitionReport>([new()
            {
                DefinitionKey = definition.DefinitionKey,
                Coverage = result.Coverage,
                Scope = result.Scope,
                ValidationRevision = result.ValidationRevision,
                IsSchemaValid = result.IsSchemaValid,
                IsValid = result.IsValid
            }]),
            Issues = Array.AsReadOnly(result.Issues.Select(issue => ToRuntimeIssue(definition, issue, sourceInspector,
                result.Scope == ConfigurationValidationScope.ActualOptions)).ToArray())
        };
    }

    internal static ConfigurationRuntimeValidationIssue ToRuntimeIssue(ConfigurationDefinition definition,
        ConfigurationValueValidationIssue issue, IConfigurationSourceInspector? inspector = null, bool actualOptions = false)
    {
        var sensitive = issue.IsSensitive || ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, issue.LogicalPath);
        var scalarSource = !actualOptions && issue.Kind == ConfigurationValidationIssueKind.Schema
            && issue.Node.NodeKind == ConfigurationNodeKind.Scalar && !issue.BlocksProjection && inspector is not null;
        var chain = scalarSource ? inspector!.GetSourceChain(definition, issue.LogicalPath) : new ConfigurationSourceChain
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = issue.LogicalPath,
            ConfigurationPath = issue.Node.ConfigurationPath ?? definition.SectionPath
        };
        IReadOnlyList<LogicalPath> locations = issue.LogicalPaths.Count == 0 ? [issue.LogicalPath] : issue.LogicalPaths;
        return new ConfigurationRuntimeValidationIssue
        {
            DefinitionKey = definition.DefinitionKey,
            DefinitionDisplayName = definition.DisplayName,
            DefinitionCategory = definition.Category,
            LogicalPath = SnapshotPath(issue.LogicalPath),
            LogicalPaths = Array.AsReadOnly(locations.Select(SnapshotPath).ToArray()),
            Kind = issue.Kind,
            NodeDisplayName = issue.Node.RelativePath.Depth == 0 ? definition.DisplayName : issue.Node.DisplayName ?? issue.Node.Name,
            ConfigurationPath = chain.ConfigurationPath,
            Problem = sensitive ? "A configuration constraint failed for a sensitive value." : issue.Message,
            EffectiveDisplayValue = scalarSource && !sensitive ? issue.DisplayValue : null,
            IsMissing = issue.IsMissing,
            IsSensitive = sensitive,
            EffectiveSource = scalarSource ? chain.Values.FirstOrDefault(value => value.IsEffective)?.Source : null,
            SourceChain = chain with
            {
                LogicalPath = SnapshotPath(chain.LogicalPath),
                Values = Array.AsReadOnly(chain.Values.ToArray())
            },
            ValidationRules = Array.AsReadOnly(issue.ValidationRules.Select(rule => rule is AllowedValuesRule allowed
                ? allowed with { Values = Array.AsReadOnly(allowed.Values.ToArray()) }
                : rule).ToArray())
        };
    }

    private static LogicalPath SnapshotPath(LogicalPath path) => new(Array.AsReadOnly(path.Segments.ToArray()));
}
