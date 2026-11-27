using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Monica.Generators.AutoController;
using Monica.Generators.AutoController.Diagnostics;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class RequestHandlerDependencyAnalyzerTests
{
    [Fact]
    public async Task Analyze_WhenConsumingCommandsAndQueries_ShouldRejectHandlerDependenciesAndAllowGeneratedApis()
    {
        const string contracts = """
            using Monica.Application.Abstractions;
            using Monica.Application.Annotations;

            namespace Scenario.PublishedLanguages.DomainOrdering.Requests;

            /// <summary>Approves an order.</summary>
            [ApiEndpoint(ApiHttpMethod.Post, "approve")]
            public sealed record CommandApprove : ICommand;

            /// <summary>Counts orders.</summary>
            [ApiEndpoint(ApiHttpMethod.Get, "count")]
            public sealed record QueryCount : IQuery<int>;
            """;
        const string consumers = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Components;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Extensions.DependencyInjection;
            using Monica.Application.Abstractions;
            using Monica.Core.Mediator;
            using Monica.Core.Results;
            using Scenario.PublishedLanguages.DomainOrdering.Contracts;
            using Scenario.PublishedLanguages.DomainOrdering.Requests;

            sealed class Approver : ApplicationRequestHandler<CommandApprove, Result>
            {
                public override Task<Result> Handle(CommandApprove request, CancellationToken token) => throw new NotImplementedException();
            }
            sealed class Counter : IRequestHandler<QueryCount, Result<int>>
            {
                public Task<Result<int>> Handle(QueryCount request, CancellationToken token) => throw new NotImplementedException();
            }

            sealed class Consumer(Approver first, IEnumerable<Counter> counters, IOrderingCommandApi commands,
                IOrderingQueryApi queries, IMediator mediator, IRequestHandler<QueryCount, Result<int>> queryHandler)
            {
                public Consumer(Counter second) : this(null!, null!, null!, null!, null!, null!) { }
                [Inject] public Counter Property { get; set; } = null!;
                public void Run([FromServices] Approver actionService, IServiceProvider services)
                {
                    _ = services.GetRequiredService<Approver>();
                    _ = services.GetService(typeof(Counter));
                    _ = services.GetKeyedServices<Counter>("blue");
                    _ = services.GetRequiredKeyedService(serviceKey: typeof(Approver), serviceType: typeof(IOrderingQueryApi));
                    _ = services.GetRequiredService<IOrderingCommandApi>();
                    _ = services.GetRequiredService<IOrderingQueryApi>();
                }
            }
            """;

        var run = GeneratorTestHarness.Run([contracts, consumers], new ApiClientSourceGenerator());
        Assert.Empty(run.OutputErrors);
        var diagnostics = await run.OutputCompilation.WithAnalyzers([new RequestHandlerDependencyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("AC1029", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        });
        var locations = diagnostics.Select(diagnostic => diagnostic.Location.SourceTree!.GetText()
            .ToString(diagnostic.Location.SourceSpan)).Order(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "first", "counters", "second", "Property", "actionService", "queryHandler",
            "services.GetRequiredService<Approver>()", "services.GetService(typeof(Counter))",
            """services.GetKeyedServices<Counter>("blue")"""
        }.Order(StringComparer.Ordinal), locations);
    }
}
