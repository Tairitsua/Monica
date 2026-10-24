using System.Reflection;
using Monica.ApplicationModel.Models;

namespace Monica.ApplicationModel.Abstractions;

/// <summary>Exposes a validated immutable application inventory and this host's startup placement.</summary>
public interface IApplicationModel
{
    /// <summary>Gets the current application's identity, shared by its replicas.</summary>
    string AppId { get; }
    /// <summary>Gets the complete immutable deployment map and canonical identity used by this host.</summary>
    ApplicationDeployment Deployment { get; }
    /// <summary>Gets all declared domains.</summary>
    IReadOnlyDictionary<string, ApplicationDomainDefinition> Domains { get; }
    /// <summary>Gets all declared roles.</summary>
    IReadOnlyDictionary<string, ApplicationRoleDefinition> Roles { get; }
    /// <summary>Gets the complete request-to-operation inventory.</summary>
    IReadOnlyDictionary<Type, ApplicationOperationDefinition> Operations { get; }
    /// <summary>Gets request-to-handler bindings verified against this executable's discovery inventory.</summary>
    /// <remarks>Remote-only operations have no entry. A local binding does not override a request's required active role.</remarks>
    IReadOnlyDictionary<Type, Type> LocalHandlers { get; }
    /// <summary>Gets compiled API dependency evidence, independently of role placement, replicas, or transport.</summary>
    IReadOnlyList<ApplicationDependencyDefinition> Dependencies { get; }
    /// <summary>Gets role-to-AppId ownership in this deployment scope.</summary>
    IReadOnlyDictionary<string, string> RoleApplications { get; }
    /// <summary>Whether the current executable includes the exact role's implementation.</summary>
    bool IsRoleAvailable(string roleId);
    /// <summary>Whether the current AppId activates the exact role identifier.</summary>
    bool IsRoleActive(string roleId);
    /// <summary>Gets an operation or throws for a request outside this application.</summary>
    ApplicationOperationDefinition GetOperation(Type requestType);
    /// <summary>Gets a role's configured receiving application or throws if placement is missing.</summary>
    string ResolveRoleAppId(string roleId);
    /// <summary>Gets the code-owning role for an assembly, or null for core/framework assemblies.</summary>
    ApplicationRoleDefinition? FindRole(Assembly assembly);
}
