namespace Monica.ApplicationModel.Models;

/// <summary>Describes a domain core without requiring a public assembly-marker type.</summary>
public sealed record ApplicationDomainDefinition(string Id, string AssemblyName, string? BackgroundRole = null);

/// <summary>Describes one role's stable identity, business ownership, and implementation assembly.</summary>
public sealed record ApplicationRoleDefinition(string Id, string DomainId, string AssemblyName, string? WorkloadOwner = null);

/// <summary>Describes an application operation independently of its HTTP route or deployment address.</summary>
/// <remarks>Handler names identify the producer's implementation without loading it. Only local bindings resolve them to runtime types.</remarks>
public sealed record ApplicationOperationDefinition(
    Type RequestType,
    string HandlerTypeName,
    string HandlerAssemblyName,
    string DomainId,
    string RoleId,
    string? RequiredRole);

/// <summary>Registers a generated caller against the interface compiled into its contract assembly.</summary>
public sealed record ApplicationApiClientDefinition(Type InterfaceType, Type ImplementationType);

/// <summary>One statically resolved published API usage, independent of deployment and observed traffic.</summary>
/// <param name="SourceAssemblyName">The domain core or role assembly containing the call.</param>
/// <param name="SourceTypeName">The calling type retained as navigable architecture evidence.</param>
/// <param name="RequestType">The published request identifying the target operation and its owner.</param>
public sealed record ApplicationDependencyDefinition(string SourceAssemblyName, string SourceTypeName, Type RequestType);

/// <summary>Assigns logical roles to one application; replicas share this placement.</summary>
public sealed class ApplicationPlacement
{
    /// <summary>Gets or sets the application's display name. Null defaults to its AppId map key.</summary>
    public string? AppName { get; set; }

    /// <summary>Gets or sets the explicitly selected roles. Prefixes do not imply sibling or child roles.</summary>
    /// <remarks>Null is invalid; explicitly use an empty array for an application with no active roles.</remarks>
    public string[]? Roles { get; set; }
}

/// <summary>Describes one immutable application entry in a validated deployment snapshot.</summary>
/// <param name="AppName">The display name, defaulted to the entry's AppId when omitted.</param>
/// <param name="Roles">The copied, read-only collection of exact role identifiers assigned to the application.</param>
public sealed record ApplicationDeploymentEntry(string AppName, IReadOnlyList<string> Roles);
