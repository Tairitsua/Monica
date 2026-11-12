using Monica.BuildingBlocks.Models;

namespace Monica.BuildingBlocks.Abstractions;

/// <summary>
/// Resolves project-owned requirement identifiers into optional navigation targets.
/// </summary>
/// <remarks>
/// Resolution is performed only when a building-block detail view is loaded. Implementations should avoid throwing for
/// unknown identifiers and return <see langword="null"/> instead. Resolver failures are isolated per requirement so
/// that catalog inspection remains available.
/// </remarks>
public interface IBuildingBlockRequirementLinkResolver
{
    /// <summary>
    /// Resolves one requirement identifier into a display label and navigation target.
    /// </summary>
    /// <param name="requirementId">The normalized requirement identifier.</param>
    /// <param name="cancellationToken">Signals that the detail request has been cancelled.</param>
    /// <returns>A resolved link, or <see langword="null"/> when the identifier cannot be resolved.</returns>
    ValueTask<BuildingBlockRequirementLink?> ResolveAsync(
        string requirementId,
        CancellationToken cancellationToken = default);
}
