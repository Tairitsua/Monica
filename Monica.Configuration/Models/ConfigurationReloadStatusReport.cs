namespace Monica.Configuration.Models;

/// <summary>
/// Describes whether the active runtime configuration projection matches the effective value store.
/// </summary>
public sealed record ConfigurationReloadStatusReport
{
    /// <summary>
    /// Gets when this report was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the effective value store used for local Managed comparisons, or null when no local Managed owner exists.
    /// </summary>
    public ConfigurationStoreDescriptor? EffectiveValueStore { get; init; }

    /// <summary>
    /// Gets runtime reload counters and timestamps for Monica's projection provider.
    /// </summary>
    public required ConfigurationRuntimeReloadState Runtime { get; init; }

    /// <summary>
    /// Gets per-definition runtime/store version comparisons.
    /// </summary>
    public IReadOnlyList<ConfigurationDefinitionReloadStatus> Definitions { get; init; } = [];

    /// <summary>
    /// Gets the number of definitions whose runtime loaded version does not match the effective store version.
    /// </summary>
    public int DriftCount => Definitions.Count(static definition => definition.HasVersionDrift);

    /// <summary>
    /// Gets whether every local definition is loaded at the current effective store version.
    /// </summary>
    public bool IsCurrent => Runtime.IsProviderActive && DriftCount == 0;
}

/// <summary>
/// Describes reload counters and timestamps for Monica's runtime projection provider.
/// </summary>
public sealed record ConfigurationRuntimeReloadState
{
    /// <summary>
    /// Gets whether Monica's runtime configuration provider is connected to the current application.
    /// </summary>
    public bool IsProviderActive { get; init; }

    /// <summary>
    /// Gets how many Monica projection reloads have completed successfully in this process.
    /// </summary>
    public long ReloadCount { get; init; }

    /// <summary>
    /// Gets when the Monica projection was last reloaded successfully.
    /// </summary>
    public DateTimeOffset? LastReloadedAt { get; init; }

    /// <summary>
    /// Gets how long the most recent successful Monica projection reload took.
    /// </summary>
    public TimeSpan? LastReloadDuration { get; init; }

    /// <summary>
    /// Gets how many Monica projection reloads failed in this process.
    /// </summary>
    public long FailedReloadCount { get; init; }

    /// <summary>
    /// Gets when the most recent Monica projection reload failure happened.
    /// </summary>
    public DateTimeOffset? LastFailedAt { get; init; }

    /// <summary>
    /// Gets the most recent reload failure message, when one exists.
    /// </summary>
    public string? LastFailureMessage { get; init; }
}

/// <summary>
/// Describes one configuration definition's runtime loaded version and effective store version.
/// </summary>
public sealed record ConfigurationDefinitionReloadStatus
{
    /// <summary>
    /// Gets the owning configuration definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the display name of the configuration definition.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the optional category of the configuration definition.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets the version currently loaded by the runtime Monica configuration provider.
    /// </summary>
    public long? RuntimeLoadedVersion { get; init; }

    /// <summary>
    /// Gets the current effective store document version.
    /// </summary>
    public long? EffectiveStoreVersion { get; init; }

    /// <summary>
    /// Gets when the effective store document was last modified.
    /// </summary>
    public DateTimeOffset? EffectiveLastModifiedAt { get; init; }

    /// <summary>
    /// Gets the version comparison status.
    /// </summary>
    public ConfigurationReloadVersionStatus Status { get; init; } = ConfigurationReloadVersionStatus.Unknown;

    /// <summary>
    /// Gets whether the runtime loaded version differs from the effective store version.
    /// </summary>
    public bool HasVersionDrift => Status is not ConfigurationReloadVersionStatus.Current;
}

/// <summary>
/// Describes how a runtime loaded configuration version compares with the effective store version.
/// </summary>
public enum ConfigurationReloadVersionStatus
{
    /// <summary>
    /// The status cannot be determined.
    /// </summary>
    Unknown,

    /// <summary>
    /// The runtime provider has not loaded this definition.
    /// </summary>
    NotLoaded,

    /// <summary>
    /// The effective store document is missing.
    /// </summary>
    MissingEffectiveValue,

    /// <summary>
    /// The runtime loaded version matches the effective store version.
    /// </summary>
    Current,

    /// <summary>
    /// The runtime loaded version is older than the effective store version.
    /// </summary>
    Stale,

    /// <summary>
    /// The runtime loaded version is newer than the effective store version.
    /// </summary>
    Ahead
}
