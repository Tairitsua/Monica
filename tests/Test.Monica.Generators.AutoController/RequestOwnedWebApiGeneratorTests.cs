using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class RequestOwnedWebApiGeneratorTests
{
    [Fact]
    public void Run_WhenExplicitHttpMappingHasNoPublicationConfig_ShouldGenerateOnlyStableContracts()
    {
        var run = RunGenerators(MissingConfigurationScenario);
        run.OutputErrors.Should().BeEmpty();
        run.GeneratedSources.Keys.Should().BeEquivalentTo("IOrderingQueryApi.g.cs");
    }

    [Fact]
    public void Run_WhenPublishedContractsAreReferencedInTheSameCompilation_ShouldGenerateCompilableControllersAndClients()
    {
        var run = RunGenerators(PublishedOrderingScenario);

        run.GeneratedSources.Keys.Should().BeEquivalentTo(
            "HttpEndpointCommandOrdering.g.cs",
            "HttpEndpointQueryOrdering.g.cs",
            "IOrderingCommandApi.g.cs",
            "IOrderingQueryApi.g.cs",
            "HttpOrderingCommandApiClient.g.cs",
            "HttpOrderingQueryApiClient.g.cs",
            "LocalOrderingCommandApiClient.g.cs",
            "LocalOrderingQueryApiClient.g.cs");
        run.GeneratedSources["HttpEndpointCommandOrdering.g.cs"]
            .Should().Contain("Monica.Core.Execution.Mvc.MediatedController");
        run.OutputErrors.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenPublishedEndpointUsesBody_ShouldDelegateSerializationToTransportBase()
    {
        var run = RunGenerators(PublishedOrderingScenario);

        run.OutputErrors.Should().BeEmpty();
        var httpClient = run.GeneratedSources["HttpOrderingCommandApiClient.g.cs"];
        httpClient.Should().Contain("CreateHttpRequest(request,");
        httpClient.Should().Contain("includeBody: true");
        httpClient.Should().NotContain("HttpApiRequestExtensions.BuildApiRequestUri");
        httpClient.Should().NotContain("JsonContent.Create");
        httpClient.Should().Contain("ExecuteAsync<").And.Contain("RemoteCallContext(serviceDescriptor");
    }

    [Fact]
    public void Run_WhenPublishedAndLocalRequestsShareADomain_ShouldExcludeLocalRequestFromApiContract()
    {
        var run = RunGenerators(PublishedAndLocalScenario);

        run.OutputErrors.Should().BeEmpty();
        var controller = run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"];
        var apiContract = run.GeneratedSources["IOrderingQueryApi.g.cs"];

        controller.Should().Contain("published-orders");
        controller.Should().Contain("local-health");
        apiContract.Should().Contain("GetPublishedOrders");
        apiContract.Should().NotContain("GetLocalHealth");
    }

    [Fact]
    public void Run_WhenHttpRouteDiffersFromRequestName_ShouldDeriveMethodNameFromRequestAndPreserveMapping()
    {
        var run = RunGenerators(ExactEndpointScenario);

        run.OutputErrors.Should().BeEmpty();
        var controller = run.GeneratedSources["HttpEndpointQueryOrdering.g.cs"];
        var apiContract = run.GeneratedSources["IOrderingQueryApi.g.cs"];

        controller.Should().Contain("api/v2/Ordering");
        controller.Should().Contain("HttpGet");
        controller.Should().Contain("orders/{OrderId}");
        controller.Should().Contain(
            "ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Query)");
        controller.Should().Contain("OrderById(");
        controller.Should().Contain("Finds one published order by its identifier.");
        apiContract.Should().Contain("OrderById(");
        apiContract.Should().Contain("CancellationToken");
    }

    [Fact]
    public void Run_WhenEverySupportedVerbAndPlaceholderIsUsed_ShouldGenerateMatchingControllerActions()
    {
        var run = RunGenerators(AllHttpMethodsScenario);

        run.OutputErrors.Should().BeEmpty();
        var generatedControllers = string.Join(
            Environment.NewLine,
            run.GeneratedSources
                .Where(static source => source.Key.StartsWith("HttpEndpoint", StringComparison.Ordinal))
                .Select(static source => source.Value));

        generatedControllers.Should().Contain("HttpGet");
        generatedControllers.Should().Contain("HttpPost");
        generatedControllers.Should().Contain("HttpPut");
        generatedControllers.Should().Contain("HttpPatch");
        generatedControllers.Should().Contain("HttpDelete");
        generatedControllers.Should().Contain("items/{Id}");
        generatedControllers.Should().Contain(
            "ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Query)");
        generatedControllers.Should().Contain(
            "ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Body)");
    }

    [Fact]
    public void Run_WhenResultShapesAndDuplicateSimpleNamesAreUsed_ShouldGenerateUnambiguousCompilableContracts()
    {
        var run = RunGenerators(ResultShapeScenario);

        run.OutputErrors.Should().BeEmpty();
        var apiContract = run.GeneratedSources["IOrderingQueryApi.g.cs"];

        apiContract.Should().Contain("global::Monica.Core.Results.Res");
        apiContract.Should().Contain("global::Monica.Core.Results.ResPaged");
        apiContract.Should().Contain("global::Scenario.Results.CustomEnvelope");
        apiContract.Should().Contain("global::Scenario.Alpha.SharedDto");
        apiContract.Should().Contain("global::Scenario.Beta.SharedDto");
        apiContract.Should().Contain("global::System.Collections.Generic.Dictionary");
    }

    [Fact]
    public void Run_WhenTransportBaseTypesAreOverridden_ShouldUseTheConfiguredBases()
    {
        var run = RunGenerators(CustomTransportBaseScenario);

        run.OutputErrors.Should().BeEmpty();
        run.GeneratedSources["HttpOrderingQueryApiClient.g.cs"]
            .Should().Contain("global::Scenario.Transport.OurHttpApi");
        run.GeneratedSources["LocalOrderingQueryApiClient.g.cs"]
            .Should().Contain("global::Scenario.Transport.OurLocalApi");
    }

    [Fact]
    public void Run_WhenLocalEndpointUsesFormBinding_ShouldGenerateFormControllerWithoutPublishingApiOperation()
    {
        var run = RunGenerators(LocalFormScenario);

        run.OutputErrors.Should().BeEmpty();
        var controller = run.GeneratedSources["HttpEndpointCommandOrdering.g.cs"];
        controller.Should().Contain(
            "ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Form)");
        controller.Should().Contain("local-upload");
        run.GeneratedSources.Keys.Should().NotContain("IOrderingCommandApi.g.cs");
    }

    [Fact]
    public void Run_WhenDeleteEndpointBindsCollectionsFromQuery_ShouldWarnButStillGenerate()
    {
        var run = RunGenerators(DeleteBulkQueryScenario);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics
            .Should()
            .ContainSingle(static diagnostic => diagnostic.Id == "AC1019");
        var controller = run.GeneratedSources["HttpEndpointCommandOrdering.g.cs"];
        controller.Should().Contain("HttpDelete");
        controller.Should().Contain("ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Query)");
    }

    [Fact]
    public void Run_WhenDeleteEndpointDeclaresBodyBinding_ShouldNotWarn()
    {
        var run = RunGenerators(DeleteBulkBodyScenario);

        run.OutputErrors.Should().BeEmpty();
        run.RunResult.Diagnostics
            .Select(static diagnostic => diagnostic.Id)
            .Should()
            .NotContain("AC1019");
        var controller = run.GeneratedSources["HttpEndpointCommandOrdering.g.cs"];
        controller.Should().Contain("ApiEndpointRequestAttribute(global::Monica.Application.Annotations.ApiRequestBinding.Body)");
    }

    [Theory]
    [MemberData(nameof(InvalidScenarios))]
    public void Run_WhenContractIsInvalid_ShouldReportTheExpectedDiagnostic(
        string source,
        string expectedDiagnosticId)
    {
        var run = RunGenerators(source);

        run.RunResult.Diagnostics
            .Select(static diagnostic => diagnostic.Id)
            .Should()
            .Contain(expectedDiagnosticId);
    }

    [Fact]
    public void Run_WhenInputsAreUnchanged_ShouldProduceIdenticalIncrementalOutput()
    {
        var firstRun = RunGenerators(PublishedOrderingScenario);
        var secondRun = firstRun.RunAgain();

        secondRun.GeneratedSources.Should().BeEquivalentTo(firstRun.GeneratedSources);
        secondRun.RunResult.Diagnostics
            .Select(ToDiagnosticSnapshot)
            .Should()
            .Equal(firstRun.RunResult.Diagnostics.Select(ToDiagnosticSnapshot));
        secondRun.OutputErrors.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenProjectDirectoryIsProvided_ShouldReturnSourcesWithoutWritingFiles()
    {
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"monica-generator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);

        try
        {
            var run = GeneratorTestHarness.RunWithProjectDirectory(
                PublishedOrderingScenario,
                projectDirectory,
                new HttpApiControllerSourceGenerator(),
                new ApiClientSourceGenerator());

            run.OutputErrors.Should().BeEmpty();
            Directory.EnumerateFileSystemEntries(projectDirectory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    public static TheoryData<string, string> InvalidScenarios => new()
    {
        { LegacyHandlerAttributeScenario, "AC1002" },
        { MissingPlaceholderPropertyScenario, "AC1012" },
        { InvalidRouteScenario.Replace("$ROUTE$", "orders?active=true", StringComparison.Ordinal), "AC1011" },
        { InvalidRouteScenario.Replace("$ROUTE$", "orders#active", StringComparison.Ordinal), "AC1011" },
        { InvalidRouteScenario.Replace("$ROUTE$", "orders/{Id?invalid}", StringComparison.Ordinal), "AC1011" },
        { PublishedFormScenario, "AC1013" },
        { MissingSummaryScenario, "AC1014" },
        { DuplicateOperationScenario, "AC1010" }
    };

    [Fact]
    public void Run_WhenApplicationServiceIsLocalOnly_ShouldCompileWithoutPublishingHttp()
    {
        var first = RunGenerators(MissingEndpointScenario);
        var second = RunGenerators(MissingEndpointScenario);
        first.RunResult.Diagnostics.Should().BeEmpty();
        first.OutputErrors.Should().BeEmpty();
        first.GeneratedSources.Should().BeEmpty();
        second.GeneratedSources.Should().BeEquivalentTo(first.GeneratedSources);
        second.OutputErrors.Should().BeEmpty();
    }

    private static GeneratorTestHarness.GeneratorTestRun RunGenerators(string source)
    {
        return GeneratorTestHarness.Run(
            source,
            new HttpApiControllerSourceGenerator(),
            new ApiClientSourceGenerator());
    }

    private static string ToDiagnosticSnapshot(Diagnostic diagnostic)
    {
        return $"{diagnostic.Id}|{diagnostic.Severity}|{diagnostic.GetMessage()}";
    }

    private const string PublishedOrderingScenario = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;
        using Scenario.PublishedLanguages.DomainOrdering.Contracts;
        using Scenario.PublishedLanguages.DomainOrdering.Models;
        using Scenario.PublishedLanguages.DomainOrdering.Requests;

        [assembly: WebApiGenerationConfig(
            "api/v1",
            DomainName = "Ordering",
            ApiClientTargets = ApiClientGenerationTargets.Http | ApiClientGenerationTargets.Local)]

        namespace Scenario.PublishedLanguages.DomainOrdering.Models
        {
            public sealed record OrderDto(Guid Id);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets all published orders.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders", Binding = ApiRequestBinding.Query)]
            public sealed record QueryGetOrders : IResultRequest<IReadOnlyList<OrderDto>>;

            /// <summary>Creates one published order.</summary>
            [ApiEndpoint(ApiHttpMethod.Post, "orders", Binding = ApiRequestBinding.Body)]
            public sealed record CommandCreateOrder(string Number) : IResultRequest<OrderDto>;
        }

        namespace Scenario.Application
        {
            public sealed class QueryHandlerGetOrders
                : ApplicationService<QueryGetOrders, IReadOnlyList<OrderDto>>
            {
                public override Task<Res<IReadOnlyList<OrderDto>>> Handle(
                    QueryGetOrders request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class CommandHandlerCreateOrder
                : ApplicationService<CommandCreateOrder, OrderDto>
            {
                public override Task<Res<OrderDto>> Handle(
                    CommandCreateOrder request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }

        namespace Scenario.Consumer
        {
            public sealed class SameCompilationConsumer(
                IOrderingQueryApi queries,
                IOrderingCommandApi commands)
            {
                public IOrderingQueryApi Queries { get; } = queries;
                public IOrderingCommandApi Commands { get; } = commands;
            }
        }
        """;

    private const string PublishedAndLocalScenario = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets orders through the published contract.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "published-orders")]
            public sealed record QueryGetPublishedOrders : IResultRequest<IReadOnlyList<string>>;
        }

        namespace Scenario.Local.Requests
        {
            /// <summary>Gets process-local health details.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "local-health")]
            public sealed record QueryGetLocalHealth : IResultRequest<string>;
        }

        namespace Scenario.Application
        {
            using Scenario.Local.Requests;
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            public sealed class QueryHandlerPublishedOrders
                : ApplicationService<QueryGetPublishedOrders, IReadOnlyList<string>>
            {
                public override Task<Res<IReadOnlyList<string>>> Handle(
                    QueryGetPublishedOrders request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class QueryHandlerLocalHealth
                : ApplicationService<QueryGetLocalHealth, string>
            {
                public override Task<Res<string>> Handle(
                    QueryGetLocalHealth request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string ExactEndpointScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v2", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Models
        {
            public sealed record OrderDto(long Id);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            using Scenario.PublishedLanguages.DomainOrdering.Models;

            /// <summary>Finds one published order by its identifier.</summary>
            [ApiEndpoint(
                ApiHttpMethod.Get,
                "orders/{OrderId}",
                Binding = ApiRequestBinding.Query)]
            public sealed record QueryOrderById(long OrderId) : IResultRequest<OrderDto>;
        }

        namespace Scenario.Application
        {
            using Scenario.PublishedLanguages.DomainOrdering.Models;
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            public sealed class QueryHandlerOrderById : ApplicationService<QueryOrderById, OrderDto>
            {
                public override Task<Res<OrderDto>> Handle(
                    QueryOrderById request,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string AllHttpMethodsScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Inventory")]

        namespace Scenario.Local.Requests
        {
            /// <summary>Gets an item.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "items/{Id}")]
            public sealed record QueryGetItem(long Id) : IResultRequest<string>;

            /// <summary>Creates an item.</summary>
            [ApiEndpoint(ApiHttpMethod.Post, "items/{Id}")]
            public sealed record CommandCreateItem(long Id) : IResultRequest;

            /// <summary>Replaces an item.</summary>
            [ApiEndpoint(ApiHttpMethod.Put, "items/{Id}")]
            public sealed record CommandReplaceItem(long Id) : IResultRequest;

            /// <summary>Updates an item.</summary>
            [ApiEndpoint(ApiHttpMethod.Patch, "items/{Id}")]
            public sealed record CommandUpdateItem(long Id) : IResultRequest;

            /// <summary>Deletes an item.</summary>
            [ApiEndpoint(ApiHttpMethod.Delete, "items/{Id}")]
            public sealed record CommandDeleteItem(long Id) : IResultRequest;
        }

        namespace Scenario.Application
        {
            using Scenario.Local.Requests;

            public sealed class QueryHandlerGetItem : ApplicationService<QueryGetItem, string>
            {
                public override Task<Res<string>> Handle(QueryGetItem request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class CommandHandlerCreateItem : ApplicationService<CommandCreateItem>
            {
                public override Task<Res> Handle(CommandCreateItem request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class CommandHandlerReplaceItem : ApplicationService<CommandReplaceItem>
            {
                public override Task<Res> Handle(CommandReplaceItem request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class CommandHandlerUpdateItem : ApplicationService<CommandUpdateItem>
            {
                public override Task<Res> Handle(CommandUpdateItem request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class CommandHandlerDeleteItem : ApplicationService<CommandDeleteItem>
            {
                public override Task<Res> Handle(CommandDeleteItem request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string ResultShapeScenario = """
        using System;
        using System.Collections.Generic;
        using System.Dynamic;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Mediator;
        using Monica.Core.Results;
        using Monica.Core.Results.Abstractions;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.Alpha
        {
            public sealed record SharedDto(long Id);
        }

        namespace Scenario.Beta
        {
            public sealed record SharedDto(string Code);
        }

        namespace Scenario.Results
        {
            public sealed record CustomEnvelope<T>(T Value) : IRemoteResultEnvelope<CustomEnvelope<T>>
            {
                public string? Message { get; set; }
                public ResStatus Status { get; set; }
                public ExpandoObject? Metadata { get; set; }

                public static CustomEnvelope<T> CreateRemoteFailure(ResStatus status, string message) => new(default(T)!)
                {
                    Status = status,
                    Message = message
                };
            }
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Returns a result without data.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "no-content")]
            public sealed record QueryNoContent : IResultRequest;

            /// <summary>Returns a typed result.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "typed")]
            public sealed record QueryTyped : IResultRequest<global::Scenario.Alpha.SharedDto>;

            /// <summary>Returns a paged result.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "paged")]
            public sealed record QueryPaged : IRequest<ResPaged<global::Scenario.Alpha.SharedDto>>;

            /// <summary>Returns a custom result envelope.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "custom")]
            public sealed record QueryCustom : IRequest<global::Scenario.Results.CustomEnvelope<global::Scenario.Beta.SharedDto>>;

            /// <summary>Returns a nested generic result.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "nested")]
            public sealed record QueryNested
                : IResultRequest<Dictionary<string, IReadOnlyList<global::Scenario.Alpha.SharedDto>>>;
        }

        namespace Scenario.Application
        {
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            public sealed class QueryHandlerNoContent : ApplicationService<QueryNoContent>
            {
                public override Task<Res> Handle(QueryNoContent request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class QueryHandlerTyped
                : ApplicationService<QueryTyped, global::Scenario.Alpha.SharedDto>
            {
                public override Task<Res<global::Scenario.Alpha.SharedDto>> Handle(QueryTyped request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class QueryHandlerPaged
                : CustomApplicationService<QueryPaged, ResPaged<global::Scenario.Alpha.SharedDto>>
            {
                public override Task<ResPaged<global::Scenario.Alpha.SharedDto>> Handle(QueryPaged request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class QueryHandlerCustom
                : CustomApplicationService<QueryCustom, global::Scenario.Results.CustomEnvelope<global::Scenario.Beta.SharedDto>>
            {
                public override Task<global::Scenario.Results.CustomEnvelope<global::Scenario.Beta.SharedDto>> Handle(QueryCustom request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public sealed class QueryHandlerNested
                : ApplicationService<QueryNested, Dictionary<string, IReadOnlyList<global::Scenario.Alpha.SharedDto>>>
            {
                public override Task<Res<Dictionary<string, IReadOnlyList<global::Scenario.Alpha.SharedDto>>>> Handle(QueryNested request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string LocalFormScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.Local.Requests
        {
            /// <summary>Uploads one local form payload.</summary>
            [ApiEndpoint(ApiHttpMethod.Post, "local-upload", Binding = ApiRequestBinding.Form)]
            public sealed record CommandUpload(string Name) : IResultRequest;
        }

        namespace Scenario.Application
        {
            using Scenario.Local.Requests;

            public sealed class CommandHandlerUpload : ApplicationService<CommandUpload>
            {
                public override Task<Res> Handle(CommandUpload request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string DeleteBulkQueryScenario = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.Local.Requests
        {
            /// <summary>Deletes items in bulk.</summary>
            [ApiEndpoint(ApiHttpMethod.Delete, "items/bulk")]
            public sealed record CommandDeleteItems(List<string> Ids) : IResultRequest;
        }

        namespace Scenario.Application
        {
            using Scenario.Local.Requests;

            public sealed class CommandHandlerDeleteItems : ApplicationService<CommandDeleteItems>
            {
                public override Task<Res> Handle(CommandDeleteItems request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string DeleteBulkBodyScenario = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.Local.Requests
        {
            /// <summary>Deletes items in bulk.</summary>
            [ApiEndpoint(ApiHttpMethod.Delete, "items/bulk", Binding = ApiRequestBinding.Body)]
            public sealed record CommandDeleteItems(List<string> Ids) : IResultRequest;
        }

        namespace Scenario.Application
        {
            using Scenario.Local.Requests;

            public sealed class CommandHandlerDeleteItems : ApplicationService<CommandDeleteItems>
            {
                public override Task<Res> Handle(CommandDeleteItems request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
        }
        """;

    private const string CustomTransportBaseScenario = """
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
            ApiClientTargets = ApiClientGenerationTargets.Http | ApiClientGenerationTargets.Local,
            HttpClientBaseType = typeof(Scenario.Transport.OurHttpApi),
            LocalClientBaseType = typeof(Scenario.Transport.OurLocalApi))]

        namespace Scenario.Transport
        {
            public abstract class OurHttpApi(
                ICachedServiceProvider serviceProvider,
                HttpClient httpClient)
                : HttpApiClient(serviceProvider, httpClient);

            public abstract class OurLocalApi(ICachedServiceProvider serviceProvider)
                : LocalApiClient(serviceProvider);
        }

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Gets one order through custom transports.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "orders/{Id}")]
            public sealed record QueryOrder(long Id) : IResultRequest<string>;
        }
        """;

    private const string MissingEndpointScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Has no endpoint declaration.</summary>
        public sealed record QueryMissingEndpoint : IResultRequest<string>;

        public sealed class QueryHandlerMissingEndpoint : ApplicationService<QueryMissingEndpoint, string>
        {
            public override Task<Res<string>> Handle(QueryMissingEndpoint request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string LegacyHandlerAttributeScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Owns its endpoint contract.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders")]
        public sealed record QueryLegacyHandler : IResultRequest<string>;

        public sealed class QueryHandlerLegacyHandler : ApplicationService<QueryLegacyHandler, string>
        {
            [HttpGet("legacy")]
            public override Task<Res<string>> Handle(QueryLegacyHandler request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string MissingConfigurationScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests;

        /// <summary>Has an endpoint but no assembly configuration.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders")]
        public sealed record QueryMissingConfiguration : IResultRequest<string>;

        public sealed class QueryHandlerMissingConfiguration : ApplicationService<QueryMissingConfiguration, string>
        {
            public override Task<Res<string>> Handle(QueryMissingConfiguration request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string MissingPlaceholderPropertyScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Uses an invalid route placeholder.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "orders/{MissingId}")]
        public sealed record QueryMissingPlaceholder(long Id) : IResultRequest<string>;

        public sealed class QueryHandlerMissingPlaceholder : ApplicationService<QueryMissingPlaceholder, string>
        {
            public override Task<Res<string>> Handle(QueryMissingPlaceholder request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string InvalidRouteScenario = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        /// <summary>Uses an invalid route.</summary>
        [ApiEndpoint(ApiHttpMethod.Get, "$ROUTE$")]
        public sealed record QueryInvalidRoute(long? Id) : IResultRequest<string>;

        public sealed class QueryHandlerInvalidRoute : ApplicationService<QueryInvalidRoute, string>
        {
            public override Task<Res<string>> Handle(QueryInvalidRoute request, CancellationToken cancellationToken) => throw new NotImplementedException();
        }
        """;

    private const string PublishedFormScenario = """
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Attempts to publish form binding.</summary>
            [ApiEndpoint(ApiHttpMethod.Post, "upload", Binding = ApiRequestBinding.Form)]
            public sealed record CommandPublishedForm(string Name) : IResultRequest;
        }
        """;

    private const string MissingSummaryScenario = """
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace Scenario.PublishedLanguages.DomainOrdering.Requests
        {
            [ApiEndpoint(ApiHttpMethod.Get, "orders")]
            public sealed record QueryMissingSummary : IResultRequest<string>;
        }
        """;

    private const string DuplicateOperationScenario = """
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.Annotations;
        using Monica.Application.Annotations;

        [assembly: WebApiGenerationConfig("api/v1", DomainName = "Ordering")]

        namespace First.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>First duplicate operation.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "first")]
            public sealed record QueryFind : IResultRequest<string>;
        }
        namespace Second.PublishedLanguages.DomainOrdering.Requests
        {
            /// <summary>Second duplicate operation.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "second")]
            public sealed record QueryFind : IResultRequest<string>;
        }
        """;
}
