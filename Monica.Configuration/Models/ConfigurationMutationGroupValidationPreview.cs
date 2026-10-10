namespace Monica.Configuration.Models;

/// <summary>
/// Describes complete effective aggregates produced at the submitted persistence targets.
/// </summary>
public sealed record ConfigurationMutationGroupValidationPreview
{
    /// <summary>Gets the per-definition complete validation results.</summary>
    public IReadOnlyList<ConfigurationCandidateValidationReport> Definitions { get; init; } = [];

    /// <summary>Gets the opaque fingerprint of the commands, code contracts, and all contributing baseline values.</summary>
    /// <remarks>
    /// The token is scoped to the validating host process and is not portable across replicas or restarts.
    /// Reuse it with the same commands on that owner process; a changed owner requires a fresh preview.
    /// </remarks>
    public required string ValidationFingerprint { get; init; }

    /// <summary>Gets safe planning or provider-adoption findings that prevent persistence.</summary>
    public IReadOnlyList<ConfigurationMutationValidationProblem> Problems { get; init; } = [];

    /// <summary>Gets whether every effective aggregate and independently observable adoption state is valid.</summary>
    public bool CanApply => Definitions.Count > 0 && Problems.Count == 0 && Definitions.All(static report => report.IsValid);
}

/// <summary>Describes a safe complete-group planning finding without candidate values or exception details.</summary>
public sealed record ConfigurationMutationValidationProblem
{
    /// <summary>Gets a stable machine-readable finding code.</summary>
    public required string Code { get; init; }
    /// <summary>Gets the safe operator-facing explanation.</summary>
    public required string Message { get; init; }
    /// <summary>Gets the affected definition, when the finding is definition-specific.</summary>
    public string? DefinitionKey { get; init; }
}
