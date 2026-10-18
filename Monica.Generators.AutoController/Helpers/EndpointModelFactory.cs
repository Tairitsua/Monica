using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController.Constants;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Helpers;

internal static class EndpointModelFactory
{
    public static bool IsPublishedRequest(INamedTypeSymbol request) => ResolveNamespace(request).IsValidPublishedNamespace;

    private const string REMOTE_RESULT_ENVELOPE_INTERFACE_NAMESPACE = "Monica.Core.Results.Abstractions";
    private const string REMOTE_RESULT_ENVELOPE_INTERFACE_NAME = "IRemoteResultEnvelope`1";

    private static readonly Regex SummaryPattern = new(
        "<summary>(?<content>.*?)</summary>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex RoutePlaceholderPattern = new(
        @"\{(?:\*{1,2})?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AnalysisSnapshot<EndpointModel> Create(
        INamedTypeSymbol request,
        AttributeData endpointAttribute,
        IAssemblySymbol currentAssembly,
        Location fallbackLocation,
        bool publishedOnly)
    {
        var location = endpointAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                       ?? request.Locations.FirstOrDefault(static candidate => candidate.IsInSource)
                       ?? fallbackLocation;
        var requestDisplayName = request.ToDisplayString();
        var namespaceInfo = ResolveNamespace(request);
        if (!namespaceInfo.ContainsPublishedLanguages)
        {
            if (publishedOnly)
            {
                return new AnalysisSnapshot<EndpointModel>(null);
            }
        }
        else if (!namespaceInfo.IsValidPublishedNamespace)
        {
            return Failure(
                DiagnosticDescriptors.InvalidPublishedNamespace,
                location,
                requestDisplayName);
        }

        var configResult = GeneratorConfigurationReader.Read(request.ContainingAssembly);
        if (configResult.Missing)
        {
            configResult = GeneratorConfigReadResult.Valid(new GeneratorConfig("api/v1", null, 0, null, null));
        }

        if (configResult.Error is not null || configResult.Config is null)
        {
            return Failure(
                DiagnosticDescriptors.InvalidConfiguration,
                location,
                request.ContainingAssembly.Name,
                configResult.Error ?? "Unknown configuration error.");
        }

        var config = configResult.Config;
        var domain = namespaceInfo.IsValidPublishedNamespace
            ? namespaceInfo.Domain
            : config.DomainName;
        if (string.IsNullOrWhiteSpace(domain))
        {
            return Failure(
                DiagnosticDescriptors.InvalidConfiguration,
                location,
                request.ContainingAssembly.Name,
                "DomainName is required for service-local endpoint requests.");
        }

        if (namespaceInfo.IsValidPublishedNamespace &&
            !string.IsNullOrWhiteSpace(config.DomainName) &&
            !string.Equals(config.DomainName, domain, StringComparison.Ordinal))
        {
            return Failure(
                DiagnosticDescriptors.InvalidConfiguration,
                location,
                request.ContainingAssembly.Name,
                $"DomainName '{config.DomainName}' does not match published namespace domain '{domain}'.");
        }

        var requestKind = ResolveRequestKind(request.Name);
        if (requestKind is null)
        {
            return Failure(
                DiagnosticDescriptors.InvalidRequestName,
                location,
                requestDisplayName);
        }

        if (!TryGetUniqueResultContract(request, out var resultSymbol, out var resultContractCount))
        {
            return Failure(
                DiagnosticDescriptors.InvalidResultContract,
                location,
                requestDisplayName,
                resultContractCount);
        }

        if (!TryReadEndpointAttribute(
                endpointAttribute,
                out var method,
                out var route,
                out var binding))
        {
            return Failure(
                DiagnosticDescriptors.InvalidRoute,
                location,
                requestDisplayName,
                route ?? string.Empty,
                "The attribute constructor or enum values are invalid.");
        }

        if (!ValidateRoute(route!, out var routeError))
        {
            return Failure(
                DiagnosticDescriptors.InvalidRoute,
                location,
                requestDisplayName,
                route!,
                routeError);
        }

        var routeDiagnostics = ValidateRouteProperties(request, route!, location);
        if (routeDiagnostics.Count > 0)
        {
            return new AnalysisSnapshot<EndpointModel>(null, routeDiagnostics);
        }

        var resolvedOperationName = request.Name.Substring(requestKind.Length);
        if (!SyntaxFacts.IsValidIdentifier(resolvedOperationName))
        {
            return Failure(
                DiagnosticDescriptors.InvalidOperationName,
                location,
                requestDisplayName,
                resolvedOperationName);
        }

        var resolvedBinding = ResolveBinding(binding, method!);
        var isPublished = namespaceInfo.IsValidPublishedNamespace;
        if (isPublished && resolvedBinding == "Form")
        {
            return Failure(
                DiagnosticDescriptors.UnsupportedPublishedBinding,
                location,
                requestDisplayName,
                resolvedBinding);
        }

        if (isPublished && resultSymbol.SpecialType == SpecialType.System_Object)
        {
            return Failure(
                DiagnosticDescriptors.UnsupportedPublishedResult,
                location,
                requestDisplayName);
        }

        if (isPublished && !IsConcreteRemoteResultEnvelope(resultSymbol))
        {
            return Failure(
                DiagnosticDescriptors.InvalidPublishedResultEnvelope,
                location,
                requestDisplayName,
                resultSymbol.ToDisplayString());
        }

        var validateSummary = SymbolEqualityComparer.Default.Equals(request.ContainingAssembly, currentAssembly);
        if (!TryGetDocumentationComment(
                request,
                validateSummary,
                location,
                out var documentationComment,
                out var documentationDiagnostic))
        {
            return new AnalysisSnapshot<EndpointModel>(null, new[] { documentationDiagnostic! });
        }

        return new AnalysisSnapshot<EndpointModel>(new EndpointModel(
            requestDisplayName,
            request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            resultSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            domain!,
            namespaceInfo.ContractNamespaceRoot ?? request.ContainingNamespace.ToDisplayString(),
            config.RoutePrefix,
            route!,
            requestKind,
            resolvedOperationName,
            method!,
            resolvedBinding,
            documentationComment!,
            NormalizeRoute($"{config.RoutePrefix}/{domain}/{route}"),
            SourceLocationSnapshot.Create(location),
            config));
    }

