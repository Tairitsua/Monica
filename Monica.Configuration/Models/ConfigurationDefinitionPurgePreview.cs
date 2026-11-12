namespace Monica.Configuration.Models;

/// <summary>
/// Describes the destructive and retained records associated with purging one retired definition.
/// </summary>
public sealed record ConfigurationDefinitionPurgePreview
{
    /// <summary>Gets the process-policy restriction that makes definition maintenance read-only.</summary>
    public string? ReadOnlyReason { get; init; }
    /// <summary>
    /// Gets the stable definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the operator-facing definition name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the definition lifecycle derived from current publisher state.
    /// </summary>
    public ConfigurationDefinitionLifecycleState LifecycleState { get; init; }

    /// <summary>
    /// Gets the current definition revision used for purge concurrency control.
    /// </summary>
    public int DefinitionRevision { get; init; }

    /// <summary>
    /// Gets the logical publishers that currently report the definition.
    /// </summary>
    public IReadOnlyList<string> ActivePublisherKeys { get; init; } = [];

    /// <summary>
    /// Gets whether the definition is registered by the current process.
    /// </summary>
    /// <remarks>
    /// An Automatic local definition is active before publication. Manual local registration does not imply
    /// a persisted active publisher and cannot authorize maintenance.
    /// </remarks>
    public bool IsLocallyRegistered { get; init; }

    /// <summary>
    /// Gets whether the purge will remove a current Monica effective-value document.
    /// </summary>
    public bool HasEffectiveValue { get; init; }

    /// <summary>
    /// Gets the current effective-value version when a document exists.
    /// </summary>
    public long? EffectiveValueVersion { get; init; }

    /// <summary>
    /// Gets the number of definition-publication history records that the purge will remove.
    /// </summary>
    public int PublicationHistoryCount { get; init; }

    /// <summary>
    /// Gets the number of immutable value-history records that the purge will retain for audit.
    /// </summary>
    public int RetainedValueHistoryCount { get; init; }

    /// <summary>
    /// Gets the number of immutable mutation groups that reference retained value history.
    /// </summary>
    public int RetainedMutationGroupCount { get; init; }

    /// <summary>
    /// Gets the number of immutable unified-version snapshots that reference the definition.
    /// </summary>
    public int RetainedUnifiedVersionCount { get; init; }

    /// <summary>
    /// Gets whether the definition currently satisfies the lifecycle prerequisites for purging.
    /// </summary>
    public bool CanPurge =>
        ReadOnlyReason is null
        && LifecycleState == ConfigurationDefinitionLifecycleState.Retired
        && !IsLocallyRegistered
        && ActivePublisherKeys.Count == 0;
}
