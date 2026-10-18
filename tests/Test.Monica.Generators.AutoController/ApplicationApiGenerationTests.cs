using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class ApplicationApiGenerationTests
{
    [Fact]
    public void Run_WhenContractsCarryHttpMetadata_ShouldGenerateInterfacesWithoutWebApiReference()
    {
        var seed = GeneratorTestHarness.Run(Contracts, new ApiClientSourceGenerator());
        var compilation = seed.InputCompilation.RemoveReferences(seed.InputCompilation.References
            .Where(reference => reference.Display?.EndsWith("Monica.WebApi.dll", StringComparison.OrdinalIgnoreCase) == true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ApiClientSourceGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);

        output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        driver.GetRunResult().Results.Single().GeneratedSources.Select(source => source.HintName)
            .Should().BeEquivalentTo("IOrderingQueryApi.g.cs", "IUnusedQueryApi.g.cs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WhenLocalHostReferencesReleaseAssemblies_ShouldPublishOnlyOwnedHttpReceiversWhenEnabled(bool debug)
    {
        var (protocol, handlers) = CompileBackend();
        var source = (debug ? "#define DEBUG\n" : "") + Host;
        var run = GeneratorTestHarness.RunWithReferences(source, [protocol, handlers],
            new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator());

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().Contain("LocalOrderingQueryApiClient.g.cs");
        run.GeneratedSources.Keys.Should().NotContain("IOrderingQueryApi.g.cs");
        run.GeneratedSources.Keys.Should().NotContain("HttpEndpointQueryUnused.g.cs");
        run.GeneratedSources.ContainsKey("HttpEndpointQueryOrdering.g.cs").Should().Be(debug);
        if (debug)
            run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"].Should().Contain("HttpPost(\"queries/Total\")");
        run.RunAgain().GeneratedSources.Should().BeEquivalentTo(run.GeneratedSources);
    }

    [Fact]
    public void Run_WhenHttpHostOwnsHandlers_ShouldGeneratePairedClientsAndReceiversInRelease()
    {
        var (protocol, handlers) = CompileBackend();
        var run = GeneratorTestHarness.RunWithReferences(Host.Replace("ApiClientTransportKind.Local", "ApiClientTransportKind.Http"),
            [protocol, handlers], new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator());

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().Contain("HttpOrderingQueryApiClient.g.cs");
        run.GeneratedSources.Keys.Should().Contain("HttpEndpointQueryOrdering.g.cs");
        run.GeneratedSources.Keys.Should().NotContain("LocalOrderingQueryApiClient.g.cs");
        run.GeneratedSources["HttpOrderingQueryApiClient.g.cs"].Should().Contain("api/v1/Ordering/queries/Total");
    }

    [Fact]
    public void Run_WhenExternalUiConsumesHttp_ShouldGenerateClientsWithoutReceivers()
    {
        var (protocol, _) = CompileBackend();
        var run = GeneratorTestHarness.RunWithReferences("""
            [assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(
                Monica.Invocation.Models.ApiClientTransportKind.Http, typeof(Scenario.PublishedLanguages.DomainOrdering.Requests.QueryTotal))]
            """, [protocol], new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator());
        run.OutputErrors.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().Contain("HttpOrderingQueryApiClient.g.cs");
        run.GeneratedSources.Keys.Should().NotContain(key => key.StartsWith("HttpEndpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WhenGrpcIsSelected_ShouldRejectUnsupportedAdapter()
    {
        var run = GeneratorTestHarness.Run("""
            [assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(Monica.Invocation.Models.ApiClientTransportKind.Grpc)]
            """, new ApiClientSourceGenerator());
        run.RunResult.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "AC1004");
    }

    [Fact]
    public void Run_WhenProtocolAssemblyIsDeclaredAsHandlerOwner_ShouldReportOwnershipError()
    {
        var (protocol, _) = CompileBackend();
        var source = Host.Replace("typeof(Scenario.Handlers.TotalHandler)", "typeof(Scenario.PublishedLanguages.DomainOrdering.Requests.QueryTotal)");
        var run = GeneratorTestHarness.RunWithReferences(source, [protocol],
            new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator());
        run.RunResult.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == "AC1020");
    }

    [Fact]
    public void Run_WhenWireMappingIsShared_ShouldPreserveItsPrefixAndVerbWithoutPublishingFromHandlerAssembly()
    {
        var mappedContracts = "[assembly: Monica.Application.Annotations.HttpApiContract(\"api/v2\")]\n" +
            Contracts.Replace("ApiHttpMethod.Post", "ApiHttpMethod.Get").Replace("queries/Total", "totals");
        var (protocol, handlers) = CompileBackend(mappedContracts);
        var run = GeneratorTestHarness.RunWithReferences(Host.Replace("ApiClientTransportKind.Local", "ApiClientTransportKind.Http"),
            [protocol, handlers], new ApiClientSourceGenerator(), new HttpApiControllerSourceGenerator());
        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics.Should().BeEmpty();
        run.GeneratedSources["HttpOrderingQueryApiClient.g.cs"].Should().Contain("api/v2/Ordering/totals").And.Contain("HttpMethod.Get");
        run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"].Should().Contain("api/v2/Ordering").And.Contain("HttpGet(\"totals\")");
    }

    private static (PortableExecutableReference Protocol, PortableExecutableReference Handlers) CompileBackend(string contracts = Contracts)
    {
        var protocol = Emit(GeneratorTestHarness.Run(contracts, new ApiClientSourceGenerator()).OutputCompilation);
        var handlers = GeneratorTestHarness.RunWithReferences("""
            namespace Scenario.Handlers;
            public sealed class TotalHandler : Monica.Application.Abstractions.ApplicationService<Scenario.PublishedLanguages.DomainOrdering.Requests.QueryTotal, int>
            {
                public override System.Threading.Tasks.Task<Monica.Core.Results.Res<int>> Handle(
                    Scenario.PublishedLanguages.DomainOrdering.Requests.QueryTotal request, System.Threading.CancellationToken cancellationToken)
                    => System.Threading.Tasks.Task.FromResult(Monica.Core.Results.Res.Ok(42));
            }
            """, [protocol], new HttpApiControllerSourceGenerator());
        handlers.GeneratedSources.Should().BeEmpty();
        return (protocol, Emit(handlers.OutputCompilation));
    }

    private static PortableExecutableReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return MetadataReference.CreateFromImage(ImmutableArray.CreateRange(stream.ToArray()));
    }

    private const string Contracts = """
        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Reads the order total.</summary>
            [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Post, "queries/Total")]
            public sealed record QueryTotal : Monica.Application.Abstractions.IResultRequest<int>;
        }
        namespace Scenario.PublishedLanguages.DomainUnused.Requests
        {
            /// <summary>An operation owned by another service.</summary>
            [Monica.Application.Annotations.ApiEndpoint(Monica.Application.Annotations.ApiHttpMethod.Post, "queries/Other")]
            public sealed record QueryOther : Monica.Application.Abstractions.IResultRequest<int>;
        }
        """;

    private const string Host = """
        [assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(
            Monica.Invocation.Models.ApiClientTransportKind.Local, typeof(Scenario.PublishedLanguages.DomainOrdering.Requests.QueryTotal),
            HandlerAssemblies = new[] { typeof(Scenario.Handlers.TotalHandler) })]
        #if DEBUG
        [assembly: Monica.WebApi.Annotations.WebApiGenerationConfig]
        #endif
        """;
}
