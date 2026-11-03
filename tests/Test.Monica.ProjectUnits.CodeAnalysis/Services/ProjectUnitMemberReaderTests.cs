using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.ProjectUnits.CodeAnalysis.Services;
using Xunit;

namespace Test.Monica.ProjectUnits.CodeAnalysis.Services;

public sealed class ProjectUnitMemberReaderTests
{
    [Fact]
    public void Read_WhenMembersAreInheritedOrNullable_ShouldRetainShapeAndDocumentation()
    {
        const string source = """
            #nullable enable
            namespace Sample;
            public class Base
            {
                /// <summary>Stable identity.</summary>
                public long Id { get; protected set; }
                public virtual string Name { get; set; } = "";
            }
            public class Order : Base
            {
                /// <summary>The customer's reference.</summary>
                public required string Reference { get; init; }
                public string? Optional { get; private set; }
                public int? Retry { get; set; }
                public override string Name { get; set; } = "";
                public string this[int index] => "";
                public static int Count { get; set; }
                /// <summary>Accepts a prepared order.</summary>
                public void Accept() { }
            }
            """;
        var root = Path.GetTempPath();
        var tree = CSharpSyntaxTree.ParseText(source, path: Path.Combine(root, "Order.cs"), cancellationToken: TestContext.Current.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("MemberTest", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var symbol = compilation.GetTypeByMetadataName("Sample.Order")!;
        var fields = ProjectUnitMemberReader.ReadProperties(symbol, root);
        Assert.Equal(5, fields.Count);
        var id = Assert.Single(fields, field => field.Name == "Id");
        Assert.True(id.IsInherited);
        Assert.Equal("Stable identity.", id.Description);
        Assert.Equal("protected set", id.Setter);
        Assert.False(id.IsNullable);
        var reference = Assert.Single(fields, field => field.Name == "Reference");
        Assert.True(reference.IsRequired);
        Assert.Equal("public init", reference.Setter);
        Assert.Equal("Order.cs", reference.Source!.RelativePath);
        Assert.True(Assert.Single(fields, field => field.Name == "Optional").IsNullable);
        Assert.True(Assert.Single(fields, field => field.Name == "Retry").IsNullable);
        Assert.False(Assert.Single(fields, field => field.Name == "Name").IsInherited);
        var method = Assert.Single(ProjectUnitMemberReader.ReadOperations(symbol, root));
        Assert.Equal("Accepts a prepared order.", method.Description);
    }
}
