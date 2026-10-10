namespace Monica.Configuration.Models;

/// <summary>
/// Defines a Monica-managed configuration aggregate rooted at an options type.
/// </summary>
public sealed record ConfigurationDefinition
{
    /// <summary>
    /// Gets the stable globally unique definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the Microsoft configuration section used for options binding.
    /// </summary>
    public required string SectionPath { get; init; }

    /// <summary>
    /// Gets the display name shown in management tools.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the developer-facing description shown in management tools and generated documentation.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the owning options type identity.
    /// Local scanned definitions keep the assembly-qualified name so defaults can be constructed.
    /// Published metadata definitions keep a compact display/search identity because remote services cannot instantiate the type.
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
    /// Gets the schema version published by the owner.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Gets the current persisted definition revision. The revision advances for every effective metadata or schema
    /// change, while <see cref="SchemaVersion"/> advances only when the schema hash changes.
    /// </summary>
    public int DefinitionRevision { get; init; }

    /// <summary>
    /// Gets the schema hash used to detect drift across services.
    /// </summary>
    public required string SchemaHash { get; init; }

    /// <summary>
    /// Gets the default reload behavior for the definition.
    /// </summary>
    public ConfigurationReloadBehavior ReloadBehavior { get; init; } = ConfigurationReloadBehavior.Unknown;

    /// <summary>
    /// Gets how the current service obtained <see cref="ReloadBehavior"/> for publication.
    /// </summary>
    /// <remarks>
    /// Definitions created without explicit provenance default to <see cref="ConfigurationReloadBehaviorObservationKind.Unresolved"/>.
    /// Publication treats a concrete behavior on such a definition as a declared value so manually constructed definitions remain unambiguous.
    /// </remarks>
    public ConfigurationReloadBehaviorObservationKind ReloadBehaviorObservationKind { get; init; } =
        ConfigurationReloadBehaviorObservationKind.Unresolved;

    /// <summary>
    /// Gets the root node of the configuration schema tree.
    /// </summary>
    public required ConfigurationNodeDefinition Root { get; init; }

    /// <summary>
    /// Gets the owner's validation capability and executable revision. Missing historical metadata remains unknown.
    /// </summary>
    public ConfigurationValidationContract ValidationContract { get; init; } = new();

    /// <summary>
    /// Gets where this definition was resolved from for the current process.
    /// </summary>
    public ConfigurationDefinitionOrigin Origin { get; init; } = ConfigurationDefinitionOrigin.LocalScan;
}
