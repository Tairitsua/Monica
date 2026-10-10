namespace Monica.Configuration.Models;

/// <summary>Describes how much of a configuration contract was evaluated.</summary>
public enum ConfigurationValidationCoverage
{
    /// <summary>The complete applicable schema and locally owned object rules were evaluated.</summary>
    Complete,
    /// <summary>Only portable schema checks or a fragment were evaluated.</summary>
    SchemaOnly,
    /// <summary>A binding, contract, or execution fault prevented validation.</summary>
    Failed
}

/// <summary>Identifies the value boundary evaluated by a report.</summary>
public enum ConfigurationValidationScope
{
    /// <summary>A value fragment without its owning aggregate.</summary>
    Fragment,
    /// <summary>A complete effective configuration aggregate.</summary>
    CompleteAggregate,
    /// <summary>The actual options instance after Configure and PostConfigure.</summary>
    ActualOptions
}

/// <summary>Identifies the owner of a validation problem.</summary>
public enum ConfigurationValidationIssueKind
{
    /// <summary>A portable schema constraint was violated.</summary>
    Schema,
    /// <summary>A locally owned object rule returned a validation failure.</summary>
    Code,
    /// <summary>The validation contract cannot be executed faithfully.</summary>
    Contract,
    /// <summary>Binding or user validation code failed to execute.</summary>
    Execution
}

/// <summary>Describes the executable validation capability published by an owner.</summary>
public enum ConfigurationValidationCapability
{
    /// <summary>The owner has not published an affirmative capability.</summary>
    Unknown,
    /// <summary>The contract contains only portable schema rules.</summary>
    PortableOnly,
    /// <summary>The contract also requires locally owned object code.</summary>
    ObjectCode
}

/// <summary>
/// Publishes validation capability without granting remote metadata authority to load or execute CLR types.
/// </summary>
public sealed record ConfigurationValidationContract
{
    /// <summary>Gets the capability; older metadata defaults to unknown.</summary>
    public ConfigurationValidationCapability Capability { get; init; } = ConfigurationValidationCapability.Unknown;

    /// <summary>
    /// Gets the owner revision of executable validation, independently of the structural schema hash.
    /// Unknown historical contracts have no revision.
    /// </summary>
    public string? Revision { get; init; }
}

/// <summary>Describes coverage and executable revision for one owner in an aggregate runtime report.</summary>
public sealed record ConfigurationValidationDefinitionReport
{
    /// <summary>Gets the owning definition key.</summary>
    public required string DefinitionKey { get; init; }
    /// <summary>Gets the local executable contract revision.</summary>
    public string? ValidationRevision { get; init; }
    /// <summary>Gets the evaluated coverage.</summary>
    public ConfigurationValidationCoverage Coverage { get; init; }
    /// <summary>Gets the evaluated scope.</summary>
    public ConfigurationValidationScope Scope { get; init; } = ConfigurationValidationScope.CompleteAggregate;
    /// <summary>Gets whether all portable schema checks passed.</summary>
    public bool IsSchemaValid { get; init; }
    /// <summary>Gets whether the complete applicable contract passed.</summary>
    public bool IsValid { get; init; }
}
