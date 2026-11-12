namespace Monica.ModelQuery.Annotations;

/// <summary>
/// Configures query metadata for a model type. Unspecified settings inherit the module options.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class QueryModelAttribute : Attribute
{
    internal bool? RequireFieldAnnotationOverride { get; private set; }
    internal bool? OmitNavigationPrefixOverride { get; private set; }

    /// <summary>
    /// Requires <see cref="QueryFieldAttribute"/> on each included property, including navigation properties.
    /// When omitted, the module setting applies. The standalone getter defaults to <see langword="false"/>.
    /// </summary>
    public bool RequireFieldAnnotation
    {
        get => RequireFieldAnnotationOverride ?? false;
        set => RequireFieldAnnotationOverride = value;
    }

    /// <summary>
    /// Display name for the model. When omitted, the CLR type's short name is used.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Omits navigation paths from fallback query names. When omitted, the module setting applies.
    /// The standalone getter defaults to <see langword="false"/>.
    /// </summary>
    public bool OmitNavigationPrefix
    {
        get => OmitNavigationPrefixOverride ?? false;
        set => OmitNavigationPrefixOverride = value;
    }
}
