using AwesomeAssertions;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class GeneratorContractValidationTests
{
    [Theory]
    [MemberData(nameof(InvalidContractScenarios))]
    public void Run_WhenContractBoundaryIsInvalid_ShouldReportSpecificDiagnostic(
        string source,
        string expectedDiagnosticId)
    {
        var run = GeneratorTestHarness.Run(
            source,
            new HttpApiControllerSourceGenerator(),
            new ApiClientSourceGenerator());

        run.RunResult.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Should().Contain(expectedDiagnosticId);
    }

    public static TheoryData<string, string> InvalidContractScenarios => new()
    {
        { NestedPublishedRequestNamespace, "AC1005" },
        { InvalidRoutePrefix, "AC1004" },
        { PublishedEnvelopeWithoutRemoteFactory, "AC1017" },
        { ReadOnlyPocoRouteProperty, "AC1018" },
        { InvalidMethodSuffix, "AC1016" }
    };

    private const string InvalidMethodSuffix = """
        using Monica.Application.Abstractions;
        using Monica.Application.Annotations;

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests;

        /// <summary>Uses a type suffix that cannot become a method name.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders")]
        public sealed record Query123Orders : IResultRequest<string>;
        """;

    private const string NestedPublishedRequestNamespace = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests.Internal
        {
            /// <summary>Attempts to publish a request below the strict Requests namespace.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders")]
            public sealed record QueryInternalOrders : IResultRequest<string>;

            public sealed class QueryHandlerInternalOrders
                : ApplicationService<QueryInternalOrders, string>
            {
                public override Task<Res<string>> Handle(
                    QueryInternalOrders request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string InvalidRoutePrefix = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1?debug=true", DomainName = "Ordering")]

        /// <summary>Uses an invalid route prefix.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders")]
        public sealed record QueryInvalidPrefix : IResultRequest<string>;

        public sealed class QueryHandlerInvalidPrefix
            : ApplicationService<QueryInvalidPrefix, string>
        {
            public override Task<Res<string>> Handle(
                QueryInvalidPrefix request,
                CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string PublishedEnvelopeWithoutRemoteFactory = """
        using System.Dynamic;
        using Monica.Core.Mediator;
        using Monica.Core.Results;
        using Monica.Core.Results.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.Results
        {
            public sealed record CustomEnvelope(string Data) : IResultEnvelope
            {
                public string? Message { get; set; }
                public ResStatus Status { get; set; }
                public ExpandoObject? Metadata { get; set; }
            }
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Uses a custom envelope without a remote failure factory.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders")]
            public sealed record QueryOrders : IRequest<global::Scenario.Results.CustomEnvelope>;
        }
        """;

    private const string ReadOnlyPocoRouteProperty = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Exposes a route property that model binding cannot update.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
        public sealed class QueryReadOnlyOrder : IResultRequest<string>
        {
            public QueryReadOnlyOrder(long value)
            {
                Id = value;
            }

            public long Id { get; }
        }

        public sealed class QueryHandlerReadOnlyOrder
            : ApplicationService<QueryReadOnlyOrder, string>
        {
            public override Task<Res<string>> Handle(
                QueryReadOnlyOrder request,
                CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;
}
