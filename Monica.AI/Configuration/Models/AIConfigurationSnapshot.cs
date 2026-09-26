namespace Monica.AI.Configuration.Models;

/// <summary>A credential-free settings view suitable for an authorized operator UI.</summary>
public sealed record AIProviderSettings
{
    /// <summary>Effective provider settings after persisted overrides replace code defaults.</summary>
    public required AIProviderConfiguration Configuration { get; init; }
    /// <summary>Whether a non-empty credential is available; its value is never exposed.</summary>
    public bool HasApiKey { get; init; }
    /// <summary>Whether this provider has a code-defined baseline.</summary>
    public bool IsCodeDefined { get; init; }
    /// <summary>Whether persisted settings currently override or define this provider.</summary>
    public bool HasOverride { get; init; }
    /// <summary>Startup validation findings that currently keep this provider disabled; empty when the configuration is valid.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}

/// <summary>
/// Revisioned host settings. Every write must supply <see cref="Revision"/> to reject stale concurrent edits.
/// </summary>
public sealed record AIConfigurationSnapshot
{
    /// <summary>Monotonically increasing persisted settings revision; zero means no runtime changes.</summary>
    public long Revision { get; init; }
    /// <summary>Credential-free effective provider configurations.</summary>
    public IReadOnlyList<AIProviderSettings> Providers { get; init; } = [];
}