    public static DiagnosticSnapshot? TryCreateDeleteQueryBindingWarning(
        EndpointModel endpoint,
        INamedTypeSymbol request)
    {
        if (endpoint.HttpMethod != "Delete" || endpoint.Binding != "Query")
        {
            return null;
        }

        for (var current = request; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public ||
                    IsQueryFriendly(property.Type))
                {
                    continue;
                }

                return DiagnosticSnapshot.Create(
                    DiagnosticDescriptors.DeleteQueryBindingUnfit,
                    request.Locations.FirstOrDefault(static candidate => candidate.IsInSource),
                    endpoint.RequestDisplayName,
                    property.Name);
            }
        }

        return null;
    }

    private static bool IsQueryFriendly(ITypeSymbol type)
    {
        return type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum;
    }

    public static AttributeData? GetEndpointAttribute(INamedTypeSymbol request)
    {
        return request.GetAttributes().FirstOrDefault(static attributeData =>
            attributeData.AttributeClass?.ToDisplayString() == GeneratorConstants.API_ENDPOINT_ATTRIBUTE);
    }

    public static bool TryGetUniqueResultContract(
        INamedTypeSymbol request,
        out ITypeSymbol result,
        out int contractCount)
    {
        var resultContracts = request.AllInterfaces
            .Where(static interfaceSymbol =>
                interfaceSymbol.OriginalDefinition.MetadataName == "IRequest`1" &&
                interfaceSymbol.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Monica.Core.Mediator")
            .ToArray();
        contractCount = resultContracts.Length;
        if (contractCount == 1)
        {
            result = resultContracts[0].TypeArguments[0];
            return true;
        }

        result = null!;
        return false;
    }

    private static AnalysisSnapshot<EndpointModel> Failure(
        DiagnosticDescriptor descriptor,
        Location location,
        params object?[] arguments)
    {
        return new AnalysisSnapshot<EndpointModel>(
            null,
            new[] { DiagnosticSnapshot.Create(descriptor, location, arguments) });
    }

    private static (bool ContainsPublishedLanguages, bool IsValidPublishedNamespace, string? Domain, string? ContractNamespaceRoot)
        ResolveNamespace(INamedTypeSymbol request)
    {
        var segments = request.ContainingNamespace.ToDisplayString()
            .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        var publishedIndex = Array.IndexOf(segments, "PublishedLanguages");
        if (publishedIndex < 0)
        {
            return (false, false, null, null);
        }

        if (segments.Length != publishedIndex + 3 ||
            !segments[publishedIndex + 1].StartsWith("Domain", StringComparison.Ordinal) ||
            segments[publishedIndex + 1].Length == "Domain".Length ||
            segments[publishedIndex + 2] != "Requests")
        {
            return (true, false, null, null);
        }

        var domain = segments[publishedIndex + 1].Substring("Domain".Length);
        return (
            true,
            true,
            domain,
            string.Join(".", segments.Take(publishedIndex + 2)));
    }

    private static string? ResolveRequestKind(string requestName)
    {
        if (requestName.StartsWith("Command", StringComparison.Ordinal) && requestName.Length > "Command".Length)
        {
            return "Command";
        }

        if (requestName.StartsWith("Query", StringComparison.Ordinal) && requestName.Length > "Query".Length)
        {
            return "Query";
        }

        return null;
    }

    private static bool TryReadEndpointAttribute(
        AttributeData attribute,
        out string? method,
        out string? route,
        out int binding)
    {
        method = null;
        route = attribute.ConstructorArguments.Length > 1
            ? attribute.ConstructorArguments[1].Value as string
            : null;
        binding = GeneratorConstants.BINDING_AUTO;

        if (attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not int methodValue)
        {
            return false;
        }

        method = methodValue switch
        {
            GeneratorConstants.METHOD_GET => "Get",
            GeneratorConstants.METHOD_POST => "Post",
            GeneratorConstants.METHOD_PUT => "Put",
            GeneratorConstants.METHOD_PATCH => "Patch",
            GeneratorConstants.METHOD_DELETE => "Delete",
            _ => null
        };

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "Binding" && namedArgument.Value.Value is int bindingValue)
            {
                binding = bindingValue;
            }
        }

        return method is not null &&
               route is not null &&
               binding is >= GeneratorConstants.BINDING_AUTO and <= GeneratorConstants.BINDING_FORM;
    }

    private static string ResolveBinding(int binding, string method)
    {
        return binding switch
        {
            GeneratorConstants.BINDING_QUERY => "Query",
            GeneratorConstants.BINDING_BODY => "Body",
            GeneratorConstants.BINDING_FORM => "Form",
            _ when method is "Get" or "Delete" => "Query",
            _ => "Body"
        };
    }

    private static bool ValidateRoute(string route, out string error)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            error = "The route cannot be empty.";
            return false;
        }

        if (route != route.Trim() ||
            route.StartsWith("/", StringComparison.Ordinal) ||
            route.EndsWith("/", StringComparison.Ordinal) ||
            route.Contains("//"))
        {
            error = "The route must be relative and cannot contain leading, trailing, or duplicate slashes.";
            return false;
        }

        var withoutPlaceholders = RoutePlaceholderPattern.Replace(route, string.Empty);
        if (withoutPlaceholders.Contains("{") || withoutPlaceholders.Contains("}"))
        {
            error = "The route contains a malformed placeholder.";
            return false;
        }

        if (withoutPlaceholders.IndexOfAny(new[] { '<', '>', '"', '\'', '\\', '\n', '\r', '\t', '?', '#' }) >= 0)
        {
            error = "The route contains invalid characters.";
            return false;
        }

        foreach (Match placeholder in RoutePlaceholderPattern.Matches(route))
        {
            if (!TryValidatePlaceholderSyntax(placeholder, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string NormalizeRoute(string route)
    {
        var builder = new StringBuilder(route.Length);
        var literalStart = 0;
        foreach (Match match in RoutePlaceholderPattern.Matches(route))
        {
            builder.Append(route.Substring(literalStart, match.Index - literalStart).ToLowerInvariant());

            var placeholder = match.Value;
            var nameGroup = match.Groups["name"];
            var relativeNameStart = nameGroup.Index - match.Index;
            builder.Append(placeholder.Substring(0, relativeNameStart));
            builder.Append('_');
            builder.Append(placeholder.Substring(relativeNameStart + nameGroup.Length));
            literalStart = match.Index + match.Length;
        }

        builder.Append(route.Substring(literalStart).ToLowerInvariant());
        return builder.ToString();
    }

    private static IReadOnlyList<DiagnosticSnapshot> ValidateRouteProperties(
        INamedTypeSymbol request,
        string route,
        Location location)
    {
        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);
        for (var current = request; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsStatic && property.DeclaredAccessibility == Accessibility.Public &&
                    property.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
                    !properties.ContainsKey(property.Name))
                {
                    properties.Add(property.Name, property);
                }
            }
        }

        var diagnostics = new List<DiagnosticSnapshot>();
        foreach (Match match in RoutePlaceholderPattern.Matches(route))
        {
            var placeholderName = match.Groups["name"].Value;
            if (!properties.TryGetValue(placeholderName, out var property))
            {
                diagnostics.Add(DiagnosticSnapshot.Create(
                    DiagnosticDescriptors.MissingRouteProperty,
                    location,
                    placeholderName,
                    request.ToDisplayString()));
                continue;
            }

            if (!CanReceiveRouteValue(request, property))
            {
                diagnostics.Add(DiagnosticSnapshot.Create(
                    DiagnosticDescriptors.UnbindableRouteProperty,
                    location,
                    placeholderName,
                    property.Name,
                    request.ToDisplayString()));
            }
        }

        return diagnostics;
    }

    private static bool CanReceiveRouteValue(INamedTypeSymbol request, IPropertySymbol property)
    {
        if (property.SetMethod is
            {
                IsStatic: false,
                DeclaredAccessibility: Accessibility.Public
            })
        {
            return true;
        }

        return request.IsRecord && request.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Any(parameter =>
                string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
                SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)));
    }

    private static bool TryValidatePlaceholderSyntax(Match placeholder, out string error)
    {
        var name = placeholder.Groups["name"];
        var suffixStart = name.Index - placeholder.Index + name.Length;
        var suffix = placeholder.Value.Substring(suffixStart, placeholder.Length - suffixStart - 1);
        var optionalMarker = suffix.IndexOf('?');
        if (optionalMarker >= 0 &&
            (optionalMarker != suffix.Length - 1 || suffix.LastIndexOf('?') != optionalMarker))
        {
            error = $"Route placeholder '{placeholder.Value}' contains '?' outside the optional suffix.";
            return false;
        }

        if (optionalMarker >= 0 && suffix.IndexOf('=') >= 0)
        {
            error = $"Route placeholder '{placeholder.Value}' cannot combine an optional marker with a default value.";
            return false;
        }

        if (suffix.IndexOf('#') >= 0)
        {
            error = $"Route placeholder '{placeholder.Value}' contains invalid character '#'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsConcreteRemoteResultEnvelope(ITypeSymbol result)
    {
        return result is INamedTypeSymbol
               {
                   TypeKind: TypeKind.Class,
                   IsAbstract: false,
                   IsReferenceType: true
               } namedResult &&
               namedResult.AllInterfaces.Any(interfaceSymbol =>
                   interfaceSymbol.OriginalDefinition.MetadataName == REMOTE_RESULT_ENVELOPE_INTERFACE_NAME &&
                   interfaceSymbol.OriginalDefinition.ContainingNamespace.ToDisplayString() ==
                   REMOTE_RESULT_ENVELOPE_INTERFACE_NAMESPACE &&
                   interfaceSymbol.TypeArguments.Length == 1 &&
                   SymbolEqualityComparer.Default.Equals(interfaceSymbol.TypeArguments[0], namedResult));
    }

    private static bool TryGetDocumentationComment(
        INamedTypeSymbol request,
        bool validateSummary,
        Location location,
        out string? documentationComment,
        out DiagnosticSnapshot? diagnostic)
    {
        var xml = request.GetDocumentationCommentXml(expandIncludes: true);
        var summaryMatch = SummaryPattern.Match(xml ?? string.Empty);
        if (!summaryMatch.Success || string.IsNullOrWhiteSpace(summaryMatch.Groups["content"].Value))
        {
            if (validateSummary)
            {
                documentationComment = null;
                diagnostic = DiagnosticSnapshot.Create(
                    DiagnosticDescriptors.MissingSummary,
                    location,
                    request.ToDisplayString());
                return false;
            }

            documentationComment =
                $"/// <inheritdoc cref=\"{request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}\"/>";
            diagnostic = null;
            return true;
        }

        var contentLines = summaryMatch.Groups["content"].Value
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("/// <summary>");
        foreach (var line in contentLines)
        {
            builder.Append("/// ").AppendLine(line);
        }

        builder.Append("/// </summary>");
        documentationComment = builder.ToString();
        diagnostic = null;
        return true;
    }
}
