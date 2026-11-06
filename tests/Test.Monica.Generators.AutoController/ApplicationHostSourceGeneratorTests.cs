using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class ApplicationHostSourceGeneratorTests
{
    [Fact]
    public void Run_WhenResponseContainsNullableTypes_ShouldPreserveThemAcrossCatalogAndClients()
    {
        var contracts = Contracts.Replace(
            "public sealed record QueryGetTotal : Monica.Application.Abstractions.IQuery<int>;",
            "public sealed record QueryGetTotal : Monica.Application.Abstractions.IQuery<Scenario.Payload<string?>?>;") + """

            namespace Scenario { public sealed class Payload<T> { } }
            namespace Scenario.PublishedLanguages.DomainOrdering.Requests
            {
                /// <summary>Reads an optional count.</summary>
                [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Get, "queries/OptionalCount")]
                public sealed record QueryGetOptionalCount : Monica.Application.Abstractions.IQuery<int?>;
            }
            """;
        var ordinary = RoleDeclaration + """

            namespace Scenario.Ordering;
            internal sealed class TotalHandler
                : Monica.Application.Abstractions.ApplicationService<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal, Scenario.Payload<string?>?>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Result<Scenario.Payload<string?>?>> Handle(
                    Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
            internal sealed class OptionalCountHandler
                : Monica.Application.Abstractions.ApplicationService<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetOptionalCount, int?>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int?>> Handle(
                    Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetOptionalCount request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
            internal sealed class ProbeHandler : Monica.Application.Abstractions.ApplicationService<Scenario.Internal.QueryProbe, int>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                    Scenario.Internal.QueryProbe request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
            """;

        var protocol = GeneratorTestHarness.Run(contracts, new ApiClientSourceGenerator());
        protocol.OutputErrors.Should().BeEmpty();
        AssertNullablePayload(protocol.OutputCompilation,
            "Scenario.PublishedLanguages.DomainOrdering.Contracts.IOrderingQueryApi");
        AssertNullableValuePayload(protocol.OutputCompilation,
            "Scenario.PublishedLanguages.DomainOrdering.Contracts.IOrderingQueryApi");

        var application = CompileCatalogApplication(contracts, ordinary);
        var roleMetadata = application.Exports.Single(export => export.Contains("Scenario.Ordering.TotalHandler", StringComparison.Ordinal));
        var catalogAttribute = application.Generation.OutputCompilation.Assembly.GetAttributes().Single(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Monica.ApplicationModel.Catalog.v1");
        var catalogMetadata = Assert.IsType<string>(catalogAttribute.ConstructorArguments[1].Value);
        foreach (var metadata in new[] { roleMetadata, catalogMetadata })
        {
            var operation = Assert.Single(System.Xml.Linq.XDocument.Parse(metadata).Descendants("Operation"), element =>
                ((string?)element.Attribute("Request"))?.EndsWith(".QueryGetTotal", StringComparison.Ordinal) == true);
            var resultType = Assert.IsType<string>((string?)operation.Attribute("Result"));
            resultType.Count(character => character == '?').Should().Be(2);
        }

        var local = RunHost([.. application.References, application.Catalog]);
        var remote = RunHost([application.References[0], application.Catalog]);
        foreach (var run in new[] { local, remote })
        {
            run.OutputErrors.Should().BeEmpty();
            run.RunResult.Diagnostics.Should().BeEmpty();
            run.OutputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning &&
                    diagnostic.Id.StartsWith("CS86", StringComparison.Ordinal) &&
                    diagnostic.Location.SourceTree?.FilePath.Contains("ApplicationOrderingQueryApiClient.g.cs", StringComparison.Ordinal) == true)
                .Should().BeEmpty();
            AssertNullablePayload(run.OutputCompilation,
                "Scenario.PublishedLanguages.DomainOrdering.Implementations.Application.ApplicationOrderingQueryApiClient");
            AssertNullableValuePayload(run.OutputCompilation,
                "Scenario.PublishedLanguages.DomainOrdering.Implementations.Application.ApplicationOrderingQueryApiClient");
            run.RunAgain().GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        }

        var receiver = local.GeneratedSources["HttpEndpointQueryOrdering.g.cs"];
        var responseAttribute = receiver.Split('\n').Single(line => line.Contains("ProducesResponseType(typeof(", StringComparison.Ordinal) &&
            line.Contains("Scenario.Payload", StringComparison.Ordinal));
        responseAttribute.Should().NotContain("?");

        var receiverTree = Assert.Single(local.OutputCompilation.SyntaxTrees,
            tree => tree.FilePath.EndsWith("HttpEndpointQueryOrdering.g.cs", StringComparison.Ordinal));
        var semanticModel = local.OutputCompilation.GetSemanticModel(receiverTree);
        var runtimeTypes = receiverTree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax>()
            .Select(expression => semanticModel.GetTypeInfo(expression.Type, TestContext.Current.CancellationToken).Type)
            .OfType<INamedTypeSymbol>();
        Assert.Contains(runtimeTypes, type => type.Name == "Result" && type.TypeArguments.Length == 1 &&
            type.TypeArguments[0] is INamedTypeSymbol payload &&
            payload.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);
    }

    private static void AssertNullablePayload(CSharpCompilation compilation, string typeName)
    {
        var type = compilation.GetTypeByMetadataName(typeName);
        Assert.NotNull(type);
        var method = Assert.Single(type.GetMembers("GetTotal").OfType<IMethodSymbol>());
        var task = Assert.IsAssignableFrom<INamedTypeSymbol>(method.ReturnType);
        var result = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(task.TypeArguments));
        var payload = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(result.TypeArguments));
        payload.NullableAnnotation.Should().Be(NullableAnnotation.Annotated);
        Assert.Single(payload.TypeArguments).NullableAnnotation.Should().Be(NullableAnnotation.Annotated);
    }

    private static void AssertNullableValuePayload(CSharpCompilation compilation, string typeName)
    {
        var type = compilation.GetTypeByMetadataName(typeName);
        Assert.NotNull(type);
        var method = Assert.Single(type.GetMembers("GetOptionalCount").OfType<IMethodSymbol>());
        var task = Assert.IsAssignableFrom<INamedTypeSymbol>(method.ReturnType);
        var result = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(task.TypeArguments));
        var payload = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(result.TypeArguments));
        payload.OriginalDefinition.SpecialType.Should().Be(SpecialType.System_Nullable_T);
    }

    [Fact]
    public void Catalog_PreservesActualCrossDomainApiUsageWithoutRequiringSourceImplementationsInHost()
    {
        var application = CompileCatalogApplication();
        const string source = """
            [assembly: Monica.ApplicationModel.Annotations.ApplicationDomain("Statistics")]
            namespace Scenario.Statistics;
            public sealed class Consumer
            {
                public async System.Threading.Tasks.Task Read(Scenario.PublishedLanguages.DomainOrdering.Contracts.IOrderingQueryApi api)
                {
                    await api.GetTotal(new Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal());
                }
            }
            public sealed class Unused(Scenario.PublishedLanguages.DomainOrdering.Contracts.IOrderingQueryApi api)
            {
                public object Client => api;
            }
            """;
        var producer = GeneratorTestHarness.RunNamedWithReferences("Scenario.Statistics", source,
            [application.References[0]], new ApplicationRoleMetadataSourceGenerator());
        producer.OutputErrors.Should().BeEmpty();
        producer.RunResult.Diagnostics.Should().BeEmpty();
        producer.RunAgain().GeneratedSources.Should().BeEquivalentTo(producer.GeneratedSources);
        var export = (string)producer.OutputCompilation.Assembly.GetAttributes().Single(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Monica.ApplicationModel.Export.v1").ConstructorArguments[1].Value!;
        export.Should().Contain("Scenario.Statistics.Consumer").And.NotContain("Scenario.Statistics.Unused");
        var catalog = GeneratorTestHarness.RunCatalog("Scenario.UsageCatalog", [application.References[0]], [.. application.Exports, export]);
        catalog.OutputErrors.Should().BeEmpty();
        catalog.RunResult.Diagnostics.Should().BeEmpty();
        var catalogReference = Emit(catalog.OutputCompilation, "Scenario.UsageCatalog");
        var remote = RunHost([application.References[0], catalogReference]);
        var local = RunHost([.. application.References, catalogReference, Emit(producer.OutputCompilation, "Scenario.Statistics")]);
        foreach (var run in new[] { local, remote })
        {
            run.OutputErrors.Should().BeEmpty();
            run.RunResult.Diagnostics.Should().BeEmpty();
            run.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().Contain(
                "ApplicationDependencyDefinition(\"Scenario.Statistics\", \"Scenario.Statistics.Consumer\", typeof(global::Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal))");
            run.RunAgain().GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        }
    }

    [Fact]
    public void Run_WhenHostReferencesDeclaredReleaseRoles_ShouldEmitOneClientAndCompleteOwnershipManifest()
    {
        var references = CompileApplication();
        var run = RunHost(references);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().BeEquivalentTo(
            "GeneratedApplicationManifest.g.cs", "ApplicationOrderingQueryApiClient.g.cs", "HttpEndpointQueryOrdering.g.cs");
        var manifest = run.GeneratedSources["GeneratedApplicationManifest.g.cs"];
        manifest.Should().Contain("ApplicationDomainDefinition(\"Ordering\", \"Scenario.DomainCore\")")
            .And.Contain("ApplicationRoleDefinition(\"ordering\", \"Ordering\", \"Scenario.OrderingRole\")")
            .And.Contain("ApplicationRoleDefinition(\"ordering.chat\", \"Ordering\", \"Scenario.ChatRole\")")
            .And.Contain("\"Scenario.Chat.ChatHandlers+TotalHandler\", \"Scenario.ChatRole\"")
            .And.Contain("typeof(global::Scenario.Internal.QueryProbe)")
            .And.Contain("\"ordering.chat\", \"ordering.chat\"")
            .And.NotContain("typeof(global::Scenario.Chat.ChatHandlers.TotalHandler)");
        var client = run.GeneratedSources["ApplicationOrderingQueryApiClient.g.cs"];
        client.Should().Contain("IApplicationApiInvoker invoker")
            .And.Contain("Scenario.PublishedLanguages.DomainOrdering.Contracts.IOrderingQueryApi")
            .And.Contain("GetTotal(").And.Contain("GetChatTotal(")
            .And.Contain("ApiHttpEndpoint(\"POST\", \"api/v2/Ordering/queries/Total\", false, true)")
            .And.Contain("ApiHttpEndpoint(\"GET\", \"api/v2/Ordering/queries/ChatTotal\", true, false)")
            .And.Contain("cancellationToken, _transaction);")
            .And.Contain("IOrderingQueryApi WithTransaction(global::Monica.Invocation.Models.ApiTransaction transaction)")
            .And.NotContain("LocalApiClient").And.NotContain("HttpApiClient");
        var receiver = run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"];
        receiver.Should().Contain("ApplicationApiReceiver(typeof(global::Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal))")
            .And.Contain("ApplicationApiReceiver(typeof(global::Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetChatTotal))")
            .And.Contain("mediator.Send(request, cancellationToken)")
            .And.NotContain("IApplicationApiInvoker");
        var repeated = run.RunAgain();
        repeated.GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        repeated.RunResult.Diagnostics.Should().BeEquivalentTo(run.RunResult.Diagnostics);
    }

    [Fact]
    public void Run_WhenHostAlsoDeclaresLegacyTransportGeneration_ShouldSuppressLegacyClientsAndDuplicateReceivers()
    {
        var source = Host + """

            [assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(
                Monica.Invocation.Models.ApiClientTransportKind.Local,
                typeof(Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal))]
            [assembly: Monica.WebApi.Annotations.WebApiGenerationConfig]
            """;
        var run = RunHost(CompileApplication(), source);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().BeEquivalentTo(
            "GeneratedApplicationManifest.g.cs", "ApplicationOrderingQueryApiClient.g.cs", "HttpEndpointQueryOrdering.g.cs");
    }

    [Fact]
    public void Run_WhenBusinessDomainDiffersFromWireDomain_ShouldPreserveBothIdentities()
    {
        var run = RunHost(CompileApplication(domain: "\"DataExchange\""));

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources["GeneratedApplicationManifest.g.cs"]
            .Should().Contain("ApplicationDomainDefinition(\"DataExchange\", \"Scenario.DomainCore\")");
        run.GeneratedSources["ApplicationOrderingQueryApiClient.g.cs"]
            .Should().Contain("api/v2/Ordering/queries/Total");
    }

    [Theory]
    [InlineData("Ordering.Chat")]
    [InlineData("Legacy.Ordering.Api")]
    public void Run_WhenRoleDeclaresDurableWorkloadOwner_ShouldPreserveExactValueInManifest(string owner)
    {
        var role = OrdinaryRole.Replace("Scenario.ProductDomain.Ordering)]", $"Scenario.ProductDomain.Ordering, WorkloadOwner = \"{owner}\")]");
        var run = RunHost(CompileApplication(ordinary: role));

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().Contain(
            $"ApplicationRoleDefinition(\"ordering\", \"Ordering\", \"Scenario.OrderingRole\", \"{owner}\")");
    }

    [Theory]
    [InlineData("duplicate-handler", "AC1022")]
    [InlineData("unowned-handler", "AC1023")]
    [InlineData("undefined-domain", "AC1024")]
    [InlineData("conflicting-role", "AC1025")]
    [InlineData("missing-operation", "AC1026")]
    [InlineData("missing-all-operations", "AC1026")]
    [InlineData("unnamed-enum-domain", "AC1021")]
    [InlineData("duplicate-route", "AC1009")]
    public void Run_WhenApplicationOwnershipIsInvalid_ShouldRejectManifestAndReportSpecificDiagnostic(string scenario, string diagnostic)
    {
        var ordinary = scenario == "unowned-handler" ? OrdinaryRole.Replace(RoleDeclaration, "") : OrdinaryRole;
        if (scenario is "missing-operation" or "missing-all-operations") ordinary = RoleDeclaration;
        var chat = scenario == "duplicate-handler"
            ? ChatRole.Replace("QueryGetChatTotal", "QueryGetTotal") : ChatRole;
        if (scenario == "missing-all-operations") chat = "[assembly: Monica.ApplicationModel.Annotations.ApplicationRole(\"ordering.chat\", Scenario.ProductDomain.Ordering)]";
        if (scenario == "conflicting-role") chat = chat.Replace("ApplicationRole(\"ordering.chat\"", "ApplicationRole(\"ordering.other\"");
        var contracts = scenario == "duplicate-route"
            ? Contracts.Replace("ApiHttpMethod.Get, \"queries/ChatTotal\"", "ApiHttpMethod.Post, \"queries/Total\"") : Contracts;
        var domain = scenario == "unnamed-enum-domain" ? "(Scenario.ProductDomain)99" : "Scenario.ProductDomain.Ordering";
        var references = CompileApplication(contracts, ordinary, chat, domain);
        if (scenario == "undefined-domain") references = references.Where(reference => reference.Display != "Scenario.DomainCore.dll").ToArray();
        var run = RunHost(references);

        run.RunResult.Diagnostics.Select(static item => item.Id).Should().Contain(diagnostic);
        run.GeneratedSources.Should().NotContainKey("GeneratedApplicationManifest.g.cs");
        run.RunAgain().RunResult.Diagnostics.Select(static item => item.ToString())
            .Should().BeEquivalentTo(run.RunResult.Diagnostics.Select(static item => item.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenPlainMediatorHandlerConflictsWithApplicationService_ShouldRejectManifest(bool plainHandlerInHost)
    {
        const string plainHandler = """

            internal sealed class AlternateTotalHandler
                : Monica.Core.Mediator.IRequestHandler<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal, Monica.Core.Results.Result<int>>
            {
                public System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                    Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
            """;
        var references = CompileApplication(ordinary: plainHandlerInHost ? OrdinaryRole : OrdinaryRole + plainHandler);
        var run = RunHost(references, plainHandlerInHost ? Host + plainHandler : Host);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == "AC1022" &&
            diagnostic.GetMessage().Contains("Scenario.Ordering.TotalHandler", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("AlternateTotalHandler", StringComparison.Ordinal));
        run.GeneratedSources.Should().NotContainKey("GeneratedApplicationManifest.g.cs");
        run.RunAgain().RunResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())
            .Should().BeEquivalentTo(run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }

    [Fact]
    public void Run_WhenRoleHasPlainInternalMediatorOperation_ShouldLeaveItOutsideGeneratedApplicationApis()
    {
        var ordinary = OrdinaryRole + """

            internal sealed record QuerySupport : Monica.Core.Mediator.IRequest<int>, global::Monica.Core.Mediator.IQueryRequest;
            internal sealed class SupportHandler : Monica.Core.Mediator.IRequestHandler<QuerySupport, int>
            {
                public System.Threading.Tasks.Task<int> Handle(QuerySupport request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
            """;
        var run = RunHost(CompileApplication(ordinary: ordinary));

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().NotContain("QuerySupport").And.NotContain("SupportHandler");
        run.GeneratedSources["ApplicationOrderingQueryApiClient.g.cs"].Should().NotContain("Support(");
        run.RunAgain().GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
    }

    [Fact]
    public void Run_WhenAssemblyIsNotAnApplicationHost_ShouldLeaveExistingProtocolGenerationUnchanged()
    {
        var run = GeneratorTestHarness.Run(Contracts,
            new ApiClientSourceGenerator(), new ApplicationHostSourceGenerator());

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().BeEquivalentTo("IOrderingQueryApi.g.cs");
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("private")]
    public void Run_WhenRoleRequestIsInaccessibleToHost_ShouldReportOwnershipDiagnosticInsteadOfGeneratingUncompilableManifest(string accessibility)
    {
        var ordinary = OrdinaryRole + $$"""

            internal static class PrivateOperations
            {
                {{accessibility}} sealed record QueryHidden : Monica.Application.Abstractions.IQuery<int>;
                private sealed class HiddenHandler : Monica.Application.Abstractions.ApplicationService<QueryHidden, int>
                {
                    public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                        QueryHidden request, System.Threading.CancellationToken token)
                        => throw new System.NotImplementedException();
                }
            }
            """;
        var run = RunHost(CompileApplication(ordinary: ordinary));

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == "AC1021" &&
            diagnostic.GetMessage().Contains("QueryHidden", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("not accessible", StringComparison.Ordinal));
        run.GeneratedSources.Should().NotContainKey("GeneratedApplicationManifest.g.cs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenHostReferencesOnlyOneCatalogRole_ShouldGenerateCompleteCallersAndOnlyLocalReceivers(bool chatHost)
    {
        var application = CompileCatalogApplication();
        var localRole = application.References[chatHost ? 3 : 2];
        var run = RunHost([application.References[0], application.References[1], localRole, application.Catalog]);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        var manifest = run.GeneratedSources["GeneratedApplicationManifest.g.cs"];
        manifest.Should().Contain("ApplicationRoleDefinition(\"ordering\", \"Ordering\", \"Scenario.OrderingRole\")")
            .And.Contain("ApplicationRoleDefinition(\"ordering.chat\", \"Ordering\", \"Scenario.ChatRole\")")
            .And.Contain(chatHost
                ? "new string[] { \"Scenario.ChatRole\", \"Scenario.DomainCore\" }"
                : "new string[] { \"Scenario.DomainCore\", \"Scenario.OrderingRole\" }");
        var client = run.GeneratedSources["ApplicationOrderingQueryApiClient.g.cs"];
        client.Should().Contain("GetTotal(").And.Contain("GetChatTotal(");
        var receiver = run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"];
        receiver.Should().Contain(chatHost ? "QueryGetChatTotal" : "QueryGetTotal")
            .And.NotContain(chatHost ? "QueryGetTotal" : "QueryGetChatTotal");
        if (chatHost) manifest.Should().NotContain("QueryProbe");
        else manifest.Should().Contain("QueryProbe");
        run.RunAgain().GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
        run.RunAgain().RunResult.Diagnostics.Should().BeEquivalentTo(run.RunResult.Diagnostics);
    }

    [Fact]
    public void Run_WhenCatalogHostHasNoLocalImplementations_ShouldCompileCallersWithoutCoreOrRoleReferences()
    {
        var application = CompileCatalogApplication();
        var run = RunHost([application.References[0], application.Catalog]);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().BeEquivalentTo("GeneratedApplicationManifest.g.cs", "ApplicationOrderingQueryApiClient.g.cs");
        run.GeneratedSources["GeneratedApplicationManifest.g.cs"].Should().Contain("new string[] {  }")
            .And.NotContain("QueryProbe");
    }

    [Fact]
    public void Run_WhenCatalogIsGenerated_ShouldContainOnlyPublishedStringMetadataAndNoImplementationAssemblyReferences()
    {
        var application = CompileCatalogApplication();
        application.Generation.OutputErrors.Should().BeEmpty();
        application.Generation.RunResult.Diagnostics.Should().BeEmpty();
        application.Generation.GeneratedSources.Keys.Should().BeEquivalentTo("GeneratedApplicationCatalog.g.cs");
        application.Generation.GeneratedSources.Values.Single().Should().Contain("Monica.ApplicationModel.Catalog.v1")
            .And.NotContain("typeof(").And.NotContain("QueryProbe");
        application.Generation.RunAgain().GeneratedSources.Should().BeEquivalentTo(application.Generation.GeneratedSources);

        using var image = new MemoryStream();
        var emitted = application.Generation.OutputCompilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        emitted.Success.Should().BeTrue();
        image.Position = 0;
        using var pe = new System.Reflection.PortableExecutable.PEReader(image);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var references = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name));
        references.Should().NotContain(name => name == "Scenario.DomainCore" || name == "Scenario.OrderingRole" || name == "Scenario.ChatRole");
    }

    [Fact]
    public void Run_WhenCatalogOmitsPublishedOperations_ShouldKeepMissingOperationDiagnostic()
    {
        var application = CompileCatalogApplication();
        var run = GeneratorTestHarness.RunCatalog("Incomplete.Catalog", [application.References[0]], [application.Exports[0]]);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Where(static diagnostic => diagnostic.Id == "AC1026").Should().HaveCount(2);
        run.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenLocalRoleIsMissingCatalogHandler_ShouldNotTreatBrokenLocalImplementationAsRemote()
    {
        var application = CompileCatalogApplication();
        var emptyRole = Emit(GeneratorTestHarness.RunNamedWithReferences("Scenario.OrderingRole", RoleDeclaration,
            [application.References[0], application.References[1]]).OutputCompilation, "Scenario.OrderingRole");
        var run = RunHost([application.References[0], application.References[1], emptyRole, application.Catalog]);

        run.RunResult.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "AC1026");
        run.GeneratedSources.Should().NotContainKey("GeneratedApplicationManifest.g.cs");
    }

    [Fact]
    public void Run_WhenCatalogContractMetadataIsStale_ShouldRequireRebuildingProducerAndCatalog()
    {
        var application = CompileCatalogApplication();
        var changedProtocol = Emit(GeneratorTestHarness.Run(Contracts.Replace("queries/Total", "queries/Changed"),
            new ApiClientSourceGenerator()).OutputCompilation, "Scenario.Protocol");
        var run = RunHost([changedProtocol, application.Catalog]);

        run.RunResult.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "AC1027" &&
            diagnostic.GetMessage().Contains("stale", StringComparison.Ordinal));
        run.GeneratedSources.Should().NotContainKey("GeneratedApplicationManifest.g.cs");
    }

    [Fact]
    public void Run_WhenCatalogInputIsMissingOrDuplicated_ShouldFailInsteadOfGeneratingPartialOwnership()
    {
        var application = CompileCatalogApplication();
        var missing = GeneratorTestHarness.RunCatalog("Missing.Catalog", [application.References[0]], []);
        var duplicate = GeneratorTestHarness.RunCatalog("Duplicate.Catalog", [application.References[0]],
            application.Exports.Append(application.Exports[0]).ToArray());

        foreach (var run in new[] { missing, duplicate })
        {
            run.RunResult.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "AC1027");
            run.GeneratedSources.Should().BeEmpty();
        }
    }

    private static (MetadataReference[] References, PortableExecutableReference Catalog, string[] Exports,
        GeneratorTestHarness.GeneratorTestRun Generation) CompileCatalogApplication(string contracts = Contracts, string ordinarySource = OrdinaryRole)
    {
        var protocol = Emit(GeneratorTestHarness.Run(contracts, new ApiClientSourceGenerator()).OutputCompilation, "Scenario.Protocol");
        var core = GeneratorTestHarness.RunNamedWithReferences("Scenario.DomainCore",
            "[assembly: Monica.ApplicationModel.Annotations.ApplicationDomain(Scenario.ProductDomain.Ordering)]", [protocol],
            new ApplicationRoleMetadataSourceGenerator());
        var coreReference = Emit(core.OutputCompilation, "Scenario.DomainCore");
        var ordinary = GeneratorTestHarness.RunNamedWithReferences("Scenario.OrderingRole", ordinarySource, [protocol, coreReference],
            new ApplicationRoleMetadataSourceGenerator());
        var chat = GeneratorTestHarness.RunNamedWithReferences("Scenario.ChatRole", ChatRole, [protocol, coreReference],
            new ApplicationRoleMetadataSourceGenerator());
        var producers = new[] { core, ordinary, chat };
        foreach (var producer in producers)
        {
            producer.OutputErrors.Should().BeEmpty();
            producer.RunResult.Diagnostics.Should().BeEmpty();
            producer.RunAgain().GeneratedSources.Should().BeEquivalentTo(producer.GeneratedSources);
        }
        var exports = producers.Select(producer => (string)producer.OutputCompilation.Assembly.GetAttributes().Single(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Monica.ApplicationModel.Export.v1").ConstructorArguments[1].Value!).ToArray();
        var catalog = GeneratorTestHarness.RunCatalog("Scenario.Catalog", [protocol], exports);
        catalog.RunResult.Diagnostics.Should().BeEmpty();
        return ([protocol, coreReference, Emit(ordinary.OutputCompilation, "Scenario.OrderingRole"), Emit(chat.OutputCompilation, "Scenario.ChatRole")],
            Emit(catalog.OutputCompilation, "Scenario.Catalog"), exports, catalog);
    }

    private static GeneratorTestHarness.GeneratorTestRun RunHost(IReadOnlyCollection<MetadataReference> references, string source = Host)
        => GeneratorTestHarness.RunWithReferences(source, references,
            new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator(), new ApplicationHostSourceGenerator());

    private static MetadataReference[] CompileApplication(string contracts = Contracts, string ordinary = OrdinaryRole,
        string chat = ChatRole, string domain = "Scenario.ProductDomain.Ordering")
    {
        var protocol = Emit(GeneratorTestHarness.Run(contracts, new ApiClientSourceGenerator()).OutputCompilation,
            "Scenario.Protocol");
        var core = Emit(GeneratorTestHarness.RunWithReferences(
            $"[assembly: Monica.ApplicationModel.Annotations.ApplicationDomain({domain})]\ninternal sealed class CoreMarker;",
            [protocol]).OutputCompilation, "Scenario.DomainCore");
        ordinary = ordinary.Replace("Scenario.ProductDomain.Ordering", domain);
        chat = chat.Replace("Scenario.ProductDomain.Ordering", domain);
        var ordinaryAssembly = Emit(GeneratorTestHarness.RunWithReferences(ordinary, [protocol, core],
            new HttpApiControllerSourceGenerator()).OutputCompilation, "Scenario.OrderingRole");
        var chatAssembly = Emit(GeneratorTestHarness.RunWithReferences(chat, [protocol, core],
            new HttpApiControllerSourceGenerator()).OutputCompilation, "Scenario.ChatRole");
        return [protocol, core, ordinaryAssembly, chatAssembly];
    }

    private static PortableExecutableReference Emit(CSharpCompilation compilation, string assemblyName)
    {
        using var stream = new MemoryStream();
        var result = compilation.WithAssemblyName(assemblyName).Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        result.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return MetadataReference.CreateFromImage(ImmutableArray.CreateRange(stream.ToArray()), filePath: assemblyName + ".dll");
    }

    private const string Host = "[assembly: Monica.ApplicationModel.Annotations.ApplicationHost]\n";

    private const string RoleDeclaration = """
        [assembly: Monica.ApplicationModel.Annotations.ApplicationRole("ordering", Scenario.ProductDomain.Ordering)]
        """;

    private const string OrdinaryRole = RoleDeclaration + """

        namespace Scenario.Ordering;
        internal sealed class TotalHandler
            : Monica.Application.Abstractions.ApplicationService<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal, int>
        {
            public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetTotal request, System.Threading.CancellationToken token)
                => throw new System.NotImplementedException();
        }
        internal sealed class ProbeHandler : Monica.Application.Abstractions.ApplicationService<Scenario.Internal.QueryProbe, int>
        {
            public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                Scenario.Internal.QueryProbe request, System.Threading.CancellationToken token)
                => throw new System.NotImplementedException();
        }
        """;

    private const string ChatRole = """
        [assembly: Monica.ApplicationModel.Annotations.ApplicationRole("ordering.chat", Scenario.ProductDomain.Ordering)]
        namespace Scenario.Chat;
        internal static class ChatHandlers
        {
            internal sealed class TotalHandler
                : Monica.Application.Abstractions.ApplicationService<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetChatTotal, int>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Result<int>> Handle(
                    Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetChatTotal request, System.Threading.CancellationToken token)
                    => throw new System.NotImplementedException();
            }
        }
        """;

    private const string Contracts = """
        [assembly: Monica.Application.Annotations.HttpApiContract("api/v2")]
        namespace Scenario { public enum ProductDomain { Ordering = 17 } }
        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Reads the ordinary order total.</summary>
            [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Post, "queries/Total")]
            public sealed record QueryGetTotal : Monica.Application.Abstractions.IQuery<int>;

            /// <summary>Reads the chat-owned total.</summary>
            [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Get, "queries/ChatTotal")]
            [Monica.ApplicationModel.Annotations.RunOnRole("ordering.chat")]
            public sealed record QueryGetChatTotal : Monica.Application.Abstractions.IQuery<int>;
        }
        namespace Scenario.Internal
        {
            public sealed record QueryProbe : Monica.Application.Abstractions.IQuery<int>;
        }
        """;
}
