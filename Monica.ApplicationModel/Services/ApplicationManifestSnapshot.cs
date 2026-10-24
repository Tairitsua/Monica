using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Models;

namespace Monica.ApplicationModel.Services;

/// <summary>Reads a provider once and detaches its inventory from mutable authoring collections.</summary>
internal sealed class ApplicationManifestSnapshot(IApplicationManifest manifest) : IApplicationManifest
{
    public IReadOnlyList<string> LocalAssemblies { get; } = Array.AsReadOnly(manifest.LocalAssemblies.ToArray());
    public IReadOnlyList<ApplicationDomainDefinition> Domains { get; } = Array.AsReadOnly(manifest.Domains.ToArray());
    public IReadOnlyList<ApplicationRoleDefinition> Roles { get; } = Array.AsReadOnly(manifest.Roles.ToArray());
    public IReadOnlyList<ApplicationOperationDefinition> Operations { get; } = Array.AsReadOnly(manifest.Operations.ToArray());
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients { get; } = Array.AsReadOnly(manifest.ApiClients.ToArray());
    public IReadOnlyList<ApplicationDependencyDefinition> Dependencies { get; } = Array.AsReadOnly(manifest.Dependencies.ToArray());
}
