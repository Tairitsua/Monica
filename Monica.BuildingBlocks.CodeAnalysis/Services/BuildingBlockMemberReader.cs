using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Monica.BuildingBlocks.CodeAnalysis.Models;

namespace Monica.BuildingBlocks.CodeAnalysis.Services;

/// <summary>Reads declared shape without loading application assemblies or inferring persistence conventions.</summary>
internal static class BuildingBlockMemberReader
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static IReadOnlyList<BuildingBlockSourceProperty> ReadProperties(INamedTypeSymbol symbol, string root)
    {
        var properties = new List<BuildingBlockSourceProperty>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var owner = symbol; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
        {
            foreach (var property in owner.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || !names.Add(property.Name)) continue;
                var nullable = property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    ? true : property.Type.IsValueType ? false
                    : property.NullableAnnotation == NullableAnnotation.None ? (bool?)null
                    : property.NullableAnnotation == NullableAnnotation.Annotated;
                properties.Add(new BuildingBlockSourceProperty(property.Name, property.Type.ToDisplayString(TypeFormat),
                    nullable, property.IsRequired, property.SetMethod is null ? null
                        : $"{property.SetMethod.DeclaredAccessibility.ToString().ToLowerInvariant()} {(property.SetMethod.IsInitOnly ? "init" : "set")}",
                    owner.ToDisplayString(), !SymbolEqualityComparer.Default.Equals(owner, symbol), ReadSummary(property),
                    property.GetAttributes().Select(attribute => attribute.ToString()).OfType<string>().Order(StringComparer.Ordinal).ToArray(),
                    ReadLocation(property, root)));
            }
        }
        return properties;
    }

    internal static IReadOnlyList<BuildingBlockSourceOperation> ReadOperations(INamedTypeSymbol symbol, string root)
        => symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(method => !method.IsImplicitlyDeclared
                && method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                && method.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
            .Select(method => new BuildingBlockSourceOperation(method.MethodKind == MethodKind.Constructor ? symbol.Name : method.Name,
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), ReadSummary(method), ReadLocation(method, root)))
            .ToArray();

    private static BuildingBlockSourceLocation? ReadLocation(ISymbol symbol, string root)
    {
        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (location is null) return null;
        var span = location.GetLineSpan();
        var relative = Path.GetRelativePath(root, span.Path).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null : new BuildingBlockSourceLocation(relative, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static string? ReadSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null) return null;
            foreach (var reference in summary.Descendants().Where(element => element.Name.LocalName is "see" or "paramref").ToArray())
                reference.ReplaceWith(new XText(reference.Attribute("cref")?.Value ?? reference.Attribute("name")?.Value ?? reference.Value));
            var text = string.Join(' ', summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 ? null : text;
        }
        catch (System.Xml.XmlException) { return null; }
    }
}
