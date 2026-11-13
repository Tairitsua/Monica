namespace Monica.Framework.ChangeTracking.Annotations;

/// <summary>
/// Marking entity classes requires generating the corresponding ChangeItemData class and Apply method
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateChangeDataAttribute : Attribute
{
    /// <summary>
    /// The namespace of the generated ChangeItemData class. If not specified, the namespace of the entity class is used.
    /// </summary>
    public string? Namespace { get; set; }
    
    /// <summary>
    /// Generated ChangeItemData class name, if not specified {EntityName}ChangeItemData is used
    /// </summary>
    public string? ClassName { get; set; }
    
    /// <summary>
    /// Whether to include debugging information comments
    /// </summary>
    public bool IncludeDebugInfo { get; set; } = false;
}