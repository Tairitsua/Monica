using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Constants;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Helpers;

/// <summary>Builds the host inventory from declared compiled references, without deployment configuration.</summary>
internal static class ApplicationHostModelReader
{
    public static AnalysisSnapshot<ApplicationHostModel> Read(Compilation compilation, CancellationToken cancellationToken,
        bool exportOwnAssembly = false)
    {
        if (exportOwnAssembly
                ? Find(compilation.Assembly, GeneratorConstants.APPLICATION_DOMAIN_ATTRIBUTE) is null &&
                  Find(compilation.Assembly, GeneratorConstants.APPLICATION_ROLE_ATTRIBUTE) is null
                : !HostGenerationReader.IsApplicationHost(compilation.Assembly))
            return new AnalysisSnapshot<ApplicationHostModel>(null);

        // Handler ownership does not require exported marker types or public implementation classes.
        compilation = compilation.WithOptions(compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All));

        var hostLocation = Find(compilation.Assembly, GeneratorConstants.APPLICATION_HOST_ATTRIBUTE)?
            .ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
        var diagnostics = new List<DiagnosticSnapshot>();
        var assemblies = compilation.SourceModule.ReferencedAssemblySymbols.Append(compilation.Assembly)
            .Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default)
            .OrderBy(static assembly => assembly.Identity.ToString(), StringComparer.Ordinal).ToArray();
        var declarations = new Dictionary<IAssemblySymbol, ApplicationAssemblyModel>(SymbolEqualityComparer.Default);

        foreach (var assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domain = Find(assembly, GeneratorConstants.APPLICATION_DOMAIN_ATTRIBUTE);
            var role = Find(assembly, GeneratorConstants.APPLICATION_ROLE_ATTRIBUTE);
            if (domain is null && role is null) continue;
            if (domain is not null && role is not null)
            {
                InvalidDeclaration(assembly, "Declare either a domain core or one application role, not both.");
                continue;
            }

            var attribute = domain ?? role!;
            var domainIndex = role is null ? 0 : 1;
            var domainId = attribute.ConstructorArguments.Length > domainIndex
                ? NormalizeDomain(attribute.ConstructorArguments[domainIndex]) : null;
            var roleId = role?.ConstructorArguments.FirstOrDefault().Value as string;
            var workloadOwner = role?.NamedArguments.FirstOrDefault(static argument => argument.Key == "WorkloadOwner").Value.Value as string;
            var backgroundRole = domain?.NamedArguments.FirstOrDefault(static argument => argument.Key == "BackgroundRole").Value.Value as string;
            if (string.IsNullOrWhiteSpace(domainId) || domainId != domainId!.Trim() ||
                (role is not null && (string.IsNullOrWhiteSpace(roleId) || roleId != roleId!.Trim())))
            {
                InvalidDeclaration(assembly, "A non-empty domain and, for a role, a non-empty role ID are required. Enum domains must name a declared member.");
                continue;
            }

            if (workloadOwner is not null && (string.IsNullOrWhiteSpace(workloadOwner) || workloadOwner != workloadOwner.Trim()))
            {
                InvalidDeclaration(assembly, "WorkloadOwner must be a non-empty identifier without surrounding whitespace when supplied.");
                continue;
            }
            if (backgroundRole is not null && (string.IsNullOrWhiteSpace(backgroundRole) || backgroundRole != backgroundRole.Trim()))
            {
                InvalidDeclaration(assembly, "BackgroundRole must be a non-empty identifier without surrounding whitespace when supplied.");
                continue;
            }
            if (ValidateRegistration(attribute, assembly, domain is not null && backgroundRole is null) is { } registrationError)
            {
                InvalidDeclaration(assembly, registrationError);
                continue;
            }
            declarations.Add(assembly, new ApplicationAssemblyModel(assembly.Name, domainId!, roleId, workloadOwner, backgroundRole));
        }

        foreach (var duplicate in declarations.Values.GroupBy(static declaration =>
                     declaration.RoleId is null ? "domain:" + declaration.DomainId : "role:" + declaration.RoleId,
                     StringComparer.Ordinal).Where(static group => group.Count() > 1))
            InvalidDeclaration(compilation.Assembly,
                $"Ownership '{duplicate.Key}' is declared by multiple assemblies: {string.Join(", ", duplicate.Select(static declaration => declaration.AssemblyName))}.");

        var domains = new HashSet<string>(declarations.Values.Where(static declaration => declaration.RoleId is null)
            .Select(static declaration => declaration.DomainId), StringComparer.Ordinal);
        foreach (var declaration in declarations.Values.Where(static declaration => declaration.RoleId is not null))
            if (!domains.Contains(declaration.DomainId))
                diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.UndefinedApplicationDomain,
                    hostLocation, declaration.RoleId, declaration.DomainId));

        var operations = new List<ApplicationOperationModel>();
        var applicationRequests = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var handlersByRequest = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        var requestHandlerContract = compilation.GetTypeByMetadataName("Monica.Core.Mediator.IRequestHandler`2");
        foreach (var assembly in assemblies.Where(assembly => declarations.ContainsKey(assembly) ||
                     SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) || ReferencesApplication(assembly)))
        {
            if (exportOwnAssembly && !SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)) continue;
            declarations.TryGetValue(assembly, out var owner);
            foreach (var handler in HostGenerationReader.Types(assembly.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handler.TypeKind != TypeKind.Class || handler.IsAbstract || handler.IsGenericType) continue;
                // Runtime discovery also registers plain mediator handlers. They must not replace a declared
                // application handler silently, even though they do not contribute generated API operations.
                foreach (var contract in handler.AllInterfaces)
                {
                    if (!SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, requestHandlerContract) ||
                        contract.TypeArguments[0] is not INamedTypeSymbol handledRequest) continue;
                    if (!handlersByRequest.TryGetValue(handledRequest, out var handlers))
                        handlersByRequest.Add(handledRequest, handlers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
                    handlers.Add(handler);
                }
                if (!ApplicationServiceSymbolHelper.TryGetContract(handler, out var request, out var handlerResult)) continue;
                if (exportOwnAssembly && !EndpointModelFactory.IsPublishedRequest(request)) continue;
                var endpointAttribute = EndpointModelFactory.GetEndpointAttribute(request);
                var requirement = Find(request, GeneratorConstants.RUN_ON_ROLE_ATTRIBUTE);
                if (owner?.RoleId is null)
                {
                    if (endpointAttribute is not null || requirement is not null)
                        diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.MissingApplicationOwnership,
                            SourceLocation(handler, hostLocation), handler.ToDisplayString()));
                    continue;
                }

                applicationRequests.Add(request);
                var requiredRole = requirement?.ConstructorArguments.FirstOrDefault().Value as string;
                if (requirement is not null && !string.Equals(requiredRole, owner.RoleId, StringComparison.Ordinal))
                    diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.ConflictingExecutionRole,
                        SourceLocation(request, hostLocation), request.ToDisplayString(), requiredRole, owner.RoleId));

                if (!compilation.IsSymbolAccessibleWithin(request, compilation.Assembly))
                {
                    InvalidDeclaration(assembly, $"Operation request '{request.ToDisplayString()}' is not accessible to the application host.");
                    continue;
                }

                foreach (var (attribute, name) in ApplicationServiceSymbolHelper.GetLegacyEndpointAttributes(handler))
                    diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.LegacyHandlerAttribute,
                        attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? hostLocation,
                        handler.ToDisplayString(), name));

                EndpointModel? endpoint = null;
                if (endpointAttribute is not null)
                {
                    var analysis = EndpointModelFactory.Create(request, endpointAttribute, compilation.Assembly,
                        hostLocation, publishedOnly: false);
                    diagnostics.AddRange(analysis.Diagnostics);
                    endpoint = analysis.Value;
                    if (endpoint is null) continue;
                    EndpointModelFactory.TryGetUniqueResultContract(request, out var requestResult, out _);
                    if (!SymbolEqualityComparer.Default.Equals(handlerResult, requestResult))
                        diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.HandlerResultMismatch,
                            SourceLocation(handler, hostLocation), handler.ToDisplayString(), handlerResult.ToDisplayString(),
                            request.ToDisplayString(), requestResult.ToDisplayString()));
                    if (EndpointModelFactory.TryCreateDeleteQueryBindingWarning(endpoint, request) is { } warning)
                        diagnostics.Add(warning);
                }

                operations.Add(new ApplicationOperationModel(
                    request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), MetadataName(handler), assembly.Name,
                    owner.DomainId, owner.RoleId, requiredRole, endpoint, EndpointModelFactory.IsPublishedRequest(request) && endpoint is not null,
                    ApplicationServiceSymbolHelper.GetTags(handler).ToImmutableArray()));
            }
        }

        var localAssemblies = declarations.Values.Select(static declaration => declaration.AssemblyName)
            .OrderBy(static name => name, StringComparer.Ordinal).ToImmutableArray();
        var logicalAssemblies = declarations.Values.ToList();
        var dependencies = exportOwnAssembly
            ? ApplicationDependencyReader.Read(compilation, cancellationToken).ToList()
            : ApplicationCatalogMetadata.ReadDependencies(assemblies.SelectMany(static assembly =>
                ApplicationCatalogMetadata.ReadAttributes(assembly, ApplicationCatalogMetadata.EXPORT_KEY)), diagnostics).ToList();
        if (!exportOwnAssembly && declarations.ContainsKey(compilation.Assembly))
            dependencies.AddRange(ApplicationDependencyReader.Read(compilation, cancellationToken));
        if (!exportOwnAssembly)
        {
            var catalogPayloads = assemblies.SelectMany(static assembly => ApplicationCatalogMetadata.ReadAttributes(assembly,
                ApplicationCatalogMetadata.CATALOG_KEY)).ToArray();
            if (catalogPayloads.Length > 1)
                diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.InvalidApplicationCatalog, hostLocation,
                    "Reference exactly one product application catalog."));
            else if (catalogPayloads.Length == 1)
            {
                var catalogAnalysis = ApplicationCatalogMetadata.Read(compilation, catalogPayloads, validateCompleteness: false);
                diagnostics.AddRange(catalogAnalysis.Diagnostics);
                if (catalogAnalysis.Value is { } catalog)
                {
                    foreach (var declaration in declarations.Values)
                        if (!catalog.Assemblies.Any(candidate => candidate.Equals(declaration)))
                            InvalidDeclaration(compilation.Assembly, $"Local assembly '{declaration.AssemblyName}' does not match its product catalog ownership. Rebuild the catalog.");
                    foreach (var operation in operations.Where(static operation => operation.Published))
                        if (!catalog.Operations.Any(candidate => SameOwnership(candidate, operation)))
                            InvalidDeclaration(compilation.Assembly, $"Local operation '{operation.RequestTypeName}' does not match the product catalog. Rebuild the catalog.");
                    foreach (var operation in catalog.Operations)
                    {
                        var local = operations.FirstOrDefault(candidate => candidate.RequestTypeName == operation.RequestTypeName);
                        if (local is null)
                        {
                            if (localAssemblies.Contains(operation.HandlerAssemblyName))
                                diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.MissingApplicationOperation,
                                    hostLocation, operation.RequestTypeName));
                            operations.Add(operation);
                        }
                        foreach (var pair in handlersByRequest.Where(pair => pair.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == operation.RequestTypeName))
                        {
                            applicationRequests.Add(pair.Key);
                            foreach (var handler in pair.Value)
                                if (handler.ContainingAssembly.Name != operation.HandlerAssemblyName || MetadataName(handler) != operation.HandlerTypeName)
                                    InvalidDeclaration(handler.ContainingAssembly,
                                        $"Handler '{handler.ToDisplayString()}' conflicts with catalog ownership of '{operation.RequestTypeName}'.");
                        }
                    }
                    logicalAssemblies = catalog.Assemblies.ToList();
                    dependencies = catalog.Dependencies.ToList();
                }
            }
        }
        foreach (var pair in handlersByRequest.Where(pair => applicationRequests.Contains(pair.Key) && pair.Value.Count > 1))
            diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.DuplicateApplicationHandler,
                SourceLocation(pair.Key, hostLocation), pair.Key.ToDisplayString(),
                string.Join(", ", pair.Value.Select(static handler => handler.ToDisplayString()).OrderBy(static name => name, StringComparer.Ordinal))));

        if (!exportOwnAssembly)
        {
            ValidateApiCompleteness(assemblies, operations, diagnostics, hostLocation);
            ValidateBackgroundRoles(logicalAssemblies, message => InvalidDeclaration(compilation.Assembly, message));
        }
        ValidateDuplicates(operations, diagnostics, hostLocation);
        if (exportOwnAssembly)
        {
            logicalAssemblies = declarations.Where(pair => SymbolEqualityComparer.Default.Equals(pair.Key, compilation.Assembly))
                .Select(static pair => pair.Value).ToList();
            operations = operations.Where(static operation => operation.Published).ToList();
        }
        var model = diagnostics.Any(static diagnostic => diagnostic.ToDiagnostic().Severity == DiagnosticSeverity.Error)
            ? null : new ApplicationHostModel(
                logicalAssemblies.OrderBy(static declaration => declaration.AssemblyName, StringComparer.Ordinal).ToImmutableArray(),
                operations.OrderBy(static operation => operation.RequestTypeName, StringComparer.Ordinal).ToImmutableArray(), localAssemblies,
                ApplicationDependencyReader.Order(dependencies));
        return new AnalysisSnapshot<ApplicationHostModel>(model, diagnostics);

        void InvalidDeclaration(IAssemblySymbol assembly, string message) => diagnostics.Add(DiagnosticSnapshot.Create(
            DiagnosticDescriptors.InvalidApplicationDeclaration, hostLocation, assembly.Name, message));
    }

    internal static void ValidateApiCompleteness(IEnumerable<IAssemblySymbol> assemblies, IReadOnlyCollection<ApplicationOperationModel> operations,
        ICollection<DiagnosticSnapshot> diagnostics, Location location)
    {
        var requests = new HashSet<string>(operations.Select(static operation => operation.RequestTypeName), StringComparer.Ordinal);
        // Validate the protocol inventory itself: an entire missing API cannot be found by walking existing handlers.
        foreach (var assembly in assemblies.Where(ReferencesApplication))
        foreach (var request in HostGenerationReader.Types(assembly.GlobalNamespace))
        {
            if (EndpointModelFactory.IsPublishedRequest(request) && EndpointModelFactory.GetEndpointAttribute(request) is not null &&
                !requests.Contains(request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.MissingApplicationOperation,
                    location, request.ToDisplayString()));
        }
    }

    internal static void ValidateBackgroundRoles(IReadOnlyCollection<ApplicationAssemblyModel> assemblies, Action<string> invalid)
    {
        // Core producers cannot reference their role implementations. Resolve ownership only once the complete
        // product inventory is available from catalog inputs or the application host.
        foreach (var domain in assemblies.Where(static assembly => assembly.RoleId is null && assembly.BackgroundRole is not null))
        {
            if (!assemblies.Any(role => role.RoleId == domain.BackgroundRole && role.DomainId == domain.DomainId))
                invalid($"Domain '{domain.DomainId}' BackgroundRole '{domain.BackgroundRole}' must name a declared role in the same domain.");
        }
    }

    internal static string? GetDeclaredRoleDomain(IAssemblySymbol assembly)
    {
        var role = Find(assembly, GeneratorConstants.APPLICATION_ROLE_ATTRIBUTE);
        return role is { ConstructorArguments.Length: > 1 } ? NormalizeDomain(role.ConstructorArguments[1]) : null;
    }

    private static string? ValidateRegistration(AttributeData attribute, IAssemblySymbol assembly, bool domainWithoutBackgroundRole)
    {
        var argument = attribute.NamedArguments.FirstOrDefault(static argument => argument.Key == "Registration");
        var registration = argument.Value;
        if (argument.Key != "Registration" || registration.IsNull) return null;
        if (registration.Value is not INamedTypeSymbol type || type.TypeKind != TypeKind.Class || type.IsAbstract ||
            ContainsTypeParameters(type) || !InheritsApplicationRegistration(type) ||
            !type.InstanceConstructors.Any(static constructor => constructor.DeclaredAccessibility == Accessibility.Public &&
                constructor.Parameters.Length == 0))
            return "Registration must name a concrete closed ApplicationRegistration with a public parameterless constructor.";
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly))
            return "Registration must be declared in the same assembly as its ApplicationDomain or ApplicationRole attribute.";
        return domainWithoutBackgroundRole && OverridesActiveRegistration(type)
            ? "A domain registration overriding ConfigureActive must declare BackgroundRole to own that work."
            : null;
    }

    private static bool InheritsApplicationRegistration(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == GeneratorConstants.APPLICATION_REGISTRATION) return true;
        return false;
    }

    private static bool ContainsTypeParameters(ITypeSymbol type) => type is ITypeParameterSymbol ||
        type is IArrayTypeSymbol array && ContainsTypeParameters(array.ElementType) ||
        type is INamedTypeSymbol named && (named.IsUnboundGenericType || named.TypeArguments.Any(ContainsTypeParameters) ||
            named.ContainingType is { } parent && ContainsTypeParameters(parent));

    private static bool OverridesActiveRegistration(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        foreach (var method in current.GetMembers("ConfigureActive").OfType<IMethodSymbol>())
        {
            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                if (overridden.ContainingType.ToDisplayString() == GeneratorConstants.APPLICATION_REGISTRATION) return true;
        }
        return false;
    }

    internal static void ValidateDuplicates(IEnumerable<ApplicationOperationModel> operations,
        ICollection<DiagnosticSnapshot> diagnostics, Location location)
    {
        var endpoints = operations.Where(static operation => operation.Endpoint is not null)
            .Select(static operation => operation.Endpoint!).ToArray();
        foreach (var group in endpoints.GroupBy(static endpoint => new { endpoint.HttpMethod, endpoint.NormalizedRoute })
                     .Where(static group => group.Count() > 1))
            diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.DuplicateRoute, location,
                group.Key.HttpMethod.ToUpperInvariant(), group.First().CompleteRoute,
                string.Join(", ", group.Select(static endpoint => endpoint.RequestDisplayName))));
        foreach (var group in operations.Where(static operation => operation.Published)
                     .Select(static operation => operation.Endpoint!)
                     .GroupBy(static endpoint => new { endpoint.Domain, endpoint.RequestKind, endpoint.OperationName })
                     .Where(static group => group.Count() > 1))
            diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.DuplicateOperation, location,
                group.Key.OperationName, group.Key.Domain, group.Key.RequestKind,
                string.Join(", ", group.Select(static endpoint => endpoint.RequestDisplayName))));
    }

    private static bool ReferencesApplication(IAssemblySymbol assembly) => assembly.Modules.Any(static module =>
        module.ReferencedAssemblySymbols.Any(static reference => reference.Name == "Monica.Application"));

    private static bool SameOwnership(ApplicationOperationModel left, ApplicationOperationModel right) =>
        left.RequestTypeName == right.RequestTypeName && left.HandlerTypeName == right.HandlerTypeName &&
        left.HandlerAssemblyName == right.HandlerAssemblyName && left.DomainId == right.DomainId &&
        left.RoleId == right.RoleId && left.RequiredRole == right.RequiredRole;

    private static AttributeData? Find(ISymbol symbol, string metadataName) => symbol.GetAttributes()
        .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static Location SourceLocation(ISymbol symbol, Location fallback) =>
        symbol.Locations.FirstOrDefault(static location => location.IsInSource) ?? fallback;

    private static string MetadataName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? MetadataName(parent) + "+" + type.MetadataName
        : type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;

    private static string? NormalizeDomain(TypedConstant domain)
    {
        if (domain.Value is string value) return value;
        return domain.Type is { TypeKind: TypeKind.Enum } enumType
            ? enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, domain.Value))?.Name
            : null;
    }
}
