using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController.Constants;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Helpers;

internal static class GeneratorConfigurationReader
{
    private const string CACHED_SERVICE_PROVIDER =
        "Monica.DependencyInjection.Abstractions.ICachedServiceProvider";
    private const string HTTP_CLIENT = "System.Net.Http.HttpClient";

    public static GeneratorConfigReadResult Read(IAssemblySymbol assembly)
    {
        var attribute = assembly.GetAttributes().FirstOrDefault(static attributeData =>
            attributeData.AttributeClass?.ToDisplayString() == GeneratorConstants.HTTP_CONTRACT_ATTRIBUTE)
            ?? assembly.GetAttributes().FirstOrDefault(static attributeData =>
                attributeData.AttributeClass?.ToDisplayString() == GeneratorConstants.WEB_API_CONFIG_ATTRIBUTE);
        if (attribute is null)
        {
            return GeneratorConfigReadResult.MissingConfiguration;
        }

        var routePrefix = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;
        if (!IsValidRoutePrefix(routePrefix))
        {
            return GeneratorConfigReadResult.Invalid(
                "RoutePrefix must be a non-empty literal relative route without leading or trailing slashes, placeholders, query strings, or fragments.");
        }

        string? domainName = null;
        var targets = 0;
        INamedTypeSymbol? httpClientBaseType = null;
        INamedTypeSymbol? localClientBaseType = null;

        foreach (var namedArgument in attribute.NamedArguments)
        {
            switch (namedArgument.Key)
            {
                case "DomainName":
                    domainName = namedArgument.Value.Value as string;
                    break;
                case "ApiClientTargets":
                    targets = namedArgument.Value.Value is int value ? value : 0;
                    break;
                case "HttpClientBaseType":
                    httpClientBaseType = namedArgument.Value.Value as INamedTypeSymbol;
                    break;
                case "LocalClientBaseType":
                    localClientBaseType = namedArgument.Value.Value as INamedTypeSymbol;
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(domainName) && !SyntaxFacts.IsValidIdentifier(domainName))
        {
            return GeneratorConfigReadResult.Invalid(
                $"DomainName '{domainName}' must be a valid C# identifier.");
        }

        if ((targets & ~3) != 0)
        {
            return GeneratorConfigReadResult.Invalid(
                $"ApiClientTargets contains unsupported flag value '{targets}'.");
        }

        if (httpClientBaseType is not null &&
            !TryValidateClientBaseType(
                httpClientBaseType,
                assembly,
                "Monica.Invocation.Abstractions.HttpApiClient",
                new[] { CACHED_SERVICE_PROVIDER, HTTP_CLIENT },
                out var httpBaseError))
        {
            return GeneratorConfigReadResult.Invalid(
                $"HttpClientBaseType '{httpClientBaseType.ToDisplayString()}' is invalid: {httpBaseError}");
        }

        if (localClientBaseType is not null &&
            !TryValidateClientBaseType(
                localClientBaseType,
                assembly,
                "Monica.Invocation.Abstractions.LocalApiClient",
                new[] { CACHED_SERVICE_PROVIDER },
                out var localBaseError))
        {
            return GeneratorConfigReadResult.Invalid(
                $"LocalClientBaseType '{localClientBaseType.ToDisplayString()}' is invalid: {localBaseError}");
        }

        return GeneratorConfigReadResult.Valid(new GeneratorConfig(
            routePrefix!,
            domainName,
            targets,
            httpClientBaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            localClientBaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

    private static bool IsValidRoutePrefix(string? routePrefix)
    {
        return !string.IsNullOrWhiteSpace(routePrefix) &&
               routePrefix == routePrefix!.Trim() &&
               !routePrefix.StartsWith("/", StringComparison.Ordinal) &&
               !routePrefix.EndsWith("/", StringComparison.Ordinal) &&
               !routePrefix.Contains("//") &&
               routePrefix.IndexOfAny(new[] { '<', '>', '"', '\'', '\\', '\n', '\r', '\t', '{', '}', '?', '#' }) < 0;
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string baseTypeName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseTypeName)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryValidateClientBaseType(
        INamedTypeSymbol type,
        IAssemblySymbol generatedAssembly,
        string requiredBaseType,
        IReadOnlyList<string> constructorParameterTypes,
        out string error)
    {
        if (!InheritsFrom(type, requiredBaseType))
        {
            error = $"the type must inherit '{requiredBaseType}'.";
            return false;
        }

        if (type.IsSealed)
        {
            error = "the type cannot be sealed because the generated client derives from it.";
            return false;
        }

        if (IsGeneric(type))
        {
            error = "the type and its containing types must be closed, non-generic types.";
            return false;
        }

        if (!IsTypeAccessible(type, generatedAssembly))
        {
            error = "the type is not accessible from generated code in the configured assembly.";
            return false;
        }

        var hasExpectedConstructor = type.InstanceConstructors.Any(constructor =>
            IsConstructorAccessible(constructor, generatedAssembly) &&
            constructor.Parameters.Length == constructorParameterTypes.Count &&
            constructor.Parameters.All(static parameter => parameter.RefKind == RefKind.None) &&
            constructor.Parameters.Select(static parameter => parameter.Type.ToDisplayString())
                .SequenceEqual(constructorParameterTypes, StringComparer.Ordinal));
        if (!hasExpectedConstructor)
        {
            error =
                $"the type must expose an accessible constructor with parameters ({string.Join(", ", constructorParameterTypes)}).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0 || current.IsUnboundGenericType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTypeAccessible(INamedTypeSymbol type, IAssemblySymbol generatedAssembly)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal)
            {
                return false;
            }

            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    continue;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    if (HasInternalAccess(current.ContainingAssembly, generatedAssembly))
                    {
                        continue;
                    }

                    break;
            }

            return false;
        }

        return true;
    }

    private static bool IsConstructorAccessible(
        IMethodSymbol constructor,
        IAssemblySymbol generatedAssembly)
    {
        return constructor.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => HasInternalAccess(constructor.ContainingAssembly, generatedAssembly),
            Accessibility.ProtectedAndInternal =>
                HasInternalAccess(constructor.ContainingAssembly, generatedAssembly),
            _ => false
        };
    }

    private static bool HasInternalAccess(IAssemblySymbol declaringAssembly, IAssemblySymbol generatedAssembly)
    {
        return SymbolEqualityComparer.Default.Equals(declaringAssembly, generatedAssembly) ||
               declaringAssembly.GivesAccessTo(generatedAssembly);
    }
}

internal sealed class GeneratorConfigReadResult
{
    private GeneratorConfigReadResult(GeneratorConfig? config, string? error, bool missing)
    {
        Config = config;
        Error = error;
        Missing = missing;
    }

    public static GeneratorConfigReadResult MissingConfiguration { get; } = new(null, null, true);

    public GeneratorConfig? Config { get; }

    public string? Error { get; }

    public bool Missing { get; }

    public static GeneratorConfigReadResult Valid(GeneratorConfig config)
    {
        return new GeneratorConfigReadResult(config, null, false);
    }

    public static GeneratorConfigReadResult Invalid(string error)
    {
        return new GeneratorConfigReadResult(null, error, false);
    }
}
