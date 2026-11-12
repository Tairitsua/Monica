using System.Reflection;
using Monica.Core.XmlDocumentation.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Resolves building-block method metadata and optional XML documentation for one host.
/// </summary>
internal sealed class BuildingBlockDocumentationResolver(IXmlDocumentationService? xmlDocumentationService)
{
    internal List<BuildingBlockMethod> GetPublicMethods(Type type, Type? baseType = null)
    {
        return GetMethods(type, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, baseType);
    }

    internal string? ExtractTypeDescription(Type type)
    {
        return xmlDocumentationService?.GetTypeDocumentation(type);
    }

    internal string? ExtractMethodDescription(MethodInfo method)
    {
        return xmlDocumentationService?.GetMethodDocumentation(method)?.Summary;
    }

    private List<BuildingBlockMethod> GetMethods(Type type, BindingFlags bindingFlags, Type? baseType)
    {
        var methods = type.GetMethods(bindingFlags)
            .Where(method => !method.IsSpecialName && method.DeclaringType != typeof(object));

        if (baseType is not null)
        {
            methods = methods.Where(method => method.DeclaringType != baseType);
        }

        return
        [
            .. methods.Select(method => new BuildingBlockMethod
            {
                MethodInfo = method,
                Description = xmlDocumentationService?.GetMethodDocumentation(method)?.Summary
            })
        ];
    }
}
