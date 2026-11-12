namespace Monica.ModelQuery.Annotations;

/// <summary>
/// Includes or configures a model property for queries. Annotation overrides automatic omission of
/// properties marked with <c>JsonIgnoreAttribute</c> or <c>NotMappedAttribute</c> unless excluded explicitly.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class QueryFieldAttribute : Attribute
{
    internal bool? OmitNavigationPrefixOverride { get; private set; }

    /// <summary>
    /// The complete set of names accepted for the field, compared without regard to case. A nonempty set
    /// replaces the fallback property or navigation-path name; an empty or omitted set uses that fallback.
    /// Names must be nonblank and unique across the model, and cannot contain whitespace or parentheses.
    /// </summary>
    public string[]? QueryNames { get; set; }

    /// <summary>
    /// Display name of the field. Defaults to the reflected property name.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Excludes this field from searches that do not explicitly name their fields.
    /// Explicit field lists may still select it if its type supports searching.
    /// </summary>
    public bool ExcludeFromDefaultSearch { get; set; }

    /// <summary>
    /// Excludes the property from query metadata, even when it has this annotation.
    /// </summary>
    public bool Exclude { get; set; }

    /// <summary>
    /// Omits the navigation path from the fallback query name. When omitted, the model setting or module
    /// setting applies. The standalone getter defaults to <see langword="false"/>.
    /// </summary>
    public bool OmitNavigationPrefix
    {
        get => OmitNavigationPrefixOverride ?? false;
        set => OmitNavigationPrefixOverride = value;
    }
}
