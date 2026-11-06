namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a discovered domain event and a representative serializable payload structure.
/// </summary>
public sealed class BuildingBlockDomainEventInfo
{
    /// <summary>
    /// Gets the discovered event's full CLR type identity for exact publication.
    /// Display titles and short type names are not used to derive this key.
    /// </summary>
    public string EventKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the domain-event building-block summary.
    /// </summary>
    public required BuildingBlockSummary Info { get; init; }

    /// <summary>
    /// Gets the representative payload structure.
    /// </summary>
    public object? Structure { get; init; }
}
