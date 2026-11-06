using Microsoft.CodeAnalysis;

namespace Monica.BuildingBlocks.CodeAnalysis.Services;

internal static class BuildingBlockSymbolFacts
{
    internal static string GetMetadataName(this INamedTypeSymbol symbol)
    {
        var typeName = symbol.MetadataName;
        if (symbol.ContainingType is not null)
        {
            return $"{symbol.ContainingType.GetMetadataName()}+{typeName}";
        }

        var namespaceName = symbol.ContainingNamespace?.ToDisplayString();
        return string.IsNullOrWhiteSpace(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    internal static string GetRuntimeName(this INamedTypeSymbol symbol)
    {
        var name = symbol.Name;
        if (symbol.ContainingType is not null)
        {
            return $"{symbol.ContainingType.GetRuntimeName()}+{name}";
        }

        var namespaceName = symbol.ContainingNamespace?.ToDisplayString();
        return string.IsNullOrWhiteSpace(namespaceName) ? name : $"{namespaceName}.{name}";
    }

    internal static bool DerivesFrom(this INamedTypeSymbol symbol, string metadataName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.OriginalDefinition.GetMetadataName(), metadataName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static INamedTypeSymbol? FindBaseType(this INamedTypeSymbol symbol, string metadataName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.OriginalDefinition.GetMetadataName(), metadataName, StringComparison.Ordinal))
            {
                return current;
            }
        }

        return null;
    }

    internal static INamedTypeSymbol? FindInterface(this INamedTypeSymbol symbol, string metadataName)
    {
        return symbol.AllInterfaces.FirstOrDefault(candidate => string.Equals(
            candidate.OriginalDefinition.GetMetadataName(),
            metadataName,
            StringComparison.Ordinal));
    }

    internal static AttributeData? FindAttribute(
        this INamedTypeSymbol symbol,
        string metadataName,
        bool inherit = false)
    {
        for (var current = symbol; current is not null; current = inherit ? current.BaseType : null)
        {
            var attribute = current.GetAttributes().FirstOrDefault(candidate => string.Equals(
                candidate.AttributeClass?.GetMetadataName(),
                metadataName,
                StringComparison.Ordinal));
            if (attribute is not null)
            {
                return attribute;
            }
        }

        return null;
    }

    internal static string? ReadString(this AttributeData attribute, int constructorIndex)
    {
        return constructorIndex < attribute.ConstructorArguments.Length
               && attribute.ConstructorArguments[constructorIndex].Value is string value
            ? Normalize(value)
            : null;
    }

    internal static string? ReadNamedString(this AttributeData attribute, string name)
    {
        return attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value is string value
            ? Normalize(value)
            : null;
    }

    internal static IReadOnlyList<string?> ReadNamedStrings(this AttributeData attribute, string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value;
        return value.Kind == TypedConstantKind.Array
            ? value.Values.Select(static item => item.Value as string).ToArray()
            : [];
    }

    internal static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
