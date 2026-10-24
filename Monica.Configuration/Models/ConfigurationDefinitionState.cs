namespace Monica.Configuration.Models;

/// <summary>
/// Provides one configuration definition and its effective values, with sensitive values masked by default.
/// </summary>
public sealed record ConfigurationDefinitionState
{
    /// <summary>Gets aggregate deployment candidate validation and restart state for a Startup definition.</summary>
    public ConfigurationStartupCandidate? StartupCandidate { get; init; }

    /// <summary>
    /// Gets the complete definition that describes the returned values.
    /// </summary>
    public required ConfigurationDefinition Definition { get; init; }

    /// <summary>
    /// Gets the persisted effective-value document version observed while building this state, or null when no
    /// persisted document exists.
    /// </summary>
    public long? EffectiveValueVersion { get; init; }

    /// <summary>
    /// Gets effective values for every statically declared non-root schema node in preorder.
    /// Aggregate values containing sensitive data are masked unless the read explicitly requests disclosure.
    /// </summary>
    public IReadOnlyList<ConfigurationEffectiveValue> EffectiveValues { get; init; } = [];
}
