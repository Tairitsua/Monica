namespace Monica.Core.Modularity.Diagnostics.Annotations;

/// <summary>
/// Marks an option property as sensitive for automatic diagnostics projection. The property always remains visible
/// by name and type; the ordinary workbench redacts its value, while the explicit sensitive-value reveal policy may reveal a
/// bounded scalar representation.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ModuleOptionDiagnosticsSensitiveAttribute : Attribute;
