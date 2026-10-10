using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Identifies the caller's structural validation boundary.</summary>
internal enum ConfigurationValidationProfile { Runtime, Mutation, CapturedValue, Seed }

/// <summary>Contains immutable, safe validation results before source/display projection.</summary>
internal sealed record ConfigurationCompleteValidationResult
{
    internal ConfigurationValidationCoverage Coverage { get; init; }
    internal ConfigurationValidationScope Scope { get; init; } = ConfigurationValidationScope.CompleteAggregate;
    internal string? ValidationRevision { get; init; }
    internal IReadOnlyList<ConfigurationValueValidationIssue> Issues { get; init; } = [];
    internal bool IsSchemaValid => Coverage != ConfigurationValidationCoverage.Failed
        && Issues.All(issue => issue.Kind != ConfigurationValidationIssueKind.Schema);
    internal bool IsValid => Coverage == ConfigurationValidationCoverage.Complete && Issues.Count == 0;
}
