namespace Monica.Configuration.Models;

/// <summary>
/// Describes the current runtime validation state of Monica-managed configuration definitions.
/// </summary>
public sealed record ConfigurationValidationReport
{
    /// <summary>
    /// Gets when this report was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets all runtime validation issues discovered in the current process.
    /// </summary>
    public IReadOnlyList<ConfigurationRuntimeValidationIssue> Issues { get; init; } = [];

    /// <summary>Gets how much of the applicable contracts was evaluated.</summary>
    public ConfigurationValidationCoverage Coverage { get; init; } = ConfigurationValidationCoverage.SchemaOnly;
    /// <summary>Gets the complete source-snapshot boundary.</summary>
    public ConfigurationValidationScope Scope { get; init; } = ConfigurationValidationScope.CompleteAggregate;
    /// <summary>Gets the executable revision for a single-owner report; aggregate reports use DefinitionReports.</summary>
    public string? ValidationRevision { get; init; }
    /// <summary>Gets coverage and executable revision for every evaluated owner.</summary>
    public IReadOnlyList<ConfigurationValidationDefinitionReport> DefinitionReports { get; init; } = [];
    /// <summary>Gets whether portable schema checks passed without operational faults.</summary>
    public bool IsSchemaValid => Coverage != ConfigurationValidationCoverage.Failed
        && Issues.All(issue => issue.Kind != ConfigurationValidationIssueKind.Schema);

    /// <summary>
    /// Gets whether every local configuration definition is valid.
    /// </summary>
    public bool IsValid => Coverage == ConfigurationValidationCoverage.Complete && Issues.Count == 0
        && DefinitionReports.All(report => report.IsValid);

    /// <summary>
    /// Gets the total number of runtime validation issues.
    /// </summary>
    public int IssueCount => Issues.Count;
}

/// <summary>
/// Describes one source-aware runtime validation issue for a managed configuration value.
/// </summary>
public sealed record ConfigurationRuntimeValidationIssue
{
    /// <summary>
    /// Gets the owning configuration definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the display name of the owning configuration definition.
    /// </summary>
    public required string DefinitionDisplayName { get; init; }

    /// <summary>
    /// Gets the optional category of the owning configuration definition.
    /// </summary>
    public string? DefinitionCategory { get; init; }

    /// <summary>
    /// Gets the target logical path inside the definition.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>Gets all associated locations for a multi-member object rule.</summary>
    public IReadOnlyList<LogicalPath> LogicalPaths { get; init; } = [];
    /// <summary>Gets the owner of this problem.</summary>
    public ConfigurationValidationIssueKind Kind { get; init; } = ConfigurationValidationIssueKind.Schema;

    /// <summary>
    /// Gets the target node display name.
    /// </summary>
    public required string NodeDisplayName { get; init; }

    /// <summary>
    /// Gets the projected Microsoft configuration key.
    /// </summary>
    public required string ConfigurationPath { get; init; }

    /// <summary>
    /// Gets the human-readable validation problem.
    /// </summary>
    public required string Problem { get; init; }

    /// <summary>
    /// Gets the display-safe effective value. Sensitive and missing values are represented by metadata instead.
    /// </summary>
    public string? EffectiveDisplayValue { get; init; }

    /// <summary>
    /// Gets whether the effective value is missing.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Gets whether the effective value was redacted because the schema marks the node sensitive.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Gets the source that currently supplies the effective value, when one exists.
    /// </summary>
    public ConfigurationSourceDescriptor? EffectiveSource { get; init; }

    /// <summary>
    /// Gets the source chain for the target configuration key.
    /// </summary>
    public required ConfigurationSourceChain SourceChain { get; init; }

    /// <summary>
    /// Gets the schema validation rules that constrain this value.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationRule> ValidationRules { get; init; } = [];
}
