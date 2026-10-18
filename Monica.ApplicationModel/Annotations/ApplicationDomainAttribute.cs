namespace Monica.ApplicationModel.Annotations;

/// <summary>Declares the business domain owned by a shared domain assembly.</summary>
/// <param name="domain">The existing product domain identifier, as a nonempty string or named enum value.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApplicationDomainAttribute(object domain) : Attribute
{
    /// <summary>Gets the stable domain key, preserving the product's identifier spelling.</summary>
    public string DomainId { get; } = DomainIdentifier.Normalize(domain);
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
