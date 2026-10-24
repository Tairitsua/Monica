using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core.Mediator;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Annotations;

namespace Monica.ApplicationModel.Services;

internal sealed class CompiledApplicationModel : IApplicationModel, ITypeActivationPolicy
{
    private readonly ApplicationDeployment _deployment;
    private readonly FrozenDictionary<string, ApplicationRoleDefinition> _assemblyRoles;
    private readonly FrozenDictionary<Type, ApplicationOperationDefinition[]> _handlerOperations;
    private readonly FrozenSet<string> _localRoles;
    private readonly FrozenDictionary<string, ApplicationRoleDefinition> _allAssemblyRoles;
    private readonly FrozenDictionary<string, ApplicationDomainDefinition> _assemblyDomains;
    private readonly FrozenSet<string> _localAssemblies;
    private readonly bool _autonomousWork;

    public CompiledApplicationModel(IApplicationManifest manifest, ApplicationDeployment deployment, ITypeFinder finder, bool autonomousWork = true)
    {
        _autonomousWork = autonomousWork;
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
            if (localAssemblies.Contains(domain.AssemblyName))
            {
                var declaration = RequireAssembly(domain.AssemblyName).GetCustomAttribute<ApplicationDomainAttribute>();
                if (declaration?.DomainId != domain.Id || declaration.BackgroundRole != domain.BackgroundRole)
                    throw new InvalidOperationException($"Domain '{domain.Id}' does not match its compiled assembly declaration.");
            }
            if (domain.BackgroundRole is { } backgroundRole)
            {
                ApplicationDeployment.RequireIdentifier(backgroundRole, "BackgroundRole");
                if (!Roles.TryGetValue(backgroundRole, out var owner) || owner.DomainId != domain.Id)
                    throw new InvalidOperationException($"BackgroundRole '{backgroundRole}' must name a declared role in domain '{domain.Id}'.");
            }
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
        _allAssemblyRoles = Roles.Values.ToFrozenDictionary(role => role.AssemblyName, StringComparer.Ordinal);
        _assemblyDomains = Domains.Values.ToFrozenDictionary(domain => domain.AssemblyName, StringComparer.Ordinal);
        _localAssemblies = localAssemblies.ToFrozenSet(StringComparer.Ordinal);
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
        Dependencies = Array.AsReadOnly(manifest.Dependencies.Distinct().ToArray());
        foreach (var dependency in Dependencies)
        {
            if (!ownedAssemblies.Contains(dependency.SourceAssemblyName) || !Operations.ContainsKey(dependency.RequestType))
                throw new InvalidOperationException($"Application dependency from '{dependency.SourceTypeName}' has unknown source or operation ownership.");
        }
        LocalHandlers = localHandlers.ToFrozenDictionary();
        _handlerOperations = handlers.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        Assembly RequireAssembly(string name) => assemblies.TryGetValue(name, out var assembly) ? assembly :
            throw new InvalidOperationException($"Compiled application assembly '{name}' is excluded from this host's discovery inventory.");
    }

    public string AppId => _deployment.AppId;
    public ApplicationDeployment Deployment => _deployment;
    public IReadOnlyCollection<string> ActiveWorkloadOwners { get; }
    public IReadOnlyDictionary<string, ApplicationDomainDefinition> Domains { get; }
    public IReadOnlyDictionary<string, ApplicationRoleDefinition> Roles { get; }
    public IReadOnlyDictionary<Type, ApplicationOperationDefinition> Operations { get; }
    public IReadOnlyDictionary<Type, Type> LocalHandlers { get; }
    public IReadOnlyList<ApplicationDependencyDefinition> Dependencies { get; }
    public IReadOnlyDictionary<string, string> RoleApplications => _deployment.RoleApplications;
    public bool IsRoleActive(string roleId) => _deployment.IsRoleActive(roleId);
    public bool IsRoleAvailable(string roleId) => _localRoles.Contains(roleId);
    public ApplicationOperationDefinition GetOperation(Type requestType) => Operations.TryGetValue(requestType, out var operation)
        ? operation : throw new InvalidOperationException($"Request '{requestType}' is not present in the compiled application manifest.");
    public string ResolveRoleAppId(string roleId) => RoleApplications.TryGetValue(roleId, out var appId) ? appId :
        throw new InvalidOperationException($"Role '{roleId}' has no owner in this deployment scope.");
    public ApplicationRoleDefinition? FindRole(Assembly assembly) => _assemblyRoles.GetValueOrDefault(assembly.GetName().Name!);

