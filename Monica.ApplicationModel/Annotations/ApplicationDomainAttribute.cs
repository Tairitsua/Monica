namespace Monica.ApplicationModel.Annotations;

/// <summary>Declares the business domain owned by a shared domain assembly.</summary>
/// <param name="domain">The existing product domain identifier, as a nonempty string or named enum value.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApplicationDomainAttribute(object domain) : Attribute
{
    /// <summary>Gets the stable domain key, preserving the product's identifier spelling.</summary>
    public string DomainId { get; } = DomainIdentifier.Normalize(domain);

    /// <summary>Names the same-domain role that owns this core's autonomous resources, or null for a passive-only core.</summary>
    /// <remarks>This does not restrict passive services or business operations. The named role must exist in the compiled inventory.</remarks>
    public string? BackgroundRole { get; set; }

    /// <summary>Names this assembly's optional concrete application registration with a public parameterless constructor.</summary>
    public Type? Registration { get; set; }
}

internal static class DomainIdentifier
{
    public static string Normalize(object value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) && text == text.Trim() => text,
        Enum identifier when Enum.GetName(identifier.GetType(), identifier) is { } name => name,
        _ => throw new ArgumentException("A domain identifier must be a nonempty string or a named enum value.", nameof(value))
    };
}
