namespace Monica.BuildingBlocks.Annotations;

/// <summary>
/// Declares human-readable context for a building block.
/// </summary>
/// <remarks>
/// The annotation is intentionally not inherited and does not make an otherwise unsupported type discoverable.
/// Each discovered building block describes its own responsibility. Missing metadata is reported as catalog coverage
/// debt rather than as a discovery failure.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class BuildingBlockMetadataAttribute(string title) : Attribute
{
    /// <summary>
    /// Gets the concise display title used by diagnostics, logs, and management UIs.
    /// </summary>
    public string Title { get; } = title;

    /// <summary>
    /// Gets or sets a project-defined team or member identity responsible for this building block.
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// Gets or sets a short explanation of the building block's business or architectural responsibility.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets searchable architectural or business classification tags.
    /// </summary>
    public string[] Tags { get; set; } = [];
}