    public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null)
    {
        ArgumentNullException.ThrowIfNull(implementationType);
        var implementation = GetOwnership(implementationType);
        var declared = registrationOwner is null ? default : GetOwnership(registrationOwner);
        ValidateOwnership(implementation, declared);
        if (!IsIncluded(implementation) || !IsIncluded(declared)) return ActivationMode.Disabled;
        if (kind is TypeActivationKind.Service or TypeActivationKind.RequestHandler)
        {
            // Held background execution must not remove the same implementation's passive DI surface.
            return !_handlerOperations.TryGetValue(implementationType, out var operations)
                || operations.Any(operation => operation.RequiredRole is null || IsRoleActive(operation.RequiredRole))
                ? ActivationMode.Automatic : ActivationMode.Disabled;
        }
        if (kind is TypeActivationKind.Seeder or TypeActivationKind.HostedService
            && implementationType.IsDefined(typeof(ProcessLocalAttribute), inherit: false))
            return ActivationMode.Automatic;
        var role = ResolveOwner(implementation, declared, requireBackgroundRole: kind != TypeActivationKind.Endpoint);
        if (role is not null && !IsRoleActive(role.Id)) return ActivationMode.Disabled;
        if (kind == TypeActivationKind.Endpoint) return ActivationMode.Automatic;
        return _autonomousWork ? ActivationMode.Automatic : ActivationMode.Manual;
    }

    private (ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) GetOwnership(Type type)
    {
        var name = type.Assembly.GetName().Name!;
        var role = _allAssemblyRoles.GetValueOrDefault(name);
        var domain = role is not null ? Domains[role.DomainId] : _assemblyDomains.GetValueOrDefault(name);
        if (role is null && domain is null && (type.Assembly.IsDefined(typeof(ApplicationRoleAttribute))
            || type.Assembly.IsDefined(typeof(ApplicationDomainAttribute))))
            throw new InvalidOperationException($"Owned implementation '{type.FullName}' is absent from the compiled application inventory.");
        return (role, domain, domain is null ? null : name);
    }

    private bool IsIncluded((ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) owner)
        => owner.Assembly is null || _localAssemblies.Contains(owner.Assembly);

    private static void ValidateOwnership(
        (ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) implementation,
        (ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) declared)
    {
        if (implementation.Role is { } implementationRole && declared.Role is { } declaredRole && implementationRole.Id != declaredRole.Id
            || implementation.Domain is { } implementationDomain && declared.Domain is { } declaredDomain && implementationDomain.Id != declaredDomain.Id)
            throw new InvalidOperationException("A resource's registration owner conflicts with its declared application implementation ownership.");
    }

    private ApplicationRoleDefinition? ResolveOwner(
        (ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) implementation,
        (ApplicationRoleDefinition? Role, ApplicationDomainDefinition? Domain, string? Assembly) declared,
        bool requireBackgroundRole)
    {
        if (implementation.Role is { } role) return role;
        if (declared.Role is { } owner) return owner;
        var domain = declared.Domain ?? implementation.Domain;
        if (domain is null || !requireBackgroundRole) return null;
        if (domain.BackgroundRole is null)
            throw new InvalidOperationException($"Domain '{domain.Id}' declares autonomous work but has no BackgroundRole.");
        return Roles[domain.BackgroundRole];
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

    public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null)
    {
        var implementation = GetOwnership(implementationType);
        var declared = registrationOwner is null ? default : GetOwnership(registrationOwner);
        ValidateOwnership(implementation, declared);
        var role = ResolveOwner(implementation, declared, true);
        return role is null ? null : role.WorkloadOwner ?? role.Id;
    }

    private static FrozenDictionary<string, T> Unique<T>(IEnumerable<T> definitions, Func<T, string> key, string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var id = key(definition);
            ApplicationDeployment.RequireIdentifier(id, kind);
            if (!result.TryAdd(id, definition)) throw new InvalidOperationException($"Duplicate application {kind} '{id}'.");
            if (!identifiers.Add(id))
                throw new InvalidOperationException($"Application {kind} '{id}' is ambiguous under case-insensitive configuration lookup.");
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
