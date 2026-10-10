using Monica.Configuration.Models;

namespace Monica.Configuration.UI.State;

/// <summary>
/// Presents a safe validation diagnostic without changing the staged mutation state.
/// </summary>
public sealed record ConfigurationValidationIssue
{
    /// <summary>
    /// Gets the target definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the display name of the target definition.
    /// </summary>
    public required string DefinitionDisplayName { get; init; }

    /// <summary>
    /// Gets the target logical path.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>
    /// Gets every member location associated with the diagnostic, including sibling and object-level locations.
    /// </summary>
    public IReadOnlyList<LogicalPath> LogicalPaths { get; init; } = [];

    /// <summary>
    /// Gets whether a display value is available. Object rules and actual-options diagnostics never expose values.
    /// </summary>
    public bool HasDisplayValue { get; init; } = true;

    /// <summary>
    /// Gets the diagnostic kind. Local input errors use the schema category.
    /// </summary>
    public ConfigurationValidationIssueKind Kind { get; init; } = ConfigurationValidationIssueKind.Schema;

    /// <summary>
    /// Gets the target node display name.
    /// </summary>
    public required string NodeDisplayName { get; init; }

    /// <summary>
    /// Gets the display-safe invalid value.
    /// </summary>
    public string? InvalidDisplayValue { get; init; }

    /// <summary>
    /// Gets the projected Microsoft configuration key when this issue comes from runtime validation.
    /// </summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>
    /// Gets the validation error shown to the operator.
    /// </summary>
    public required string ValidationError { get; init; }

    /// <summary>
    /// Gets the source that currently supplies the effective value, when available.
    /// </summary>
    public ConfigurationSourceDescriptor? EffectiveSource { get; init; }

    /// <summary>
    /// Gets whether the effective value is missing.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Gets whether the node contains sensitive data.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Gets the validation rules that explain why the value is constrained.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationRule> ValidationRules { get; init; } = [];
}
