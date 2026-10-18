using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class IncrementalGeneratorPipelineTests
{
    [Fact]
    public void Run_WhenOnlyUnrelatedSyntaxChanges_ShouldReuseSemanticSnapshotsAndGeneratedSources()
    {
        AssertUnrelatedEditIsCached(
            new HttpApiControllerSourceGenerator(),
            "ControllerCandidateAnalysis",
            "ControllerCandidateCollection");
        AssertUnrelatedEditIsCached(
            new ApiClientSourceGenerator(),
            "ApiEndpointAnalysis",
            "ApiEndpointCollection");
    }

    [Fact]
    public void Run_WhenOnlyAssemblyConfigurationChanges_ShouldRegenerateRoutes()
    {
        AssertConfigurationEditRegeneratesRoutes(new HttpApiControllerSourceGenerator());
        AssertConfigurationEditRegeneratesRoutes(new ApiClientSourceGenerator());
    }

    [Fact]
    public void Run_WhenPublishedRequestComesFromProtocolMetadata_ShouldUseItsXmlSummaryInController()
    {
        var seed = GeneratorTestHarness.Run(
            ProtocolMetadataScenario,
            new ApiClientSourceGenerator());
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview,
            documentationMode: DocumentationMode.Diagnose);
        var protocolTree = CSharpSyntaxTree.ParseText(
            ProtocolMetadataScenario,
            parseOptions,
            path: "ProtocolRequest.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var protocolCompilation = CSharpCompilation.Create(
            "Scenario.Protocol",
            new[] { protocolTree },
            seed.InputCompilation.References,
            seed.InputCompilation.Options);
        using var assemblyStream = new MemoryStream();
        using var documentationStream = new MemoryStream();
        var emitResult = protocolCompilation.Emit(
            assemblyStream,
            xmlDocumentationStream: documentationStream,
            cancellationToken: TestContext.Current.CancellationToken);
        emitResult.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var protocolReference = MetadataReference.CreateFromImage(
            ImmutableArray.CreateRange(assemblyStream.ToArray()),
            documentation: new InMemoryXmlDocumentationProvider(documentationStream.ToArray()),
            filePath: "Scenario.Protocol.dll");
        var serviceTree = CSharpSyntaxTree.ParseText(
            ServiceCompilationScenario,
            parseOptions,
            path: "ServiceHandler.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var serviceCompilation = CSharpCompilation.Create(
            "Scenario.Service",
            new[] { serviceTree },
            seed.InputCompilation.References.Concat(new[] { protocolReference }),
            seed.InputCompilation.Options);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new HttpApiControllerSourceGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            serviceCompilation,
            out var outputCompilation,
            out _,
            TestContext.Current.CancellationToken);
        var runResult = driver.GetRunResult();

        outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        var controller = runResult.Results.Single().GeneratedSources.Single().SourceText.ToString();
        controller.Should().Contain("Loads the exact published order summary from Protocol XML.");
    }

    [Fact]
    public void Run_WhenProtocolMetadataHasNoDocumentationProvider_ShouldEmitCompilableTopLevelInheritDoc()
    {
        var protocolReference = GeneratorTestHarness.CompileToMetadataReference(
            "Scenario.Protocol",
            ProtocolMetadataScenario,
            TestContext.Current.CancellationToken);

        var run = GeneratorTestHarness.RunWithReferences(
            ServiceCompilationScenario,
            [protocolReference],
            new HttpApiControllerSourceGenerator());

        run.RunResult.Diagnostics.Should().BeEmpty();
        run.OutputErrors.Should().BeEmpty();
        var expectedDocumentation = string.Join(
            Environment.NewLine,
            "    /// <inheritdoc cref=\"global::Scenario.PublishedLanguages.DomainOrdering.Requests.QueryGetPublishedOrder\"/>",
            "    [HttpGet(\"orders/{Id}\")]");
        run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"]
            .Should().Contain(expectedDocumentation);
    }

    [Fact]
    public void Run_WhenHttpClientReturnsPositionalEnvelope_ShouldCompileWithoutParameterlessConstructor()
    {
        var run = GeneratorTestHarness.Run(
            PositionalEnvelopeScenario,
            new ApiClientSourceGenerator());

        run.RunResult.Diagnostics.Should().BeEmpty();
        run.OutputErrors.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().Contain("HttpOrderingQueryApiClient.g.cs");
        run.GeneratedSources["HttpOrderingQueryApiClient.g.cs"]
            .Should().Contain("global::Scenario.Results.PositionalEnvelope<string>");
    }

    [Fact]
    public void Run_WhenPublishedResultIsNotAnEnvelope_ShouldReportContractDiagnostic()
    {
        var run = GeneratorTestHarness.Run(
            InvalidPublishedEnvelopeScenario,
            new ApiClientSourceGenerator());

        run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Should().Contain("AC1017");
    }

    [Fact]
    public void Run_WhenRoutesDifferOnlyByLiteralCaseAndPlaceholderName_ShouldReportDuplicateRoute()
    {
        var run = GeneratorTestHarness.Run(
            SemanticallyDuplicateRouteScenario,
            new HttpApiControllerSourceGenerator());

        run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Should().Contain("AC1009");
    }

    [Fact]
    public void Run_WhenRouteShapesDifferByConstraintCatchAllOrOptionality_ShouldKeepRoutesDistinct()
    {
        var run = GeneratorTestHarness.Run(
            DistinctRouteShapeScenario,
            new HttpApiControllerSourceGenerator());

        run.RunResult.Diagnostics.Should().BeEmpty();
        run.OutputErrors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sealed", "ICachedServiceProvider serviceProvider, HttpClient httpClient")]
    [InlineData("abstract", "ICachedServiceProvider serviceProvider, HttpClient httpClient, int discriminator")]
    public void Run_WhenConfiguredHttpBaseCannotBeSubclassedAsGenerated_ShouldReportConfigurationDiagnostic(
        string typeModifier,
        string constructorParameters)
    {
        var source = InvalidConfiguredBaseScenario
            .Replace("$TYPE_MODIFIER$", typeModifier, StringComparison.Ordinal)
            .Replace("$CONSTRUCTOR_PARAMETERS$", constructorParameters, StringComparison.Ordinal);

        var run = GeneratorTestHarness.Run(source, new ApiClientSourceGenerator());

        run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Should().Contain("AC1004");
    }

    [Theory]
    [MemberData(nameof(InaccessibleOrGenericBaseScenarios))]
    public void Run_WhenConfiguredHttpBaseCannotBeNamedFromGeneratedCode_ShouldReportConfigurationDiagnostic(
        string source)
    {
        var run = GeneratorTestHarness.Run(source, new ApiClientSourceGenerator());

        run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Should().Contain("AC1004");
    }

    public static TheoryData<string> InaccessibleOrGenericBaseScenarios => new()
    {
        GenericConfiguredBaseScenario,
        FileLocalConfiguredBaseScenario
    };

    private static void AssertUnrelatedEditIsCached(
        IIncrementalGenerator generator,
        string analysisStepName,
        string collectionStepName)
    {
        var seed = GeneratorTestHarness.Run(
            new[] { IncrementalEndpointScenario, UnrelatedSyntaxBefore },
            generator);
        var parseOptions = (CSharpParseOptions)seed.InputCompilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            additionalTexts: null,
            parseOptions,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(
            seed.InputCompilation,
            out _,
            out _,
            TestContext.Current.CancellationToken);
        var firstResult = driver.GetRunResult();
        var unrelatedTree = seed.InputCompilation.SyntaxTrees.Single(static tree => tree.FilePath == "Source1.cs");
        var changedTree = CSharpSyntaxTree.ParseText(
            UnrelatedSyntaxAfter,
            parseOptions,
            unrelatedTree.FilePath,
            cancellationToken: TestContext.Current.CancellationToken);
        var changedCompilation = seed.InputCompilation.ReplaceSyntaxTree(unrelatedTree, changedTree);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            changedCompilation,
            out _,
            out _,
            TestContext.Current.CancellationToken);
        var secondResult = driver.GetRunResult();

        GetGeneratedSources(secondResult).Should().BeEquivalentTo(GetGeneratedSources(firstResult));
        AssertCachedOrUnchanged(secondResult.Results.Single().TrackedSteps[analysisStepName]);
        AssertCachedOrUnchanged(secondResult.Results.Single().TrackedSteps[collectionStepName]);
        secondResult.Results.Single().TrackedOutputSteps.Values
            .SelectMany(static steps => steps)
            .Should().NotBeEmpty()
            .And.OnlyContain(static step => step.Outputs.All(static output =>
                IsCachedOrUnchanged(output.Reason)));
    }

    private static void AssertConfigurationEditRegeneratesRoutes(IIncrementalGenerator generator)
    {
        var seed = GeneratorTestHarness.Run(
            new[] { AssemblyConfigurationBefore, ConfigurationIndependentEndpointScenario },
            generator);
        var parseOptions = (CSharpParseOptions)seed.InputCompilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            seed.InputCompilation,
            out _,
            out _,
            TestContext.Current.CancellationToken);
        var firstResult = driver.GetRunResult();
        var configurationTree = seed.InputCompilation.SyntaxTrees.Single(static tree =>
            tree.FilePath == "Source0.cs");
        var changedConfigurationTree = CSharpSyntaxTree.ParseText(
            AssemblyConfigurationAfter,
            parseOptions,
            configurationTree.FilePath,
            cancellationToken: TestContext.Current.CancellationToken);
        var changedCompilation = seed.InputCompilation.ReplaceSyntaxTree(
            configurationTree,
            changedConfigurationTree);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            changedCompilation,
            out var outputCompilation,
            out _,
            TestContext.Current.CancellationToken);
        var secondResult = driver.GetRunResult();

        outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        GetGeneratedSources(secondResult).Should().NotBeEquivalentTo(GetGeneratedSources(firstResult));
        GetGeneratedSources(secondResult).Values.Should().Contain(source =>
            source.Contains("api/v2", StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, string> GetGeneratedSources(GeneratorDriverRunResult result)
    {
        return result.Results.Single().GeneratedSources.ToDictionary(
            static source => source.HintName,
            static source => source.SourceText.ToString(),
            StringComparer.Ordinal);
    }

    private static void AssertCachedOrUnchanged(
        IEnumerable<IncrementalGeneratorRunStep> steps)
    {
        steps.Should().NotBeEmpty()
            .And.OnlyContain(static step => step.Outputs.All(static output =>
                IsCachedOrUnchanged(output.Reason)));
    }

    private static bool IsCachedOrUnchanged(IncrementalStepRunReason reason)
    {
        return reason == IncrementalStepRunReason.Cached ||
               reason == IncrementalStepRunReason.Unchanged;
    }

    private sealed class InMemoryXmlDocumentationProvider : DocumentationProvider
    {
        private readonly IReadOnlyDictionary<string, string> _comments;
        private readonly string _identity;

        public InMemoryXmlDocumentationProvider(byte[] xmlBytes)
        {
            _identity = Convert.ToBase64String(xmlBytes);
            using var stream = new MemoryStream(xmlBytes, writable: false);
            var document = XDocument.Load(stream);
            _comments = document.Descendants("member")
                .Where(static member => member.Attribute("name") is not null)
                .ToDictionary(
                    static member => member.Attribute("name")!.Value,
                    static member => member.ToString(SaveOptions.DisableFormatting),
                    StringComparer.Ordinal);
        }

        protected override string? GetDocumentationForSymbol(
            string documentationMemberID,
            CultureInfo preferredCulture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _comments.TryGetValue(documentationMemberID, out var comment)
                ? comment
                : string.Empty;
        }

        public override bool Equals(object? obj)
        {
            return obj is InMemoryXmlDocumentationProvider other &&
                   _identity == other._identity;
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(_identity);
        }
    }

    private const string IncrementalEndpointScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http)]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order without depending on unrelated source.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id:int}")]
            public sealed record QueryGetOrder(int Id) : IResultRequest<string>;
        }

        namespace Scenario.Application
        {
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            public sealed class QueryHandlerGetOrder : ApplicationService<QueryGetOrder, string>
            {
                public override Task<Res<string>> Handle(
                    QueryGetOrder request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string AssemblyConfigurationBefore = """
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http)]
        """;

    private const string AssemblyConfigurationAfter = """
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig(
            "api/v2",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http)]
        """;

    private const string ConfigurationIndependentEndpointScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order from a separately configured assembly.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id:int}")]
            public sealed record QueryGetConfiguredOrder(int Id) : IResultRequest<string>;
        }

        namespace Scenario.Application
        {
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            public sealed class QueryHandlerGetConfiguredOrder
                : ApplicationService<QueryGetConfiguredOrder, string>
            {
                public override Task<Res<string>> Handle(
                    QueryGetConfiguredOrder request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string ProtocolMetadataScenario = """
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v4", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Loads the exact published order summary from Protocol XML.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryGetPublishedOrder(long Id) : IResultRequest<string>;
        }
        """;

    private const string ServiceCompilationScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Scenario.PublishedLanguages.DomainOrdering.Requests;

        namespace Scenario.Service;

        public sealed class QueryHandlerGetPublishedOrder
            : ApplicationService<QueryGetPublishedOrder, string>
        {
            public override Task<Res<string>> Handle(
                QueryGetPublishedOrder request,
                CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string UnrelatedSyntaxBefore = """
        namespace Scenario.Unrelated;

        public sealed class Marker
        {
            public int Value => 1;
        }
        """;

    private const string UnrelatedSyntaxAfter = """
        namespace Scenario.Unrelated;

        public sealed class Marker
        {
            public int Value => 2;
        }
        """;

    private const string PositionalEnvelopeScenario = """
        using System.Dynamic;
        using Monica.Core.Mediator;
        using Monica.Core.Results;
        using Monica.Core.Results.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http)]

        namespace Scenario.Results
        {
            public sealed record PositionalEnvelope<T>(T Data) : IRemoteResultEnvelope<PositionalEnvelope<T>>
            {
                public string? Message { get; set; }
                public ResStatus Status { get; set; }
                public ExpandoObject? Metadata { get; set; }

                public static PositionalEnvelope<T> CreateRemoteFailure(ResStatus status, string message) => new(default(T)!)
                {
                    Status = status,
                    Message = message
                };
            }
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets an order in a constructor-only result envelope.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryGetOrder(long Id)
                : IRequest<global::Scenario.Results.PositionalEnvelope<string>>;
        }
        """;

    private const string InvalidPublishedEnvelopeScenario = """
        using Monica.Core.Mediator;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Uses an invalid scalar API result.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/count")]
            public sealed record QueryCountOrders : IRequest<int>;
        }
        """;

    private const string SemanticallyDuplicateRouteScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Gets an order by identifier.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "Orders/{Id:int}")]
        public sealed record QueryGetOrder(int Id) : IResultRequest<string>;

        /// <summary>Gets an order by number.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{OrderNumber:int}")]
        public sealed record QueryFindOrder(int OrderNumber) : IResultRequest<string>;

        public sealed class QueryHandlerGetOrder : ApplicationService<QueryGetOrder, string>
        {
            public override Task<Res<string>> Handle(QueryGetOrder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class QueryHandlerFindOrder : ApplicationService<QueryFindOrder, string>
        {
            public override Task<Res<string>> Handle(QueryFindOrder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string DistinctRouteShapeScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Gets an integer order identifier.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id:int}")]
        public sealed record QueryIntegerOrder(int Id) : IResultRequest<string>;

        /// <summary>Gets a GUID order identifier.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id:guid}")]
        public sealed record QueryGuidOrder(Guid Id) : IResultRequest<string>;

        /// <summary>Gets an optional order identifier.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id?}")]
        public sealed record QueryOptionalOrder(int? Id) : IResultRequest<string>;

        /// <summary>Gets an order catch-all path.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{*Path}")]
        public sealed record QueryOrderPath(string Path) : IResultRequest<string>;

        /// <summary>Gets an order using a default region.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/region/{Region=global}")]
        public sealed record QueryOrderRegion(string? Region) : IResultRequest<string>;

        public sealed class QueryHandlerIntegerOrder : ApplicationService<QueryIntegerOrder, string>
        {
            public override Task<Res<string>> Handle(QueryIntegerOrder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class QueryHandlerGuidOrder : ApplicationService<QueryGuidOrder, string>
        {
            public override Task<Res<string>> Handle(QueryGuidOrder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class QueryHandlerOptionalOrder : ApplicationService<QueryOptionalOrder, string>
        {
            public override Task<Res<string>> Handle(QueryOptionalOrder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class QueryHandlerOrderPath : ApplicationService<QueryOrderPath, string>
        {
            public override Task<Res<string>> Handle(QueryOrderPath request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class QueryHandlerOrderRegion : ApplicationService<QueryOrderRegion, string>
        {
            public override Task<Res<string>> Handle(QueryOrderRegion request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string InvalidConfiguredBaseScenario = """
        using System.Net.Http;
        using Monica.DependencyInjection.Abstractions;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;
        using Monica.Invocation.Abstractions;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http,
            HttpClientBaseType = typeof(Scenario.Transport.InvalidHttpApi))]

        namespace Scenario.Transport
        {
            public $TYPE_MODIFIER$ class InvalidHttpApi($CONSTRUCTOR_PARAMETERS$)
                : HttpApiClient(serviceProvider, httpClient);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order through an invalid custom transport.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryGetOrder(long Id) : IResultRequest<string>;
        }
        """;

    private const string GenericConfiguredBaseScenario = """
        using System.Net.Http;
        using Monica.DependencyInjection.Abstractions;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;
        using Monica.Invocation.Abstractions;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http,
            HttpClientBaseType = typeof(Scenario.Transport.GenericHttpApi<>))]

        namespace Scenario.Transport
        {
            public abstract class GenericHttpApi<T>(
                ICachedServiceProvider serviceProvider,
                HttpClient httpClient)
                : HttpApiClient(serviceProvider, httpClient);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order through a generic custom transport.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryGetOrder(long Id) : IResultRequest<string>;
        }
        """;

    private const string FileLocalConfiguredBaseScenario = """
        using System.Net.Http;
        using Monica.DependencyInjection.Abstractions;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;
        using Monica.Invocation.Abstractions;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http,
            HttpClientBaseType = typeof(Scenario.Transport.FileLocalHttpApi))]

        namespace Scenario.Transport
        {
            file abstract class FileLocalHttpApi(
                ICachedServiceProvider serviceProvider,
                HttpClient httpClient)
                : HttpApiClient(serviceProvider, httpClient);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order through a file-local custom transport.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryGetOrder(long Id) : IResultRequest<string>;
        }
        """;
}
