namespace Monica.Framework.ChangeTracking.Annotations;

/// <summary>
/// Used to configure the property behavior when ChangeItemData is generated
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ChangeEntryPropertyAttribute : Attribute
{
    /// <summary>
    /// Whether to ignore this attribute and not generate it in ChangeItemData
    /// </summary>
    public bool Ignore { get; set; } = false;
    
    /// <summary>
    /// The title of the attribute, used for subsequent generation of the Format method
    /// </summary>
    public string? Title { get; set; }
}