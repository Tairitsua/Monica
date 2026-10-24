using System.Text.Json.Serialization;
using Monica.Configuration.Models;

namespace Monica.ProjectUnits.Models;

/// <summary>
/// Describes one public method without exposing <see cref="System.Reflection.MethodInfo"/>.
/// </summary>
public sealed class ProjectUnitMethodSummary
{
    /// <summary>
    /// Gets the method name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the readable method signature.
    /// </summary>
    public required string Signature { get; init; }

    /// <summary>
    /// Gets the XML documentation summary when available.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether the method is static.
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Gets whether the method can be overridden.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// Gets whether the method is abstract.
    /// </summary>
    public bool IsAbstract { get; init; }

    /// <summary>
    /// Gets whether the method returns a task-like value.
    /// </summary>
    public bool IsAsync { get; init; }
}

/// <summary>
/// Describes how one unit consumes a configuration project unit.
/// </summary>
public sealed class ProjectUnitConfigurationUsage
{
    /// <summary>
    /// Gets the consuming project unit.
    /// </summary>
    public required ProjectUnitReference Unit { get; init; }

    /// <summary>
    /// Gets the options accessor discovered for the consumer. The accessor alone does not determine reload behavior.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EConfigurationUsageType UsageType { get; init; }
}

/// <summary>
/// Provides serializable configuration-specific details for a configuration project unit.
/// </summary>
public sealed class ProjectUnitConfigurationDetail
{
    /// <summary>
    /// Gets the Monica.Configuration definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets whether values require restart. Startup values always require restart; Managed values use discovered access patterns.
    /// </summary>
    public bool? IsOffline { get; init; }

    /// <summary>
    /// Gets the reload behavior determined by Startup ownership or inferred from Managed consumer access patterns.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConfigurationReloadBehavior? ReloadBehavior { get; init; }

    /// <summary>
    /// Gets discovered configuration consumers and their access patterns.
    /// </summary>
    public IReadOnlyList<ProjectUnitConfigurationUsage> Usages { get; init; } = [];
}

/// <summary>
/// Provides a complete serializable detail projection for one project unit.
/// </summary>
public sealed class ProjectUnitDetail
{
    /// <summary>
    /// Gets the unit's catalog summary.
    /// </summary>
    public required ProjectUnitSummary Summary { get; init; }

    /// <summary>
    /// Gets units that depend on this unit.
    /// </summary>
    public IReadOnlyList<ProjectUnitReference> Dependents { get; init; } = [];

    /// <summary>
    /// Gets resolved and unresolved requirement references.
    /// </summary>
    public IReadOnlyList<ProjectUnitRequirementReference> Requirements { get; init; } = [];

    /// <summary>
    /// Gets public method metadata.
    /// </summary>
    public IReadOnlyList<ProjectUnitMethodSummary> Methods { get; init; } = [];

    /// <summary>
    /// Gets readable constructor parameter type names.
    /// </summary>
    public IReadOnlyList<string> ConstructorParameters { get; init; } = [];

    /// <summary>
    /// Gets configuration-specific details when the unit represents a Monica configuration.
    /// </summary>
    public ProjectUnitConfigurationDetail? Configuration { get; init; }
}
