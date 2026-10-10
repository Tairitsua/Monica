using Monica.Configuration.Models;
using Monica.Configuration.UI.State;
using MudBlazor;

namespace Monica.Configuration.UI.Support;

/// <summary>
/// Maps safe infrastructure diagnostics to presentation without modifying pending changes or resolving options.
/// </summary>
public static class ConfigurationValidationPresentation
{
    /// <summary>Gets the status color from validation coverage and complete validity.</summary>
    public static Color StatusColor(ConfigurationValidationCoverage coverage, bool isValid) => coverage switch
    {
        ConfigurationValidationCoverage.Failed => Color.Error,
        ConfigurationValidationCoverage.SchemaOnly => Color.Warning,
        _ => isValid ? Color.Success : Color.Error
    };

    /// <summary>Maps one candidate diagnostic, retaining all associated member locations.</summary>
    public static ConfigurationValidationIssue FromCandidate(ConfigurationCandidateValidationIssue issue) => new()
    {
        DefinitionKey = issue.DefinitionKey,
        DefinitionDisplayName = issue.DefinitionDisplayName,
        LogicalPath = issue.LogicalPath,
        LogicalPaths = issue.LogicalPaths,
        NodeDisplayName = issue.NodeDisplayName,
        Kind = issue.Kind,
        HasDisplayValue = issue.Kind == ConfigurationValidationIssueKind.Schema,
        InvalidDisplayValue = issue.Kind == ConfigurationValidationIssueKind.Schema ? issue.CandidateDisplayValue : null,
        ValidationError = issue.Problem,
        IsMissing = issue.IsMissing,
        IsSensitive = issue.IsSensitive,
        ValidationRules = issue.ValidationRules
    };

    /// <summary>Maps one source or actual-options diagnostic without fabricating provider provenance.</summary>
    public static ConfigurationValidationIssue FromRuntime(ConfigurationRuntimeValidationIssue issue, bool actualOptions = false) => new()
    {
        DefinitionKey = issue.DefinitionKey,
        DefinitionDisplayName = issue.DefinitionDisplayName,
        LogicalPath = issue.LogicalPath,
        LogicalPaths = issue.LogicalPaths,
        NodeDisplayName = issue.NodeDisplayName,
        Kind = issue.Kind,
        HasDisplayValue = !actualOptions && issue.Kind == ConfigurationValidationIssueKind.Schema,
        InvalidDisplayValue = !actualOptions && issue.Kind == ConfigurationValidationIssueKind.Schema ? issue.EffectiveDisplayValue : null,
        ConfigurationPath = actualOptions ? null : issue.ConfigurationPath,
        EffectiveSource = actualOptions ? null : issue.EffectiveSource,
        ValidationError = issue.Problem,
        IsMissing = issue.IsMissing,
        IsSensitive = issue.IsSensitive,
        ValidationRules = issue.ValidationRules
    };
}
