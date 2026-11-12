namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes the request-filter service available to the current host and its detached path snapshot.
/// </summary>
public sealed class BuildingBlockRequestFilterState
{
    /// <summary>
    /// Gets whether the current host has a registered request-filter service.
    /// This does not establish middleware attachment or authorize path changes.
    /// </summary>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Gets the exact, case-sensitive paths disabled when this snapshot was captured.
    /// An unavailable service supplies an empty list; later updates do not change this snapshot.
    /// </summary>
    public IReadOnlyList<string> DisabledPaths { get; init; } = [];
}
