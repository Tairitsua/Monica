namespace Monica.Configuration.Models;

/// <summary>
/// Defines dictionary key and value-template metadata.
/// </summary>
public sealed record ConfigurationDictionaryTemplate
{
    /// <summary>
    /// Gets the CLR type name of dictionary keys.
    /// </summary>
    public required string KeyClrTypeName { get; init; }

    /// <summary>
    /// Gets the scalar kind used by dictionary keys.
    /// </summary>
    public ConfigurationValueKind KeyKind { get; init; }

    /// <summary>Gets portable named enum-key values without loading the owner's enum type remotely.</summary>
    public IReadOnlyList<ConfigurationEnumValue> KeyEnumValues { get; init; } = [];

    /// <summary>Gets the built-in integral enum storage identity for exact numeric dictionary-key conversion.</summary>
    public string? KeyEnumUnderlyingClrTypeName { get; init; }

    /// <summary>
    /// Gets the optional key validation regular expression.
    /// </summary>
    public string? KeyRegexPattern { get; init; }

    /// <summary>
    /// Gets the schema template for dictionary values.
    /// </summary>
    public required ConfigurationNodeDefinition ValueTemplate { get; init; }

    /// <summary>
    /// Gets whether keys containing ':' are rejected for Microsoft configuration projection.
    /// </summary>
    public bool DisallowColonInKey { get; init; } = true;
}
