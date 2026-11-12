using Microsoft.CodeAnalysis;
using Monica.BuildingBlocks.CodeAnalysis.Models;

namespace Monica.BuildingBlocks.CodeAnalysis.Services;

/// <summary>
/// Collects test classes from the declared types of one test project. A test class is a concrete,
/// non-generic class owning at least one Fact/Theory test method. Class and method declarations remain
/// separate so consumers can apply inheritance without inventing cross-method relationships.
/// </summary>
internal static class BuildingBlockTestClassScanner
{
    private const string TRAIT_ATTRIBUTE = "TraitAttribute";
    private const string TRAIT_INVALID_CODE = "BuildingBlock.Tests.Trait.Invalid";
    private static readonly HashSet<string> TestMethodAttributeNames = ["FactAttribute", "TheoryAttribute"];

    internal sealed record TestClassCandidate(
        INamedTypeSymbol Symbol,
        int TestMethodCount,
        IReadOnlyList<BuildingBlockSourceTestTrait> Traits,
        IReadOnlyList<TestMethodCandidate> Methods,
        IReadOnlyList<BuildingBlockSourceDiagnostic> Diagnostics);

    internal sealed record TestMethodCandidate(IMethodSymbol Symbol, IReadOnlyList<BuildingBlockSourceTestTrait> Traits);

    internal static IEnumerable<TestClassCandidate> Scan(IEnumerable<INamedTypeSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol is not { TypeKind: TypeKind.Class, IsAbstract: false, Arity: 0 })
            {
                continue;
            }

            var testMethods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(IsTestMethod)
                .ToArray();
            if (testMethods.Length == 0)
            {
                continue;
            }

            var traits = new Dictionary<(string Key, string Value), BuildingBlockSourceTestTrait>();
            var diagnostics = new List<BuildingBlockSourceDiagnostic>();
            CollectTraits(symbol, symbol.GetAttributes(), traits, diagnostics);
            var methods = new List<TestMethodCandidate>();
            foreach (var method in testMethods)
            {
                var methodTraits = new Dictionary<(string Key, string Value), BuildingBlockSourceTestTrait>();
                CollectTraits(symbol, method.GetAttributes(), methodTraits, diagnostics);
                methods.Add(new TestMethodCandidate(method, methodTraits.Values
                    .OrderBy(static trait => trait.Key, StringComparer.Ordinal)
                    .ThenBy(static trait => trait.Value, StringComparer.Ordinal).ToArray()));
            }

            yield return new TestClassCandidate(
                symbol,
                testMethods.Length,
                traits.Values
                    .OrderBy(static trait => trait.Key, StringComparer.Ordinal)
                    .ThenBy(static trait => trait.Value, StringComparer.Ordinal)
                    .ToArray(),
                methods,
                diagnostics);
        }
    }

    private static bool IsTestMethod(IMethodSymbol method)
        => method.GetAttributes().Any(attribute =>
            attribute.AttributeClass is { } attributeClass
            && TestMethodAttributeNames.Contains(attributeClass.Name));

    private static void CollectTraits(
        INamedTypeSymbol owner,
        IEnumerable<AttributeData> attributes,
        IDictionary<(string Key, string Value), BuildingBlockSourceTestTrait> traits,
        ICollection<BuildingBlockSourceDiagnostic> diagnostics)
    {
        foreach (var attribute in attributes.Where(static attribute =>
                     attribute.AttributeClass is { Name: TRAIT_ATTRIBUTE }))
        {
            if (attribute.ConstructorArguments is not [{ Value: string key }, { Value: string value }])
            {
                diagnostics.Add(InvalidTraitDiagnostic(owner));
                continue;
            }

            var normalizedKey = key.Trim();
            var normalizedValue = value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
            {
                diagnostics.Add(InvalidTraitDiagnostic(owner));
                continue;
            }

            var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
            var span = syntax?.GetLocation().GetLineSpan();
            traits[(normalizedKey, normalizedValue)] = new BuildingBlockSourceTestTrait(normalizedKey, normalizedValue)
            {
                Source = span is { } location
                    ? new BuildingBlockSourceLocation(location.Path, location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1)
                    : null
            };
        }
    }

    private static BuildingBlockSourceDiagnostic InvalidTraitDiagnostic(INamedTypeSymbol owner)
        => new(
            TRAIT_INVALID_CODE,
            BuildingBlockSourceDiagnosticSeverity.Warning,
            $"Test trait attributes on '{owner.GetRuntimeName()}' must declare constant, non-empty key and value strings.",
            null,
            null,
            null);
}
