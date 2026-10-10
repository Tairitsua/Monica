using Monica.Configuration.Models;

namespace Monica.Configuration.Annotations;

/// <summary>
/// Marks a CLR options type as a Monica-managed configuration definition.
/// </summary>
/// <remarks>
/// Configuration identity is declared per options type and is not inherited by derived classes.
/// A locally discovered owner must be a concrete, closed class with a public parameterless instance constructor,
/// matching Microsoft options creation. The class itself need not be public. Discovery rejects an unsupported
/// root constructor before registration or effective-value seeding; nested binding retains its own construction rules.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ConfigurationAttribute : Attribute
{
    private string? _sectionPath;

    /// <summary>
    /// Initializes a new configuration attribute without an explicit section path.
    /// The scanner derives the binding section from the module's configured section path convention.
    /// </summary>
    public ConfigurationAttribute()
    {
    }

    /// <summary>
    /// Initializes a new configuration attribute with an explicit binding section path.
    /// </summary>
    /// <param name="sectionPath">The root Microsoft configuration section path.</param>
    public ConfigurationAttribute(string sectionPath)
    {
        _sectionPath = sectionPath;
    }

    /// <summary>
    /// Gets the root Microsoft configuration section path.
    /// </summary>
    /// <remarks>
    /// When set, this value is the highest-priority binding path for Monica scanning and bootstrap binding.
    /// When omitted, Monica uses the module's section path convention; bootstrap binding uses the short CLR type name.
    /// </remarks>
    public string? SectionPath
    {
        get => _sectionPath;
        set => _sectionPath = value;
    }

    /// <summary>
    /// Gets or sets the stable definition key.
    /// </summary>
    /// <remarks>
    /// When omitted, Monica uses the CLR full type name. An explicit key is recommended when persisted metadata,
    /// effective values, or history must retain the same identity across namespace or type-name refactoring.
    /// </remarks>
    public string? DefinitionKey { get; set; }

    /// <summary>
    /// Gets or sets the display name shown in management tools.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the developer-facing description shown in generated documentation.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a developer-defined category used for grouping configuration definitions.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the default reload behavior for nodes under this definition.
    /// </summary>
    public ConfigurationReloadBehavior ReloadBehavior { get; set; } = ConfigurationReloadBehavior.Unknown;

}
