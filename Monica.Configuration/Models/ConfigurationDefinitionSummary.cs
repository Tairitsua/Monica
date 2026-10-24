namespace Monica.Configuration.Models;

/// <summary>
/// Lightweight configuration definition summary for list views.
/// </summary>
public sealed record ConfigurationDefinitionSummary
{
    /// <summary>Gets an authority restriction that prevents this process from changing managed values.</summary>
    public string? MutationReadOnlyReason { get; init; }

    /// <summary>Gets whether values belong to deployment inputs or the Monica managed store.</summary>
    public ConfigurationKind Kind { get; init; } = ConfigurationKind.Managed;

    /// <summary>Gets why the definition's values cannot be changed through Monica.</summary>
    public string? ReadOnlyReason => Kind == ConfigurationKind.Startup
        ? "Startup configuration is deployment-owned. Change deployment inputs and restart the process."
        : MutationReadOnlyReason;

    /// <summary>
    /// Gets the definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the binding section path.
    /// </summary>
    public required string SectionPath { get; init; }

    /// <summary>
    /// Gets the display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the developer-facing description shown in management tools and generated documentation.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the compact owning options type identity used for display and search.
    /// </summary>
    public required string ClrTypeName { get; init; }

    /// <summary>
    /// Gets the assembly name of the project that published this definition.
    /// </summary>
    public required string FromProject { get; init; }

    /// <summary>
    /// Gets a developer-defined category for grouping.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets the schema version.
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the current persisted definition revision.
    /// </summary>
    public int DefinitionRevision { get; init; }

    /// <summary>
    /// Gets the schema hash used to detect drift across services.
    /// </summary>
    public required string SchemaHash { get; init; }

    /// <summary>
    /// Gets where this definition was resolved from for the current process.
    /// </summary>
    public ConfigurationDefinitionOrigin Origin { get; init; } = ConfigurationDefinitionOrigin.LocalScan;

    /// <summary>
    /// Gets whether the definition is currently published or retained only for diagnostics and audit.
    /// </summary>
    public ConfigurationDefinitionLifecycleState LifecycleState { get; init; } =
        ConfigurationDefinitionLifecycleState.Active;

    /// <summary>
    /// Gets whether this definition has complete authoritative metadata and can be managed safely.
    /// </summary>
    public ConfigurationDefinitionAvailability Availability { get; init; } = ConfigurationDefinitionAvailability.Available;

    /// <summary>
    /// Gets the persisted metadata problem associated with this definition, when one is known.
    /// </summary>
    public ConfigurationDefinitionMetadataDiagnostic? MetadataDiagnostic { get; init; }

    /// <summary>
    /// Gets whether Configuration UI operations that require a complete schema are allowed.
    /// </summary>
    public bool CanManage => Availability == ConfigurationDefinitionAvailability.Available;

    /// <summary>
    /// Gets whether operations that change configuration values are allowed.
    /// </summary>
    public bool CanMutate => Kind == ConfigurationKind.Managed && CanManage
        && LifecycleState == ConfigurationDefinitionLifecycleState.Active && MutationReadOnlyReason is null;
}
