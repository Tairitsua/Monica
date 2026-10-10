using Microsoft.Extensions.Options;

namespace Monica.Configuration.Models;

/// <summary>
/// Describes the latest observed default-options creation attempt. It never retains the options object or source JSON.
/// </summary>
public sealed record ConfigurationOptionsValidationReport
{
    /// <summary>Gets the owning definition key.</summary>
    public required string DefinitionKey { get; init; }
    /// <summary>Gets the options name. Managed binding observes only the default name.</summary>
    public string OptionsName { get; init; } = Options.DefaultName;
    /// <summary>Gets the unique identity of this observed creation attempt.</summary>
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    /// <summary>Gets when validation of this creation attempt was observed.</summary>
    public DateTimeOffset AttemptedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the executed local validation revision.</summary>
    public string? ValidationRevision { get; init; }
    /// <summary>Gets how much of the contract was evaluated.</summary>
    public ConfigurationValidationCoverage Coverage { get; init; }
    /// <summary>Gets the actual-instance boundary.</summary>
    public ConfigurationValidationScope Scope { get; init; } = ConfigurationValidationScope.ActualOptions;
    /// <summary>Gets immutable, display-safe issues without claims about source values.</summary>
    public IReadOnlyList<ConfigurationRuntimeValidationIssue> Issues { get; init; } = [];
    /// <summary>Gets whether portable schema checks passed without operational faults.</summary>
    public bool IsSchemaValid => Coverage != ConfigurationValidationCoverage.Failed
        && Issues.All(issue => issue.Kind != ConfigurationValidationIssueKind.Schema);
    /// <summary>Gets whether the complete actual-instance contract passed.</summary>
    public bool IsValid => Coverage == ConfigurationValidationCoverage.Complete && Issues.Count == 0;
    /// <summary>Gets the number of reported issues.</summary>
    public int IssueCount => Issues.Count;
}
