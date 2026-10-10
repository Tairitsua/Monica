namespace Monica.Configuration.Models;

/// <summary>Identifies the complete value boundary evaluated by a candidate report.</summary>
public enum ConfigurationCandidateValidationTarget
{
    /// <summary>The effective aggregate after production provider precedence and defaults.</summary>
    EffectiveAggregate,
    /// <summary>The complete owner-managed store document independently of runtime overrides.</summary>
    StoredDocument
}

/// <summary>
/// Describes schema validation results for an operator-provided candidate value.
/// </summary>
public sealed record ConfigurationCandidateValidationReport
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
    /// Gets the logical path whose complete candidate value was validated.
    /// </summary>
    public required LogicalPath ScopePath { get; init; }

    /// <summary>Gets the complete value boundary; fragment scope takes precedence for fragment reports.</summary>
    public ConfigurationCandidateValidationTarget Target { get; init; }

    /// <summary>Gets whether the proposed effective aggregate differs from the current effective value, when evaluated.</summary>
    /// <remarks>False explicitly identifies a masked or otherwise ineffective edit. It does not bypass document validation.</remarks>
    public bool? HasEffectiveChange { get; init; }

    /// <summary>
    /// Gets all validation issues found in the candidate value.
    /// </summary>
    public IReadOnlyList<ConfigurationCandidateValidationIssue> Issues { get; init; } = [];

    /// <summary>Gets how much of the applicable contract was evaluated.</summary>
    public ConfigurationValidationCoverage Coverage { get; init; } = ConfigurationValidationCoverage.SchemaOnly;
    /// <summary>Gets whether this report covers a fragment or its complete aggregate.</summary>
    public ConfigurationValidationScope Scope { get; init; } = ConfigurationValidationScope.Fragment;
    /// <summary>Gets the owner revision evaluated by a complete validation.</summary>
    public string? ValidationRevision { get; init; }
    /// <summary>Gets whether portable schema checks passed without operational faults.</summary>
    public bool IsSchemaValid => Coverage != ConfigurationValidationCoverage.Failed
        && Issues.All(issue => issue.Kind != ConfigurationValidationIssueKind.Schema);

    /// <summary>
    /// Gets whether the candidate value satisfies every mutation-time schema constraint.
    /// </summary>
    public bool IsValid => Coverage == ConfigurationValidationCoverage.Complete && Issues.Count == 0;

    /// <summary>
    /// Gets the total number of validation issues.
    /// </summary>
    public int IssueCount => Issues.Count;
}

/// <summary>
/// Describes one mutation-time schema validation issue in an operator-provided candidate value.
/// </summary>
public sealed record ConfigurationCandidateValidationIssue
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
    /// Gets the logical path of the invalid candidate node.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>Gets all associated locations for a multi-member object rule.</summary>
    public IReadOnlyList<LogicalPath> LogicalPaths { get; init; } = [];
    /// <summary>Gets the owner of this problem.</summary>
    public ConfigurationValidationIssueKind Kind { get; init; } = ConfigurationValidationIssueKind.Schema;

    /// <summary>
    /// Gets the display name of the invalid schema node.
    /// </summary>
    public required string NodeDisplayName { get; init; }

    /// <summary>
    /// Gets the human-readable validation problem.
    /// </summary>
    public required string Problem { get; init; }

    /// <summary>
    /// Gets the display-safe candidate value. Sensitive and missing values are represented by metadata instead.
    /// </summary>
    public string? CandidateDisplayValue { get; init; }

    /// <summary>
    /// Gets whether the candidate value is missing.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Gets whether the candidate value was redacted because the schema marks the node sensitive.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Gets the schema validation rules that constrain the candidate value.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationRule> ValidationRules { get; init; } = [];
}
