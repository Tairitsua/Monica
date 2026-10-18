using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core.Mediator;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.ApplicationModel.Services;

internal sealed class CompiledApplicationModel : IApplicationModel, ITypeActivationPolicy
{
    private readonly ApplicationDeployment _deployment;
    private readonly FrozenDictionary<string, ApplicationRoleDefinition> _assemblyRoles;
    private readonly FrozenDictionary<Type, ApplicationOperationDefinition[]> _handlerOperations;
    private readonly FrozenSet<string> _localRoles;

    public CompiledApplicationModel(IApplicationManifest manifest, ApplicationDeployment deployment, ITypeFinder finder)
    {
        _deployment = deployment;
        Domains = Unique(manifest.Domains, definition => definition.Id, "domain");
        Roles = Unique(manifest.Roles, definition => definition.Id, "role");
        var localAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assemblyName in manifest.LocalAssemblies)
        {
            ApplicationDeployment.RequireIdentifier(assemblyName, "Local assembly");
            if (!localAssemblies.Add(assemblyName))
                throw new InvalidOperationException($"Duplicate local application assembly '{assemblyName}'.");
        }
        var assemblies = finder.GetAssemblies().ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);
        var inventory = finder.GetTypes().ToHashSet();
        var ownedAssemblies = new HashSet<string>(StringComparer.Ordinal);
        var workloadOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domain in Domains.Values)
        {
            ApplicationDeployment.RequireIdentifier(domain.AssemblyName, "Domain assembly");
            if (!ownedAssemblies.Add(domain.AssemblyName))
                throw new InvalidOperationException($"Domain '{domain.Id}' does not own a unique assembly.");
            if (localAssemblies.Contains(domain.AssemblyName) &&
                RequireAssembly(domain.AssemblyName).GetCustomAttribute<ApplicationDomainAttribute>()?.DomainId != domain.Id)
                throw new InvalidOperationException($"Domain '{domain.Id}' does not match its compiled assembly declaration.");
        }
        foreach (var role in Roles.Values)
        {
            ApplicationDeployment.RequireIdentifier(role.AssemblyName, "Role assembly");
            if (!Domains.TryGetValue(role.DomainId, out var domain) || !ownedAssemblies.Add(role.AssemblyName))
                throw new InvalidOperationException($"Role '{role.Id}' does not match a unique compiled domain/assembly declaration.");
            if (localAssemblies.Contains(role.AssemblyName))
            {
                var declaration = RequireAssembly(role.AssemblyName).GetCustomAttribute<ApplicationRoleAttribute>();
                if (declaration?.RoleId != role.Id || declaration.DomainId != role.DomainId || declaration.WorkloadOwner != role.WorkloadOwner)
                    throw new InvalidOperationException($"Role '{role.Id}' does not match its compiled assembly declaration.");
                if (!localAssemblies.Contains(domain.AssemblyName))
                    throw new InvalidOperationException($"Local role '{role.Id}' requires its domain core '{domain.Id}' in the local inventory.");
            }
            var workloadOwner = role.WorkloadOwner ?? role.Id;
            ApplicationDeployment.RequireIdentifier(workloadOwner, "Workload owner");
            if (!workloadOwners.Add(workloadOwner))
                throw new InvalidOperationException($"Workload owner '{workloadOwner}' belongs to multiple roles; shared scheduler snapshots would retire each other's definitions.");
        }
        foreach (var assemblyName in localAssemblies)
            if (!ownedAssemblies.Contains(assemblyName))
                throw new InvalidOperationException($"Local application assembly '{assemblyName}' has no declared domain or role owner.");
        foreach (var assembly in assemblies.Values)
        {
            if ((assembly.GetCustomAttribute<ApplicationDomainAttribute>() is not null ||
                 assembly.GetCustomAttribute<ApplicationRoleAttribute>() is not null) &&
                !localAssemblies.Contains(assembly.GetName().Name!))
                throw new InvalidOperationException($"Annotated assembly '{assembly.GetName().Name}' is absent from the local compiled manifest. Regenerate this host's inventory.");
        }
        foreach (var roleId in RoleApplications.Keys)
            if (!Roles.ContainsKey(roleId))
                throw new InvalidOperationException($"Deployment names unknown role '{roleId}'.");
        _localRoles = Roles.Values.Where(role => localAssemblies.Contains(role.AssemblyName))
            .Select(role => role.Id).ToFrozenSet(StringComparer.Ordinal);
        // The catalog owns placement globally; only this executable's selected roles require local implementations.
        foreach (var roleId in Roles.Keys)
        {
            _ = ResolveRoleAppId(roleId);
            if (IsRoleActive(roleId) && !IsRoleAvailable(roleId))
                throw new InvalidOperationException($"Active role '{roleId}' has no implementation in this executable for AppId '{AppId}'.");
        }

        _assemblyRoles = Roles.Values.Where(role => IsRoleAvailable(role.Id))
            .ToFrozenDictionary(role => role.AssemblyName, StringComparer.Ordinal);
        ActiveWorkloadOwners = Array.AsReadOnly(Roles.Values.Where(role => IsRoleActive(role.Id))
            .Select(role => role.WorkloadOwner ?? role.Id).Order(StringComparer.Ordinal).ToArray());
        var operations = new Dictionary<Type, ApplicationOperationDefinition>();
        var handlers = new Dictionary<Type, List<ApplicationOperationDefinition>>();
        var localHandlers = new Dictionary<Type, Type>();
        foreach (var operation in manifest.Operations)
        {
            ApplicationDeployment.RequireIdentifier(operation.HandlerTypeName, "Operation handler");
            if (!operations.TryAdd(operation.RequestType, operation))
                throw new InvalidOperationException($"Request '{operation.RequestType}' has multiple application handlers.");
            if (!Roles.TryGetValue(operation.RoleId, out var role) || role.DomainId != operation.DomainId ||
                role.AssemblyName != operation.HandlerAssemblyName)
                throw new InvalidOperationException($"Operation '{operation.RequestType}' has inconsistent role ownership.");
            var declaredRequirement = operation.RequestType.GetCustomAttribute<RunOnRoleAttribute>()?.RoleId;
            if (operation.RequiredRole != declaredRequirement ||
                operation.RequiredRole is { } required && required != operation.RoleId)
                throw new InvalidOperationException($"Operation '{operation.RequestType}' must run on its declared owning role.");
            if (!IsRoleAvailable(operation.RoleId)) continue;
            var handler = RequireAssembly(operation.HandlerAssemblyName).GetType(operation.HandlerTypeName, throwOnError: true)!;
            if (!inventory.Contains(handler) || handler.IsAbstract || handler.ContainsGenericParameters ||
                !handler.GetInterfaces().Any(contract => contract.IsGenericType &&
                    contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>) &&
                    contract.GenericTypeArguments[0] == operation.RequestType))
                throw new InvalidOperationException($"Handler '{handler}' is missing from the host discovery inventory or does not handle '{operation.RequestType}'.");
            if (!handlers.TryGetValue(handler, out var owned)) handlers.Add(handler, owned = []);
            owned.Add(operation);
            localHandlers.Add(operation.RequestType, handler);
        }
        Operations = operations.ToFrozenDictionary();
        LocalHandlers = localHandlers.ToFrozenDictionary();
        _handlerOperations = handlers.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        Assembly RequireAssembly(string name) => assemblies.TryGetValue(name, out var assembly) ? assembly :
            throw new InvalidOperationException($"Compiled application assembly '{name}' is excluded from this host's discovery inventory.");
    }

    public string AppId => _deployment.AppId;
    public IReadOnlyCollection<string> ActiveWorkloadOwners { get; }
    public IReadOnlyDictionary<string, ApplicationDomainDefinition> Domains { get; }
    public IReadOnlyDictionary<string, ApplicationRoleDefinition> Roles { get; }
    public IReadOnlyDictionary<Type, ApplicationOperationDefinition> Operations { get; }
    public IReadOnlyDictionary<Type, Type> LocalHandlers { get; }
    public IReadOnlyDictionary<string, string> RoleApplications => _deployment.RoleApplications;
    public bool IsRoleActive(string roleId) => _deployment.IsRoleActive(roleId);
    public bool IsRoleAvailable(string roleId) => _localRoles.Contains(roleId);
    public ApplicationOperationDefinition GetOperation(Type requestType) => Operations.TryGetValue(requestType, out var operation)
        ? operation : throw new InvalidOperationException($"Request '{requestType}' is not present in the compiled application manifest.");
    public string ResolveRoleAppId(string roleId) => RoleApplications.TryGetValue(roleId, out var appId) ? appId :
        throw new InvalidOperationException($"Role '{roleId}' has no owner in this deployment scope.");
    public ApplicationRoleDefinition? FindRole(Assembly assembly) => _assemblyRoles.GetValueOrDefault(assembly.GetName().Name!);

    public bool IsEnabled(Type implementationType, TypeActivationKind kind)
    {
        var role = FindRole(implementationType.Assembly);
        if (role is null) return true;
        if (kind is TypeActivationKind.Job or TypeActivationKind.ExternalSubscription or TypeActivationKind.HostedService or TypeActivationKind.Endpoint)
            return IsRoleActive(role.Id);
        if (kind == TypeActivationKind.Service && typeof(IHostedService).IsAssignableFrom(implementationType))
            return IsRoleActive(role.Id);
        // Ordinary handlers and passive services stay available. A constrained handler can never be constructed remotely.
        return !_handlerOperations.TryGetValue(implementationType, out var operations) ||
            operations.Any(operation => operation.RequiredRole is null || IsRoleActive(operation.RequiredRole));
    }

    public void EnsureCanExecuteRequest(Type requestType)
    {
        var known = Operations.TryGetValue(requestType, out var operation);
        if (known && !LocalHandlers.ContainsKey(requestType))
            throw new InvalidOperationException($"Request '{requestType}' has no local implementation in AppId '{AppId}'. Use its generated application API to reach role '{operation!.RoleId}'.");
        var requirement = known
            ? operation!.RequiredRole : requestType.GetCustomAttribute<RunOnRoleAttribute>()?.RoleId;
        if (requirement is not null && !IsRoleActive(requirement))
            throw new InvalidOperationException($"Request '{requestType}' requires role '{requirement}', which is not active in AppId '{AppId}'. Use its generated application API.");
    }

    public string? GetWorkloadOwner(Type implementationType) => FindRole(implementationType.Assembly) is { } role
        ? role.WorkloadOwner ?? role.Id : null;

    private static FrozenDictionary<string, T> Unique<T>(IEnumerable<T> definitions, Func<T, string> key, string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var id = key(definition);
            ApplicationDeployment.RequireIdentifier(id, kind);
            if (!result.TryAdd(id, definition)) throw new InvalidOperationException($"Duplicate application {kind} '{id}'.");
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
