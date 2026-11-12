namespace Monica.Configuration.Models;

/// <summary>Describes current deployment inputs without replacing the validated value used by this process.</summary>
public sealed record ConfigurationStartupCandidate
{
    /// <summary>Gets the candidate value, masked using the same disclosure policy as the running value.</summary>
    public string? DisplayValue { get; init; }

    /// <summary>Gets the current winning provider for a scalar candidate, or null for a composite value.</summary>
    public ConfigurationSourceDescriptor? EffectiveSource { get; init; }

    /// <summary>Gets whether the candidate differs semantically from the frozen running value or is invalid.</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>Gets the complete, display-safe validation report for the candidate definition.</summary>
    public required ConfigurationValidationReport ValidationReport { get; init; }
}
