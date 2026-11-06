namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a requirement-link target returned by an application resolver.
/// </summary>
/// <param name="Title">Optional human-readable title for the requirement.</param>
/// <param name="Href">Absolute or application-relative navigation target.</param>
public sealed record BuildingBlockRequirementLink(string? Title, string Href);

/// <summary>
/// Represents one normalized requirement associated with a building block.
/// </summary>
public sealed class BuildingBlockRequirementReference
{
    /// <summary>
    /// Gets the stable requirement identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the display title supplied by the resolver, or the identifier when no title was supplied.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the safe navigation target, or <see langword="null"/> when the requirement is unresolved.
    /// </summary>
    public string? Href { get; init; }

    /// <summary>
    /// Gets whether this requirement has a navigation target.
    /// </summary>
    public bool IsResolved => Href is not null;
}
