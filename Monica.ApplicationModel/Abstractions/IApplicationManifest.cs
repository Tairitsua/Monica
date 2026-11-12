using Monica.ApplicationModel.Models;

namespace Monica.ApplicationModel.Abstractions;

/// <summary>Supplies compile-time application ownership, independently of environment placement.</summary>
/// <remarks>Generated in the business host. Logical ownership may include remote-only roles. Implementations expose deterministic, stable collections.</remarks>
public interface IApplicationManifest
{
    /// <summary>Gets the annotated domain and role assemblies whose implementations are included in this executable.</summary>
    /// <remarks>The default describes a complete host. A partial host must explicitly identify its local assemblies; omission from discovery is still an error.</remarks>
    IReadOnlyList<string> LocalAssemblies => [.. Domains.Select(domain => domain.AssemblyName), .. Roles.Select(role => role.AssemblyName)];
    /// <summary>Gets the domain core inventory.</summary>
    IReadOnlyList<ApplicationDomainDefinition> Domains { get; }
    /// <summary>Gets the role library inventory.</summary>
    IReadOnlyList<ApplicationRoleDefinition> Roles { get; }
    /// <summary>Gets published application operations, including remote-only ownership, and nonpublished local requests.</summary>
    IReadOnlyList<ApplicationOperationDefinition> Operations { get; }
    /// <summary>Gets generated API interface-to-caller bindings.</summary>
    IReadOnlyList<ApplicationApiClientDefinition> ApiClients { get; }
    /// <summary>Gets statically resolved published API calls made by owned domain and role source code.</summary>
    /// <remarks>Reflection, shared unowned helpers, events and external protocols are outside this evidence scope.</remarks>
    IReadOnlyList<ApplicationDependencyDefinition> Dependencies => [];
}
