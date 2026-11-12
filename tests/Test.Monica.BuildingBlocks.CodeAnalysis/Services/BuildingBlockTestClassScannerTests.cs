using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.BuildingBlocks.CodeAnalysis.Models;
using Monica.BuildingBlocks.CodeAnalysis.Services;
using Xunit;

namespace Test.Monica.BuildingBlocks.CodeAnalysis.Services;

public sealed class BuildingBlockTestClassScannerTests
{
    [Fact]
    public void Scan_preserves_class_and_method_trait_scope_and_skips_non_test_types()
    {
        var compilation = CreateCompilation(SCAN_SOURCE);
        var symbols = GetDeclaredTypes(compilation.Assembly.GlobalNamespace).ToArray();

        var candidates = BuildingBlockTestClassScanner.Scan(symbols).ToDictionary(
            static candidate => candidate.Symbol.GetRuntimeName(),
            StringComparer.Ordinal);

        candidates.Keys.Should().BeEquivalentTo(
        [
            "Samples.AlphaServiceTests",
            "Samples.BetaServiceTests",
            "Samples.Outer+InnerServiceTests",
            "Samples.BadTraitTests",
            "Samples.EmptyTraitTests",
            "Samples.DoubleDeclarationTests"
        ]);

        var alpha = candidates["Samples.AlphaServiceTests"];
        alpha.TestMethodCount.Should().Be(2);
        alpha.Traits.Select(trait => (trait.Key, trait.Value)).Should().BeEquivalentTo(
        [
            ("REQ", "REQ-ALPHA-1"),
            ("BuildingBlock", "Samples.AlphaService")
        ]);
        alpha.Methods.Should().HaveCount(2);
        alpha.Methods.SelectMany(method => method.Traits).Should().ContainSingle()
            .Which.Value.Should().Be("REQ-ALPHA-2");
        alpha.Methods.SelectMany(method => method.Traits).Should().OnlyContain(trait => trait.Source != null);
        alpha.Diagnostics.Should().BeEmpty();

        // Traits on methods without a Fact/Theory are invisible to the test framework and stay unmerged.
        var beta = candidates["Samples.BetaServiceTests"];
        beta.TestMethodCount.Should().Be(1);
        beta.Traits.Should().BeEmpty();

        candidates["Samples.Outer+InnerServiceTests"].TestMethodCount.Should().Be(1);

        candidates["Samples.BadTraitTests"].Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "BuildingBlock.Tests.Trait.Invalid"
            && diagnostic.Severity == BuildingBlockSourceDiagnosticSeverity.Warning);
        candidates["Samples.EmptyTraitTests"].Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "BuildingBlock.Tests.Trait.Invalid");
        candidates["Samples.DoubleDeclarationTests"].Methods.SelectMany(method => method.Traits).Should().ContainSingle();
    }

    private const string SCAN_SOURCE = """
        namespace Xunit
        {
            [System.AttributeUsage(
                System.AttributeTargets.Class | System.AttributeTargets.Method,
                AllowMultiple = true)]
            public sealed class TraitAttribute(string key, string value) : System.Attribute { }

            public sealed class FactAttribute : System.Attribute { }

            public sealed class TheoryAttribute : System.Attribute { }
        }

        namespace Samples
        {
            using Xunit;

            [Trait("REQ", "REQ-ALPHA-1")]
            [Trait("BuildingBlock", "Samples.AlphaService")]
            public sealed class AlphaServiceTests
            {
                [Fact]
                public void Creates_alarm() { }

                [Fact]
                [Trait("REQ", " REQ-ALPHA-2 ")]
                public void Rings_alarm() { }
            }

            public sealed class BetaServiceTests
            {
                [Theory]
                public void Honors_limits(int value) { }

                [Trait("REQ", "attached-to-no-test")]
                public void Helper_without_fact() { }
            }

            public sealed class Outer
            {
                public sealed class InnerServiceTests
                {
                    [Fact]
                    public void Nested_class_is_scanned() { }
                }
            }

            public sealed class PlainHelperWithoutTests
            {
                public void No_fact_no_theory() { }
            }

            public abstract class AbstractTests
            {
                [Fact]
                public void Abstract_classes_are_not_test_classes() { }
            }

            public sealed class GenericTests<T>
            {
                [Fact]
                public void Generic_classes_are_not_test_classes() { }
            }

            public sealed class BadTraitTests
            {
                [Fact]
                [Trait("REQ", null)]
                public void Null_value_is_invalid() { }
            }

            public sealed class EmptyTraitTests
            {
                [Fact]
                [Trait("  ", "")]
                public void Blank_pairs_are_invalid() { }
            }

            public sealed class DoubleDeclarationTests
            {
                [Fact]
                [Trait("REQ", "REQ-GAMMA-1")]
                [Trait("REQ", "REQ-GAMMA-1")]
                public void Duplicates_collapse() { }
            }
        }
        """;

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "BuildingBlockTestClassScannerSamples",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IEnumerable<INamedTypeSymbol> GetDeclaredTypes(INamespaceSymbol namespaceSymbol)
        => namespaceSymbol.GetTypeMembers()
            .SelectMany(WithNested)
            .Concat(namespaceSymbol.GetNamespaceMembers().SelectMany(GetDeclaredTypes));

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
        => new[] { type }.Concat(type.GetTypeMembers().SelectMany(WithNested));
}
