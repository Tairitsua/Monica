namespace Monica.Configuration.Models;

/// <summary>
/// Describes the current effective value at one logical path.
/// </summary>
public sealed record ConfigurationEffectiveValue
{
    /// <summary>Gets deployment candidate diagnostics for Startup values; the ordinary value remains frozen actual state.</summary>
    public ConfigurationStartupCandidate? StartupCandidate { get; init; }

    /// <summary>
    /// Gets the definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the logical path.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>
    /// Gets the projected Microsoft configuration path.
    /// </summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>
    /// Gets the effective display value. Sensitive values are omitted unless the read explicitly requests disclosure.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets whether the value is sensitive.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Gets the effective document version.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// Gets the runtime provider that currently supplies this scalar value.
    /// </summary>
    /// <remarks>
    /// This is null for object, dictionary, list, and root snapshots because those values can be composed from
    /// multiple providers.
    /// </remarks>
    public ConfigurationSourceDescriptor? EffectiveSource { get; init; }
}
