namespace Monica.BuildingBlocks.Annotations;

/// <summary>
/// Associates a building block with one externally managed requirement identifier.
/// </summary>
/// <remarks>
/// The identifier format belongs to the consuming project. Monica trims and deduplicates identifiers but does not
/// impose a project-specific naming convention.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class BuildingBlockRequirementAttribute(string requirementId) : Attribute
{
    /// <summary>
    /// Gets the requirement identifier supplied by the application.
    /// </summary>
    public string RequirementId { get; } = requirementId;
}
