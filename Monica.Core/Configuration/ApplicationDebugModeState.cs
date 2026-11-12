namespace Monica.Core.Configuration;

/// <summary>Read-only evidence for the host's frozen diagnostic policy and its current deployment candidate.</summary>
/// <param name="ActualValue">The policy enforced by this running host.</param>
/// <param name="ActualSource">The startup provider identity, Code, or Default.</param>
/// <param name="CandidateValue">The validated value a restart would select, or null when invalid or unavailable.</param>
/// <param name="CandidateSource">The current provider identity, Code, or Default; null when unavailable.</param>
/// <param name="CandidateError">A controlled validation failure, or null for a valid candidate.</param>
public sealed record ApplicationDebugModeState(
    bool ActualValue,
    string ActualSource,
    bool? CandidateValue,
    string? CandidateSource,
    string? CandidateError)
{
    /// <summary>Gets whether deployment inputs differ from the actual policy or require correction before restart.</summary>
    public bool RequiresRestart => CandidateError is not null || CandidateValue != ActualValue;
}
