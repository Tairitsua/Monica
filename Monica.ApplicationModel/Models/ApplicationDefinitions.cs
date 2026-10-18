namespace Monica.ApplicationModel.Models;

/// <summary>Describes a domain core without requiring a public assembly-marker type.</summary>
public sealed record ApplicationDomainDefinition(string Id, string AssemblyName);

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

/// <summary>Assigns logical roles to one application; replicas share this placement.</summary>
public sealed class ApplicationPlacement
{
    /// <summary>Gets or sets the explicitly selected roles. Prefixes do not imply sibling or child roles.</summary>
    public string[] Roles { get; set; } = [];
}
