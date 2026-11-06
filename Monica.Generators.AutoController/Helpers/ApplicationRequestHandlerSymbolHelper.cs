using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Constants;

namespace Monica.Generators.AutoController.Helpers;

internal static class ApplicationRequestHandlerSymbolHelper
{
    private static readonly HashSet<string> LegacyEndpointAttributeNames = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Mvc.RouteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPostAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPutAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPatchAttribute",
        "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpHeadAttribute",
        "Microsoft.AspNetCore.Mvc.HttpOptionsAttribute",
        "Microsoft.AspNetCore.Mvc.AcceptVerbsAttribute",
        "Microsoft.AspNetCore.Mvc.FromBodyAttribute",
        "Microsoft.AspNetCore.Mvc.FromQueryAttribute",
        "Microsoft.AspNetCore.Mvc.FromFormAttribute",
        "Microsoft.AspNetCore.Mvc.FromRouteAttribute"
    };

    public static bool TryGetContract(
        INamedTypeSymbol? handler,
        out INamedTypeSymbol request,
        out ITypeSymbol result)
    {
        for (var current = handler; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;
            if (definition.Name == "ApplicationRequestHandler" &&
                definition.Arity == 2 &&
                definition.ContainingNamespace.ToDisplayString() == GeneratorConstants.APPLICATION_ABSTRACTIONS_NAMESPACE &&
                current.TypeArguments[0] is INamedTypeSymbol requestType)
            {
                request = requestType;
                result = current.TypeArguments[1];
                return true;
            }
        }

        request = null!;
        result = null!;
        return false;
    }

    public static IEnumerable<(AttributeData Attribute, string Name)> GetLegacyEndpointAttributes(
        INamedTypeSymbol handler)
    {
        foreach (var attribute in handler.GetAttributes())
        {
            if (TryGetLegacyAttributeName(attribute, out var name))
            {
                yield return (attribute, name);
            }
        }

        foreach (var method in handler.GetMembers().OfType<IMethodSymbol>())
        {
            foreach (var attribute in method.GetAttributes())
            {
                if (TryGetLegacyAttributeName(attribute, out var name))
                {
                    yield return (attribute, name);
                }
            }

            foreach (var parameter in method.Parameters)
            {
                foreach (var attribute in parameter.GetAttributes())
                {
                    if (TryGetLegacyAttributeName(attribute, out var name))
                    {
                        yield return (attribute, name);
                    }
                }
            }
        }
    }

    public static IReadOnlyList<string> GetTags(INamedTypeSymbol handler)
    {
        var tags = new List<string>();
        foreach (var attribute in handler.GetAttributes().Where(static attributeData =>
                     attributeData.AttributeClass?.Name == "TagsAttribute"))
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Kind == TypedConstantKind.Array)
                {
                    tags.AddRange(argument.Values
                        .Select(static value => value.Value as string)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))!);
                }
                else if (argument.Value is string value && !string.IsNullOrWhiteSpace(value))
                {
                    tags.Add(value);
                }
            }
        }

        return tags.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool TryGetLegacyAttributeName(AttributeData attribute, out string name)
    {
        var fullName = attribute.AttributeClass?.ToDisplayString();
        if (fullName is not null && LegacyEndpointAttributeNames.Contains(fullName))
        {
            name = attribute.AttributeClass!.Name;
            return true;
        }

        name = string.Empty;
        return false;
    }
}
