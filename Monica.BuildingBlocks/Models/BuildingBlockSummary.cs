using System.Text.Json.Serialization;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Identifies another building block without exposing its runtime reflection model.
/// </summary>
public sealed class BuildingBlockReference
{
    /// <summary>
    /// Gets the represented CLR type's full name.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the normalized display title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the architectural building-block kind.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BuildingBlockKind Kind { get; init; }
}

/// <summary>
/// Provides the serializable catalog projection used by APIs and management UIs.
/// </summary>
public sealed class BuildingBlockSummary
{
    /// <summary>
    /// Gets the represented CLR type's full name.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the normalized metadata title or the CLR type name fallback.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the effective description from metadata or XML documentation.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the normalized owner declared in metadata.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    /// Gets normalized searchable metadata tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Gets the architectural building-block kind.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BuildingBlockKind Kind { get; init; }

    /// <summary>
    /// Gets the stable execution points supported by Monica adapters for this building-block kind. Presence indicates a
    /// supported boundary and does not guarantee that every instance emits it.
    /// </summary>
    public IReadOnlyList<string> ExecutionPoints { get; init; } = [];

    /// <summary>
    /// Gets whether the represented type declares building-block metadata explicitly.
    /// </summary>
    public bool HasExplicitMetadata { get; init; }

    /// <summary>
    /// Gets the number of normalized requirement references.
    /// </summary>
    public int RequirementCount { get; init; }

    /// <summary>
    /// Gets the number of public methods recorded for this building block.
    /// </summary>
    public int MethodCount { get; init; }

    /// <summary>
    /// Gets the building blocks on which this building block depends.
    /// </summary>
    public IReadOnlyList<BuildingBlockReference> Dependencies { get; init; } = [];

    /// <summary>
    /// Gets the number of building blocks that depend on this building block.
    /// </summary>
    public int DependedByCount { get; init; }

    /// <summary>
    /// Gets catalog warnings and architecture alerts for this building block.
    /// </summary>
    public IReadOnlyList<BuildingBlockAlert> Alerts { get; init; } = [];
}
