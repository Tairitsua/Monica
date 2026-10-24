namespace Monica.Configuration.Models;

/// <summary>
/// Describes all runtime sources that contribute a value for one configuration path.
/// </summary>
public sealed record ConfigurationSourceChain
{
    /// <summary>Gets whether current providers describe managed effective values or deployment candidates.</summary>
    public ConfigurationKind Kind { get; init; } = ConfigurationKind.Managed;

    /// <summary>Gets the frozen running Startup value and its candidate diagnostics, when applicable.</summary>
    public ConfigurationEffectiveValue? StartupActualValue { get; init; }

    /// <summary>Gets the original provider contributions captured before composition for a Startup definition.</summary>
    public IReadOnlyList<ConfigurationSourceValue> StartupValues { get; init; } = [];

    /// <summary>
    /// Gets the owning configuration definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the logical path inside the configuration definition.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>
    /// Gets the projected Microsoft configuration path.
    /// </summary>
    public required string ConfigurationPath { get; init; }

    /// <summary>
    /// Gets the source values ordered from highest priority to lowest priority.
    /// </summary>
    public IReadOnlyList<ConfigurationSourceValue> Values { get; init; } = [];
}

/// <summary>
/// Describes one source's contribution to a configuration path.
/// </summary>
public sealed record ConfigurationSourceValue
{
    /// <summary>
    /// Gets the source descriptor.
    /// </summary>
    public required ConfigurationSourceDescriptor Source { get; init; }

    /// <summary>
    /// Gets the source value. Sensitive values are null unless the read explicitly requests disclosure.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets whether this source currently wins the Microsoft configuration stack.
    /// </summary>
    public bool IsEffective { get; init; }

    /// <summary>
    /// Gets whether the schema marks this value as sensitive, even when disclosure was requested.
    /// </summary>
    public bool IsSensitive { get; init; }
}

/// <summary>
/// Describes source contribution counts for a whole configuration definition.
/// </summary>
public sealed record ConfigurationDefinitionSourceContribution
{
    /// <summary>
    /// Gets the source descriptor.
    /// </summary>
    public required ConfigurationSourceDescriptor Source { get; init; }

    /// <summary>
    /// Gets the number of scalar values supplied by this source for the definition.
    /// </summary>
    public int SuppliedValueCount { get; init; }

    /// <summary>
    /// Gets the number of scalar values where this source is the effective winner.
    /// </summary>
    public int EffectiveValueCount { get; init; }
}

/// <summary>
/// Describes all visible configuration values supplied by one runtime source.
/// </summary>
public sealed record ConfigurationSourceInventory
{
    /// <summary>
    /// Gets the inspected runtime source.
    /// </summary>
    public required ConfigurationSourceDescriptor Source { get; init; }

    /// <summary>
    /// Gets the number of visible scalar values supplied by this source.
    /// </summary>
    public int SuppliedValueCount { get; init; }

    /// <summary>
    /// Gets the number of supplied scalar values where this source currently wins.
    /// </summary>
    public int EffectiveValueCount { get; init; }

    /// <summary>
    /// Gets the number of Monica-managed scalar values supplied by this source.
    /// </summary>
    public int ManagedValueCount { get; init; }

    /// <summary>
    /// Gets the number of non-Monica runtime scalar values supplied by this source.
    /// </summary>
    public int UnmanagedValueCount { get; init; }

    /// <summary>
    /// Gets the supplied visible scalar values.
    /// </summary>
    public IReadOnlyList<ConfigurationSourceInventoryItem> Items { get; init; } = [];
}

/// <summary>
/// Describes one visible configuration value supplied by a runtime source.
/// </summary>
public sealed record ConfigurationSourceInventoryItem
{
    /// <summary>
    /// Gets whether this value belongs to a Monica-managed configuration definition.
    /// </summary>
    public bool IsManagedByMonica { get; init; } = true;

    /// <summary>
    /// Gets the owning definition key. This is null for unmanaged runtime values.
    /// </summary>
    public string? DefinitionKey { get; init; }

    /// <summary>
    /// Gets the owning definition display name. This is null for unmanaged runtime values.
    /// </summary>
    public string? DefinitionDisplayName { get; init; }

    /// <summary>
    /// Gets the projected Microsoft configuration path.
    /// </summary>
    public required string ConfigurationPath { get; init; }

    /// <summary>
    /// Gets the path relative to the owning definition section.
    /// </summary>
    public required string RelativeConfigurationPath { get; init; }

    /// <summary>
    /// Gets the schema node display name when the path can be resolved.
    /// </summary>
    public string? NodeLabel { get; init; }

    /// <summary>
    /// Gets the source value. Sensitive values are null unless the read explicitly requests disclosure.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets whether the schema marks this value as sensitive, even when disclosure was requested.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Gets whether this source currently wins for the path.
    /// </summary>
    public bool IsEffective { get; init; }
}

/// <summary>
/// Describes a normalized configuration file payload with sensitive values masked by default.
/// </summary>
public sealed record ConfigurationSourceFileView
{
    /// <summary>
    /// Gets the source descriptor.
    /// </summary>
    public required ConfigurationSourceDescriptor Source { get; init; }

    /// <summary>
    /// Gets normalized JSON content. Known sensitive paths are redacted unless disclosure was requested.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets whether sensitive managed values were redacted.
    /// </summary>
    public bool HasRedactions { get; init; }
}

/// <summary>
/// Describes a requested mutation against a specific external configuration source.
/// </summary>
public sealed record ConfigurationSourceMutationRequest
{
    /// <summary>
    /// Gets the target source key.
    /// </summary>
    public required string SourceKey { get; init; }

    /// <summary>
    /// Gets the target configuration definition key.
    /// </summary>
    public required string DefinitionKey { get; init; }

    /// <summary>
    /// Gets the target logical path.
    /// </summary>
    public required LogicalPath LogicalPath { get; init; }

    /// <summary>
    /// Gets the mutation kind.
    /// </summary>
    public ConfigurationMutationKind MutationKind { get; init; }

    /// <summary>
    /// Gets the JSON value payload.
    /// </summary>
    public required ConfigurationStoredValue Value { get; init; }

    /// <summary>
    /// Gets the expected schema version.
    /// </summary>
    public int ExpectedSchemaVersion { get; init; }

    /// <summary>
    /// Gets the expected source content revision hash.
    /// </summary>
    public string? ExpectedSourceRevision { get; init; }

    /// <summary>
    /// Gets the mutation context.
    /// </summary>
    public ConfigurationMutationContext Context { get; init; } = new();
}
