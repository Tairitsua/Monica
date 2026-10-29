using System.Collections.Immutable;
using System.Xml.Linq;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class ApplicationOwnershipMetadataTests
{
    [Fact]
    public void Run_WhenDomainOwnsBackgroundWork_ShouldRoundTripOwnershipWithoutExportingRegistrationTypes()
    {
        var core = ExportCore("orders.worker", registration: true);
        var coreReference = Emit(core, "Scenario.Core");
        var role = ExportRole(coreReference, registration: true);
        var catalog = GeneratorTestHarness.RunCatalog("Scenario.Catalog", [], [Payload(core), Payload(role)]);
        AssertValid(catalog);
        var catalogReference = Emit(catalog, "Scenario.Catalog");

        foreach (var references in new MetadataReference[][]
                 {
                     [catalogReference],
                     [catalogReference, coreReference, Emit(role, "Scenario.Role")]
                 })
        {
            var host = RunHost(references);
            AssertValid(host);
            host.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().Contain(
                "ApplicationDomainDefinition(\"Orders\", \"Scenario.Core\", \"orders.worker\")");
        }

        foreach (var export in new[] { Payload(core), Payload(role), catalog.GeneratedSources.Values.Single() })
            export.Should().NotContain("Scenario.DomainRegistration").And.NotContain("Scenario.RoleRegistration")
                .And.NotContain("typeof(").And.NotContain("Registration=");
        XElement.Parse(Payload(core)).Element("Assemblies")!.Elements("Assembly").Single()
            .Attribute("BackgroundRole")!.Value.Should().Be("orders.worker");

        using var image = new MemoryStream();
        catalog.OutputCompilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        image.Position = 0;
        using var pe = new System.Reflection.PortableExecutable.PEReader(image);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .Should().NotContain(name => name == "Scenario.Core" || name == "Scenario.Role");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" orders.worker")]
    [InlineData("orders.worker ")]
    public void Run_WhenBackgroundRoleIdentifierIsInvalid_ShouldRejectProducer(string backgroundRole)
    {
        var run = ExportCore(backgroundRole, assertValid: false);
        AssertInvalid(run, "AC1021", "BackgroundRole");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenBackgroundRoleIsMissingOrInAnotherDomain_ShouldRejectCompleteInventories(bool differentDomain)
    {
        var core = ExportCore("other.worker");
        var coreReference = Emit(core, "Scenario.Core");
        var role = ExportRole(coreReference);
        var references = new List<MetadataReference> { coreReference, Emit(role, "Scenario.Role") };
        var exports = new List<string> { Payload(core), Payload(role) };
        if (differentDomain)
        {
            var otherCore = GeneratorTestHarness.RunNamedWithReferences("Other.Core",
                "[assembly: Monica.ApplicationModel.Annotations.ApplicationDomain(\"Other\")]", [],
                new ApplicationRoleMetadataSourceGenerator());
            AssertValid(otherCore);
            var otherReference = Emit(otherCore, "Other.Core");
            var otherRole = GeneratorTestHarness.RunNamedWithReferences("Other.Role",
                "[assembly: Monica.ApplicationModel.Annotations.ApplicationRole(\"other.worker\", \"Other\")]", [otherReference],
                new ApplicationRoleMetadataSourceGenerator());
            AssertValid(otherRole);
            references.AddRange([otherReference, Emit(otherRole, "Other.Role")]);
            exports.AddRange([Payload(otherCore), Payload(otherRole)]);
        }

        AssertInvalid(RunHost(references), "AC1021", "same domain");
        AssertInvalid(GeneratorTestHarness.RunCatalog("Invalid.Catalog", [], exports), "AC1027", "same domain");
    }

    [Fact]
    public void Run_WhenLocalBackgroundRoleDiffersFromCatalog_ShouldRequireCatalogRebuild()
    {
        var core = ExportCore("orders.worker");
        var role = ExportRole(Emit(core, "Scenario.Core"));
        var catalog = GeneratorTestHarness.RunCatalog("Scenario.Catalog", [], [Payload(core), Payload(role)]);
        AssertValid(catalog);
        var changedCore = ExportCore(null);

        var host = RunHost([Emit(changedCore, "Scenario.Core"), Emit(role, "Scenario.Role"), Emit(catalog, "Scenario.Catalog")]);

        AssertInvalid(host, "AC1021", "Rebuild the catalog");
    }

    [Fact]
    public void Run_WhenCatalogAssignsBackgroundRoleToRoleAssembly_ShouldRejectInvalidOwnership()
    {
        var core = ExportCore(null);
        var role = ExportRole(Emit(core, "Scenario.Core"));
        var payload = XElement.Parse(Payload(role));
        payload.Element("Assemblies")!.Element("Assembly")!.SetAttributeValue("BackgroundRole", "orders.worker");

        AssertInvalid(GeneratorTestHarness.RunCatalog("Invalid.Catalog", [], [Payload(core), payload.ToString()]),
            "AC1027", "cannot declare BackgroundRole");
    }

    public static IEnumerable<object[]> InvalidRegistrations()
    {
        foreach (var role in new[] { false, true })
        foreach (var (type, declaration) in new (string, string)[]
                 {
                     ("LocalRegistration", "public sealed class LocalRegistration;"),
                     ("LocalRegistration", "public abstract class LocalRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration;"),
                     ("LocalRegistration", "public sealed class LocalRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration { private LocalRegistration() {} }"),
                     ("LocalRegistration", "public sealed class LocalRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration { public LocalRegistration(int value) {} }"),
                     ("LocalRegistration<>", "public sealed class LocalRegistration<T> : Monica.ApplicationModel.Abstractions.ApplicationRegistration;")
                 })
            yield return [role, type, declaration];
    }

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public void Run_WhenRegistrationCannotBeConstructed_ShouldRejectDeclaration(bool role, string type, string declaration)
    {
        var run = RunRegistration(role, $"typeof({type})", declaration);
        AssertInvalid(run, "AC1021", "public parameterless constructor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenRegistrationBelongsToAnotherAssembly_ShouldRejectDeclaration(bool role)
    {
        var foreign = GeneratorTestHarness.CompileToMetadataReference("Foreign.Registration", """
            public sealed class ForeignRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration;
            """, TestContext.Current.CancellationToken);
        var run = RunRegistration(role, "typeof(ForeignRegistration)", string.Empty, [foreign]);
        AssertInvalid(run, "AC1021", "same assembly");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenDomainRegistrationOverridesActiveWithoutOwner_ShouldRejectDeclaration(bool inherited)
    {
        var declaration = $$"""
            public class ActiveRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration
            {
                public override void ConfigureActive(Monica.ApplicationModel.Abstractions.ApplicationRegistrationContext context) { }
            }
            {{(inherited ? "public sealed class LocalRegistration : ActiveRegistration;" : string.Empty)}}
            """;
        AssertInvalid(RunRegistration(false, inherited ? "typeof(LocalRegistration)" : "typeof(ActiveRegistration)", declaration),
            "AC1021", "must declare BackgroundRole");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Run_WhenRegistrationIsNullOrClosedConstructedType_ShouldExportOnlyOwnership(bool role, bool closedType)
    {
        var run = RunRegistration(role, closedType ? "typeof(LocalRegistration<int>)" : "null", """
            internal sealed class LocalRegistration<T> : Monica.ApplicationModel.Abstractions.ApplicationRegistration
            {
                public LocalRegistration() => throw new System.InvalidOperationException("Generators must not execute registrations.");
            }
            """);
        AssertValid(run);
        Payload(run).Should().NotContain("LocalRegistration");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Run_WhenLocalEndpointOmitsDomainName_ShouldInferRoleDomainAndPreserveExplicitWireOverride(
        bool enumDomain, bool contract, bool explicitDomain)
    {
        var core = ExportCore(null);
        var domain = enumDomain ? "Scenario.ProductDomain.Orders" : "\"Orders\"";
        var contractAttribute = contract
            ? $"[assembly: Monica.Application.Annotations.HttpApiContract(\"api/v7\"{(explicitDomain ? ", DomainName = \"WireOrders\"" : string.Empty)})]"
            : string.Empty;
        var role = GeneratorTestHarness.RunNamedWithReferences("Scenario.Role", $$"""
            [assembly: Monica.ApplicationModel.Annotations.ApplicationRole("orders.worker", {{domain}})]
            {{contractAttribute}}
            namespace Scenario.Local;
            [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Get, "probe")]
            public sealed record QueryProbe : Monica.Application.Abstractions.IQuery<int>;
            internal sealed class ProbeHandler : Monica.Application.Abstractions.ApplicationService<QueryProbe, int>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Res<int>> Handle(
                    QueryProbe request, System.Threading.CancellationToken cancellationToken) => throw new System.NotImplementedException();
            }
            """, [Emit(core, "Scenario.Core")], new ApplicationRoleMetadataSourceGenerator());
        AssertValid(role);

        var host = RunHost([Emit(core, "Scenario.Core"), Emit(role, "Scenario.Role")]);

        AssertValid(host);
        var wireDomain = explicitDomain ? "WireOrders" : "Orders";
        host.GeneratedSources[$"HttpEndpointQuery{wireDomain}.g.cs"].Should().Contain(
            $"[Route(\"{(contract ? "api/v7" : "api/v1")}/{wireDomain}\")]");
        host.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().Contain(
            "\"Scenario.Role\", \"Orders\", \"orders.worker\"");
        host.GeneratedSources.Keys.Should().NotContain(name => name.EndsWith("ApiClient.g.cs", StringComparison.Ordinal));
    }

    private static GeneratorTestHarness.GeneratorTestRun ExportCore(string? backgroundRole, bool registration = false, bool assertValid = true)
    {
        var arguments = backgroundRole is null ? string.Empty
            : $", BackgroundRole = {Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(backgroundRole, true)}";
        if (registration) arguments += ", Registration = typeof(Scenario.DomainRegistration)";
        var source = $$"""
            [assembly: Monica.ApplicationModel.Annotations.ApplicationDomain(Scenario.ProductDomain.Orders{{arguments}})]
            namespace Scenario;
            public enum ProductDomain { Orders = 17 }
            internal sealed class DomainRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration
            {
                public DomainRegistration() => throw new System.InvalidOperationException("Generators must not execute registrations.");
                public override void ConfigureActive(Monica.ApplicationModel.Abstractions.ApplicationRegistrationContext context) { }
            }
            """;
        var run = GeneratorTestHarness.RunNamedWithReferences("Scenario.Core", source, [], new ApplicationRoleMetadataSourceGenerator());
        if (assertValid) AssertValid(run);
        return run;
    }

    private static GeneratorTestHarness.GeneratorTestRun ExportRole(MetadataReference core, bool registration = false)
    {
        var run = GeneratorTestHarness.RunNamedWithReferences("Scenario.Role", $$"""
            [assembly: Monica.ApplicationModel.Annotations.ApplicationRole("orders.worker", "Orders"{{(registration ? ", Registration = typeof(Scenario.RoleRegistration)" : string.Empty)}})]
            namespace Scenario;
            internal sealed class RoleRegistration : Monica.ApplicationModel.Abstractions.ApplicationRegistration
            {
                public RoleRegistration() => throw new System.InvalidOperationException("Generators must not execute registrations.");
                public override void ConfigureActive(Monica.ApplicationModel.Abstractions.ApplicationRegistrationContext context) { }
            }
            """, [core], new ApplicationRoleMetadataSourceGenerator());
        AssertValid(run);
        return run;
    }

    private static GeneratorTestHarness.GeneratorTestRun RunRegistration(bool role, string type, string declaration,
        IReadOnlyCollection<MetadataReference>? references = null)
    {
        var owner = role ? "ApplicationRole(\"orders.worker\", \"Orders\"" : "ApplicationDomain(\"Orders\"";
        var dependencies = new List<MetadataReference>(references ?? []);
        if (role) dependencies.Add(Emit(ExportCore(null), "Scenario.Core"));
        return GeneratorTestHarness.RunNamedWithReferences("Scenario.Registration",
            $"[assembly: Monica.ApplicationModel.Annotations.{owner}, Registration = {type})]\n{declaration}",
            dependencies, new ApplicationRoleMetadataSourceGenerator());
    }

    private static GeneratorTestHarness.GeneratorTestRun RunHost(IReadOnlyCollection<MetadataReference> references) =>
        GeneratorTestHarness.RunNamedWithReferences("Scenario.Host",
            "[assembly: Monica.ApplicationModel.Annotations.ApplicationHost]", references, new ApplicationHostSourceGenerator());

    private static void AssertValid(GeneratorTestHarness.GeneratorTestRun run)
    {
        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        var repeated = run.RunAgain();
        repeated.GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        repeated.RunResult.Diagnostics.Should().BeEquivalentTo(run.RunResult.Diagnostics);
    }

    private static void AssertInvalid(GeneratorTestHarness.GeneratorTestRun run, string code, string message)
    {
        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == code && diagnostic.GetMessage().Contains(message, StringComparison.Ordinal));
        run.GeneratedSources.Should().BeEmpty();
        var repeated = run.RunAgain();
        repeated.GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        repeated.RunResult.Diagnostics.Should().BeEquivalentTo(run.RunResult.Diagnostics);
    }

    private static string Payload(GeneratorTestHarness.GeneratorTestRun run) =>
        (string)run.OutputCompilation.Assembly.GetAttributes().Single(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Monica.ApplicationModel.Export.v1").ConstructorArguments[1].Value!;

    private static PortableExecutableReference Emit(GeneratorTestHarness.GeneratorTestRun run, string assemblyName)
    {
        using var stream = new MemoryStream();
        var result = run.OutputCompilation.WithAssemblyName(assemblyName).Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        result.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return MetadataReference.CreateFromImage(ImmutableArray.CreateRange(stream.ToArray()), filePath: assemblyName + ".dll");
    }
}
